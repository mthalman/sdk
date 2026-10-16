// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Build.Utilities;
using Microsoft.DotNet.Build.Tasks;

namespace Microsoft.CoreSdkTasks.Tests;

/// <summary>
/// Verifies metadata write stability, RID-chain invalidation, and recovery by invoking
/// the production SDK layout targets with isolated package inputs and output directories.
/// </summary>
[TestClass]
// In-process MSBuild saves/restores process-wide state shared with other layout tests.
[DoNotParallelize]
public class SdkMetadataTests : SdkTest
{
    private const string MetadataTargets = "GenerateBundledVersions;PublishVersionFile;CopyKnownWorkloadManifestFile";
    private const string ChainTarget = "GenerateSdkRuntimeIdentifierChain";
    private const string Graph = """
        {
          "runtimes": {
            "linux-x64": { "#import": [ "linux" ] },
            "linux": { "#import": [ "any" ] },
            "win-x64": { "#import": [ "win" ] },
            "win": { "#import": [ "any" ] },
            "any": {}
          }
        }
        """;

    /// <summary>
    /// Verifies repeated metadata generation preserves bytes, encodings, and timestamps
    /// while retaining the expected version properties, tool references, and manifest list.
    /// </summary>
    [TestMethod]
    public void MetadataPreservesContentsEncodingAndTimestampsOnWarmBuilds()
    {
        MetadataProject project = CreateProject();
        project.Build(MetadataTargets);
        Dictionary<string, byte[]> contents = project.MetadataFiles.ToDictionary(path => path, File.ReadAllBytes);
        Dictionary<string, DateTime> timestamps = AgeAndSnapshot(project.MetadataFiles);

        project.Build(MetadataTargets);
        project.Build(MetadataTargets);

        AssertContents(contents);
        AssertTimestamps(timestamps);
        foreach (string name in new[] { "productVersion.txt", "sdk-productVersion.txt", "productCommit-test.txt", "productCommit-test.json" })
        {
            File.ReadAllBytes(project.ShippingFile(name)).Should().OnlyContain(value => value < 128);
        }
        File.ReadAllText(project.ShippingFile("productVersion.txt")).Should().Be("11.0.100-test" + Environment.NewLine);
        ReadProperty(project.Output("Microsoft.NETCoreSdk.BundledVersions.props"), "BundledNETCoreAppTargetFramework").Should().Be("net11.0");
        ReadProperty(project.Output("Microsoft.NETCoreSdk.BundledMSBuildInformation.props"), "BundledMSBuildVersion").Should().Be("18.0.0");
        XDocument.Load(project.Output("Microsoft.NETCoreSdk.BundledCliTools.props")).Descendants("BundledDotNetCliToolReference")
            .Single().Attribute("Include")!.Value.Should().Be("Old.Test.Tool");
        File.ReadAllLines(project.Output("KnownWorkloadManifests.txt")).Should().Equal("test.manifest", "builtin.manifest", "samsung.net.sdk.tizen");
    }

    /// <summary>
    /// Verifies scalar and item changes produce the same metadata as a clean build,
    /// repeated inputs preserve timestamps, and reverting inputs restores the original bytes.
    /// </summary>
    [TestMethod]
    [DataRow("PackageVersion", "11.0.200-test")]
    [DataRow("BUILD_SOURCEVERSION", "edited-sdk-commit")]
    [DataRow("MicrosoftBuildVersion", "18.1.0-preview.1")]
    [DataRow("MicrosoftNETCoreAppRuntimePackageVersion", "11.0.1")]
    [DataRow("ObsoleteCliTool", "Another.Old.Tool")]
    [DataRow("ObsoleteCliTool", "")]
    [DataRow("ManifestId", "Another.Manifest")]
    [DataRow("ManifestId", "")]
    [DataRow("ManifestId", "Test.Manifest;Added.Manifest")]
    [DataRow("SourceRevisionId", "edited-revision")]
    public void MetadataTracksPropertyAndItemEditsAndReverts(string property, string value)
    {
        MetadataProject project = CreateProject();
        project.Build(MetadataTargets);
        Dictionary<string, byte[]> original = project.MetadataFiles.ToDictionary(path => path, File.ReadAllBytes);

        project.Build(MetadataTargets, new() { [property] = value });
        project.MetadataFiles.Any(path => !File.ReadAllBytes(path).SequenceEqual(original[path])).Should().BeTrue();
        MetadataProject clean = CreateProject();
        clean.Build(MetadataTargets, new() { [property] = value });
        foreach (string path in project.MetadataFiles)
        {
            string cleanPath = Path.Combine(clean.Root, Path.GetRelativePath(project.Root, path));
            File.ReadAllBytes(path).Should().Equal(File.ReadAllBytes(cleanPath), "incremental metadata must match a clean build");
        }
        Dictionary<string, DateTime> changedTimestamps = AgeAndSnapshot(project.MetadataFiles);
        project.Build(MetadataTargets, new() { [property] = value });
        AssertTimestamps(changedTimestamps);

        project.Build(MetadataTargets);
        AssertContents(original);
    }

    /// <summary>
    /// Verifies framework-list and minimum-MSBuild-version edits affect generated metadata,
    /// and each deleted output is repaired without rewriting unchanged sibling files.
    /// </summary>
    [TestMethod]
    public void MetadataTracksFileEditsAndRepairsEachMissingOutputWithoutRewritingSiblings()
    {
        MetadataProject project = CreateProject();
        project.Build(MetadataTargets);
        string minimumVersion = Path.Combine(project.Root, "minimumMSBuildVersion");
        File.WriteAllText(minimumVersion, "17.15.0");
        project.Build(MetadataTargets);
        ReadProperty(project.Output("Microsoft.NETCoreSdk.BundledMSBuildInformation.props"), "MinimumMSBuildVersion").Should().Be("17.15.0");
        File.WriteAllText(minimumVersion, "17.14.0");
        File.WriteAllText(project.FrameworkListFile, """<FileList TargetFrameworkVersion="12.0" />""");
        project.Build(MetadataTargets);
        ReadProperty(project.Output("Microsoft.NETCoreSdk.BundledVersions.props"), "BundledNETCoreAppTargetFramework").Should().Be("net12.0");
        File.WriteAllText(project.FrameworkListFile, """<FileList TargetFrameworkVersion="11.0" />""");
        project.Build(MetadataTargets);

        foreach (string missing in project.MetadataFiles)
        {
            byte[] expected = File.ReadAllBytes(missing);
            Dictionary<string, DateTime> siblings = AgeAndSnapshot(project.MetadataFiles.Where(path => path != missing));
            File.Delete(missing);
            project.Build(MetadataTargets);
            File.ReadAllBytes(missing).Should().Equal(expected);
            AssertTimestamps(siblings);
        }
    }

    /// <summary>
    /// Verifies standalone product commits come from dependency metadata, while simulated
    /// VMR builds use the SDK commit without requiring or reading Version.Details.xml.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProductCommitsPreserveStandaloneAndVmrProvenance(bool vmr)
    {
        MetadataProject project = CreateProject();
        if (vmr)
        {
            File.Delete(Path.Combine(project.Root, "eng", "Version.Details.xml"));
        }
        Observation result = project.Build(MetadataTargets, new() { ["DotNetBuildFromVMR"] = vmr.ToString() });
        result.Tasks.Contains("GetDependencyInfo").Should().Be(!vmr);
        using var commits = System.Text.Json.JsonDocument.Parse(File.ReadAllText(project.ShippingFile("productCommit-test.json")));
        foreach (string name in new[] { "runtime", "aspnetcore", "windowsdesktop", "sdk" })
        {
            string expected = vmr || name == "sdk" ? "sdk-commit" : name == "aspnetcore" ? "aspnet-commit" : "runtime-commit";
            commits.RootElement.GetProperty(name).GetProperty("commit").GetString().Should().Be(expected);
        }
    }

    /// <summary>
    /// Verifies RID-only edits and reverts regenerate the UTF-8, LF-separated chain,
    /// while warm builds skip the task and preserve chain and state-file timestamps.
    /// </summary>
    [TestMethod]
    public void RuntimeIdentifierChainSkipsWarmBuildsAndTracksRidChangesAndReverts()
    {
        MetadataProject project = CreateProject();
        project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
        File.ReadAllBytes(project.ChainFile).Should().Equal(Encoding.UTF8.GetBytes("linux-x64\nlinux\nany"));
        Dictionary<string, DateTime> warm = Snapshot([project.ChainFile, .. project.StateFiles]);
        project.Build(ChainTarget).Tasks.Should().NotContain(ChainTarget);
        AssertTimestamps(warm);

        project.Build(ChainTarget, new() { ["PortableProductMonikerRid"] = "win-x64" }).Tasks.Should().Contain(ChainTarget);
        File.ReadAllText(project.ChainFile).Should().Be("win-x64\nwin\nany");
        project.Build(ChainTarget, new() { ["PortableProductMonikerRid"] = "win-x64" }).Tasks.Should().NotContain(ChainTarget);
        project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
        File.ReadAllText(project.ChainFile).Should().Be("linux-x64\nlinux\nany");
        project.Build(ChainTarget).Tasks.Should().NotContain(ChainTarget);
    }

    /// <summary>
    /// Verifies a newer graph producing identical chain bytes preserves the output timestamp
    /// but advances processing completion, so the next build skips instead of regenerating forever.
    /// Also verifies graph edits and reverts update the chain when its contents actually change.
    /// </summary>
    [TestMethod]
    public void RuntimeIdentifierChainPreservesIdenticalResultsWithoutPerpetualRegeneration()
    {
        MetadataProject project = CreateProject();
        project.Build(ChainTarget);
        Dictionary<string, DateTime> unchanged = AgeAndSnapshot([project.ChainFile]);
        WriteNewer(project.GraphFile, Graph + Environment.NewLine, project.CompletionFile);

        project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
        AssertTimestamps(unchanged);
        project.Build(ChainTarget).Tasks.Should().NotContain(ChainTarget);
        AssertTimestamps(unchanged);

        foreach (string graph in new[] { Graph.Replace("\"linux\"", "\"unix\""), Graph })
        {
            WriteNewer(project.GraphFile, graph, project.CompletionFile);
            project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
            File.ReadAllText(project.ChainFile).Should().Be(graph == Graph ? "linux-x64\nlinux\nany" : "linux-x64\nunix\nany");
            project.Build(ChainTarget).Tasks.Should().NotContain(ChainTarget);
        }
    }

    /// <summary>
    /// Verifies package-root and package-version switches invalidate the chain even when
    /// the replacement graph has an older timestamp, and reverting the mapping restores it.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RuntimeIdentifierChainTracksPackagePathChangesWithOlderInputs(bool changeRoot)
    {
        MetadataProject project = CreateProject();
        project.Build(ChainTarget);
        string root = changeRoot ? Path.Combine(project.Root, "other-packages") : project.PackageRoot;
        string version = changeRoot ? "1.0.0" : "2.0.0";
        string graphFile = project.WriteGraph(root, version, Graph.Replace("\"linux\"", "\"unix\""));
        File.SetLastWriteTimeUtc(graphFile, DateTime.UtcNow.AddDays(-1));
        var properties = new Dictionary<string, string>
        {
            ["NuGetPackageRoot"] = root + Path.DirectorySeparatorChar,
            ["MicrosoftNETCorePlatformsPackageVersion"] = version
        };

        project.Build(ChainTarget, properties).Tasks.Should().Contain(ChainTarget);
        File.ReadAllText(project.ChainFile).Should().Be("linux-x64\nunix\nany");
        project.Build(ChainTarget, properties).Tasks.Should().NotContain(ChainTarget);
        project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
        File.ReadAllText(project.ChainFile).Should().Be("linux-x64\nlinux\nany");
    }

    /// <summary>
    /// Verifies a deleted chain is regenerated despite existing completion state, and
    /// output-directory changes and reverts do not rewrite an unchanged previous destination.
    /// </summary>
    [TestMethod]
    public void RuntimeIdentifierChainRepairsMissingOutputAndTracksOutputPathChanges()
    {
        MetadataProject project = CreateProject();
        project.Build(ChainTarget);
        File.Delete(project.ChainFile);
        project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
        File.ReadAllText(project.ChainFile).Should().Be("linux-x64\nlinux\nany");
        Dictionary<string, DateTime> original = Snapshot([project.ChainFile]);
        string other = Path.Combine(project.Root, "other-output");

        project.Build(ChainTarget, new() { ["OutputPath"] = other + Path.DirectorySeparatorChar }).Tasks.Should().Contain(ChainTarget);
        File.ReadAllText(Path.Combine(other, "NETCoreSdkRuntimeIdentifierChain.txt")).Should().Be("linux-x64\nlinux\nany");
        AssertTimestamps(original);
        project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
        AssertTimestamps(original);
        project.Build(ChainTarget).Tasks.Should().NotContain(ChainTarget);
    }

    /// <summary>
    /// Verifies missing or corrupt input state and completion markers force regeneration,
    /// restore valid completion, and allow the following unchanged build to skip.
    /// </summary>
    [TestMethod]
    [DataRow("inputs", false)]
    [DataRow("inputs", true)]
    [DataRow("complete", false)]
    [DataRow("complete", true)]
    public void RuntimeIdentifierChainRepairsMissingOrCorruptState(string extension, bool corrupt)
    {
        MetadataProject project = CreateProject();
        project.Build(ChainTarget);
        string state = Path.ChangeExtension(project.CompletionFile, extension);
        if (corrupt)
        {
            File.WriteAllText(state, "corrupt");
        }
        else
        {
            File.Delete(state);
        }

        project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
        File.ReadAllText(project.ChainFile).Should().Be("linux-x64\nlinux\nany");
        File.ReadAllText(project.CompletionFile).Should().Be("complete" + Environment.NewLine);
        project.Build(ChainTarget).Tasks.Should().NotContain(ChainTarget);
    }

    /// <summary>
    /// Verifies malformed graphs and interruption before completion leave no success stamp,
    /// so a subsequent valid build repairs generation rather than trusting partial work.
    /// </summary>
    [TestMethod]
    public void RuntimeIdentifierChainRecoversAfterFailedAndInterruptedGeneration()
    {
        MetadataProject project = CreateProject();
        project.Build(ChainTarget);
        WriteNewer(project.GraphFile, "{", project.CompletionFile);
        project.Build(ChainTarget, success: false);
        File.Exists(project.CompletionFile).Should().BeFalse();

        WriteFile(project.GraphFile, Graph);
        project.Build(ChainTarget, new() { ["InterruptChain"] = "true" }, success: false);
        File.Exists(project.CompletionFile).Should().BeFalse();
        project.Build(ChainTarget).Tasks.Should().Contain(ChainTarget);
        project.Build(ChainTarget).Tasks.Should().NotContain(ChainTarget);
    }

    /// <summary>
    /// Verifies interruption after recording changed inputs leaves the previous chain and
    /// completion untouched, and the newer input state triggers regeneration on the next build.
    /// </summary>
    [TestMethod]
    public void RuntimeIdentifierChainRecoversAfterInterruptedInputPreparation()
    {
        MetadataProject project = CreateProject();
        project.Build(ChainTarget);
        Dictionary<string, DateTime> original = Snapshot([project.ChainFile, project.CompletionFile]);

        project.Build(ChainTarget, new()
        {
            ["PortableProductMonikerRid"] = "win-x64",
            ["InterruptChainPreparation"] = "true"
        }, success: false);
        AssertTimestamps(original);
        File.ReadAllText(project.ChainFile).Should().Be("linux-x64\nlinux\nany");

        var properties = new Dictionary<string, string> { ["PortableProductMonikerRid"] = "win-x64" };
        project.Build(ChainTarget, properties).Tasks.Should().Contain(ChainTarget);
        File.ReadAllText(project.ChainFile).Should().Be("win-x64\nwin\nany");
        project.Build(ChainTarget, properties).Tasks.Should().NotContain(ChainTarget);
    }

    /// <summary>
    /// Verifies newer task-assembly or defining-target timestamps force reprocessing without
    /// rewriting an identical chain, and the next unchanged build skips generation.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RuntimeIdentifierChainTracksImplementationInputTimestamps(bool taskAssembly)
    {
        MetadataProject project = CreateProject();
        string input = Path.Combine(project.Root, taskAssembly ? "sdk-tasks.dll" : "GenerateLayout.targets");
        var properties = new Dictionary<string, string>();
        if (taskAssembly)
        {
            File.Copy(typeof(GenerateSdkRuntimeIdentifierChain).Assembly.Location, input);
            properties["SdkTasksAssembly"] = input;
        }

        project.Build(ChainTarget, properties);
        project.Build(ChainTarget, properties).Tasks.Should().NotContain(ChainTarget);
        Dictionary<string, DateTime> unchanged = AgeAndSnapshot([project.ChainFile]);
        MakeNewer(input, project.CompletionFile);
        project.Build(ChainTarget, properties).Tasks.Should().Contain(ChainTarget);
        AssertTimestamps(unchanged);
        project.Build(ChainTarget, properties).Tasks.Should().NotContain(ChainTarget);
    }

    private MetadataProject CreateProject() =>
        new(TestAssetsManager.CreateTestDirectory(identifier: Guid.NewGuid().ToString("N")).Path);

    private static string ReadProperty(string file, string name) =>
        XDocument.Load(file).Descendants(name).Single().Value;

    private static Dictionary<string, DateTime> Snapshot(IEnumerable<string> paths) =>
        paths.ToDictionary(path => path, File.GetLastWriteTimeUtc);

    private static Dictionary<string, DateTime> AgeAndSnapshot(IEnumerable<string> paths)
    {
        string[] files = paths.ToArray();
        foreach (string file in files)
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
        }
        return Snapshot(files);
    }

    private static void AssertTimestamps(Dictionary<string, DateTime> timestamps)
    {
        foreach ((string path, DateTime timestamp) in timestamps)
        {
            File.GetLastWriteTimeUtc(path).Should().Be(timestamp, "{0} is unchanged", path);
        }
    }

    private static void AssertContents(Dictionary<string, byte[]> contents)
    {
        foreach ((string path, byte[] bytes) in contents)
        {
            File.ReadAllBytes(path).Should().Equal(bytes, "{0} has unchanged contents and encoding", path);
        }
    }

    private static string WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void WriteNewer(string path, string contents, string completion)
    {
        WriteFile(path, contents);
        MakeNewer(path, completion);
    }

    private static void MakeNewer(string path, string completion)
    {
        DateTime timestamp = File.GetLastWriteTimeUtc(completion);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        while (File.GetLastWriteTimeUtc(path) <= timestamp)
        {
            Thread.Sleep(20);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
    }

    private static string Escape(string value) => SecurityElement.Escape(value)!;

    private sealed record Observation(HashSet<string> Tasks);

    private sealed class MetadataProject
    {
        private readonly string _project;

        public MetadataProject(string root)
        {
            Root = root;
            PackageRoot = Path.Combine(root, "packages");
            GraphFile = WriteGraph(PackageRoot, "1.0.0", Graph);
            FrameworkListFile = WriteFile(Path.Combine(PackageRoot, "microsoft.netcore.app.ref", "11.0.0", "data", "FrameworkList.xml"),
                """<FileList TargetFrameworkVersion="11.0" />""");
            WriteFile(Path.Combine(root, "minimumMSBuildVersion"), "17.14.0");
            WriteFile(Path.Combine(root, "eng", "Version.Details.xml"), """
                <Dependencies><ProductDependencies>
                  <Dependency Name="Microsoft.NETCore.App.Ref" Version="11.0.0"><Sha>runtime-commit</Sha></Dependency>
                  <Dependency Name="Microsoft.AspNetCore.App.Ref" Version="11.0.0"><Sha>aspnet-commit</Sha></Dependency>
                </ProductDependencies></Dependencies>
                """);
            foreach (string name in new[] { "GenerateLayout.targets", "GenerateBundledVersions.targets" })
            {
                string source = LayoutFiles.GetPath(name, Path.Combine("src", "Layout", "redist", "targets", name));
                XDocument targets = XDocument.Load(source);
                XElement? complete = targets.Descendants("Target").SingleOrDefault(element => (string?)element.Attribute("Name") == ChainTarget)
                    ?.Element("CompleteIncrementalLayout");
                complete?.AddBeforeSelf(new XElement("Error", new XAttribute("Condition", "'$(InterruptChain)' == 'true'"),
                    new XAttribute("Text", "Injected chain interruption.")));
                targets.Descendants("Target").SingleOrDefault(element => (string?)element.Attribute("Name") == "_PrepareGenerateSdkRuntimeIdentifierChain")
                    ?.Element("WriteLinesToFile")?.AddAfterSelf(new XElement("Error",
                        new XAttribute("Condition", "'$(InterruptChainPreparation)' == 'true'"),
                        new XAttribute("Text", "Injected chain preparation interruption.")));
                targets.Save(Path.Combine(root, name));
            }
            string assembly = typeof(GenerateSdkRuntimeIdentifierChain).Assembly.Location;
            string builtIn = Path.Combine(Path.GetDirectoryName(typeof(Project).Assembly.Location)!, "Microsoft.Build.Tasks.Core.dll");
            string usingTasks = string.Join(Environment.NewLine,
                new[] { "GenerateSdkRuntimeIdentifierChain", "CompleteIncrementalLayout", "GetDependencyInfo", "GenerateDefaultRuntimeFrameworkVersion" }
                    .Select(name => $"<UsingTask TaskName=\"{name}\" AssemblyFile=\"{Escape(assembly)}\" />")
                    .Concat(new[] { "Copy", "Delete", "WriteLinesToFile", "ReadLinesFromFile", "XmlPeek", "Error" }
                        .Select(name => $"<UsingTask TaskName=\"Microsoft.Build.Tasks.{name}\" AssemblyFile=\"{Escape(builtIn)}\" />")));
            _project = WriteFile(Path.Combine(root, "metadata.proj"), $$"""
                <Project>
                  {{usingTasks}}
                  <PropertyGroup>
                    <OutputPath>{{Escape(Path.Combine(root, "out"))}}\</OutputPath>
                    <IntermediateOutputPath>{{Escape(Path.Combine(root, "obj"))}}\</IntermediateOutputPath>
                    <ArtifactsShippingPackagesDir>{{Escape(Path.Combine(root, "shipping"))}}\</ArtifactsShippingPackagesDir>
                    <RepositoryEngineeringDir>{{Escape(Path.Combine(root, "eng"))}}\</RepositoryEngineeringDir>
                    <NuGetPackageRoot>{{Escape(PackageRoot)}}\</NuGetPackageRoot>
                    <SdkTasksAssembly>{{Escape(assembly)}}</SdkTasksAssembly>
                    <MicrosoftNETCorePlatformsPackageVersion>1.0.0</MicrosoftNETCorePlatformsPackageVersion>
                    <MicrosoftNETCoreAppRefPackageVersion>11.0.0</MicrosoftNETCoreAppRefPackageVersion>
                    <MicrosoftNETCoreAppRuntimePackageVersion>11.0.0</MicrosoftNETCoreAppRuntimePackageVersion>
                    <MicrosoftAspNetCoreAppRefPackageVersion>11.0.0</MicrosoftAspNetCoreAppRefPackageVersion>
                    <MicrosoftAspNetCoreAppRuntimePackageVersion>11.0.0</MicrosoftAspNetCoreAppRuntimePackageVersion>
                    <MicrosoftWindowsDesktopAppRefPackageVersion>11.0.0</MicrosoftWindowsDesktopAppRefPackageVersion>
                    <MicrosoftWindowsDesktopAppRuntimePackageVersion>11.0.0</MicrosoftWindowsDesktopAppRuntimePackageVersion>
                    <NETStandardLibraryRefPackageVersion>2.1.0</NETStandardLibraryRefPackageVersion>
                    <MicrosoftBuildVersion>18.0.0-preview.1</MicrosoftBuildVersion>
                    <PortableProductMonikerRid>linux-x64</PortableProductMonikerRid>
                    <TargetRid>test</TargetRid>
                    <ProductMonikerRid>test</ProductMonikerRid>
                    <SourceRevisionId>source-revision</SourceRevisionId>
                    <BUILD_SOURCEVERSION>sdk-commit</BUILD_SOURCEVERSION>
                    <Version>11.0.100</Version>
                    <FullNugetVersion>11.0.100-test</FullNugetVersion>
                    <PackageVersion>11.0.100-test</PackageVersion>
                    <SdkFeatureBand>11.0.100</SdkFeatureBand>
                    <ObsoleteCliTool>Old.Test.Tool</ObsoleteCliTool>
                    <ManifestId>Test.Manifest</ManifestId>
                  </PropertyGroup>
                  <ItemGroup>
                    <BundledDotnetTool Include="test-tool" ObsoletesCliTool="$(ObsoleteCliTool)" />
                    <BundledManifests Include="$(ManifestId)" />
                    <BuiltinManifests Include="builtin.manifest" />
                  </ItemGroup>
                  <Import Project="GenerateLayout.targets" />
                  <Import Project="GenerateBundledVersions.targets" />
                </Project>
                """);
            Directory.CreateDirectory(Path.Combine(root, "out"));
            Directory.CreateDirectory(Path.Combine(root, "shipping"));
        }

        public string Root { get; }
        public string PackageRoot { get; }
        public string GraphFile { get; }
        public string FrameworkListFile { get; }
        public string ChainFile => Output("NETCoreSdkRuntimeIdentifierChain.txt");
        public string CompletionFile => Path.Combine(Root, "obj", "incremental-layout", "sdk-runtime-identifier-chain.complete");
        public string[] StateFiles => [Path.ChangeExtension(CompletionFile, ".inputs"), CompletionFile];
        public string[] MetadataFiles =>
        [
            ShippingFile("productVersion.txt"), ShippingFile("sdk-productVersion.txt"),
            ShippingFile("productCommit-test.txt"), ShippingFile("productCommit-test.json"),
            Output("Microsoft.NETCoreSdk.BundledVersions.props"), Output("Microsoft.NETCoreSdk.BundledCliTools.props"),
            Output("Microsoft.NETCoreSdk.BundledMSBuildInformation.props"),
            Output(".version"), Output(".toolsetversion"), Output("KnownWorkloadManifests.txt")
        ];
        public string Output(string name) => Path.Combine(Root, "out", name);
        public string ShippingFile(string name) => Path.Combine(Root, "shipping", name);

        public string WriteGraph(string root, string version, string contents)
        {
            string directory = Path.Combine(root, "microsoft.netcore.platforms", version);
            WriteFile(Path.Combine(directory, "runtime.json"), contents);
            return WriteFile(Path.Combine(directory, "PortableRuntimeIdentifierGraph.json"), contents);
        }

        public Observation Build(string targets, Dictionary<string, string>? properties = null, bool success = true)
        {
            using var collection = new ProjectCollection(properties ?? []);
            ProjectInstance instance = collection.LoadProject(_project).CreateProjectInstance();
            var logger = new MetadataLogger();
            string binlog = Path.Combine(Root, $"build-{Guid.NewGuid():N}.binlog");
            using var manager = new BuildManager();
            BuildResult result = manager.Build(new BuildParameters(collection)
                {
                    Loggers = [logger, new BinaryLogger { Parameters = binlog }]
                },
                new BuildRequestData(instance, targets.Split(';')));
            (result.OverallResult == BuildResultCode.Success).Should().Be(success, "MSBuild log:{0}{1}{0}Binlog: {2}", Environment.NewLine, logger.Text, binlog);
            return new Observation(logger.Tasks);
        }
    }

    private sealed class MetadataLogger : Logger
    {
        private readonly List<string> _messages = [];
        public HashSet<string> Tasks { get; } = [];
        public string Text => string.Join(Environment.NewLine, _messages);

        public override void Initialize(IEventSource source)
        {
            source.TaskStarted += (_, args) => Tasks.Add(args.TaskName.Split('.').Last());
            source.AnyEventRaised += (_, args) =>
            {
                if (args.Message is not null)
                {
                    _messages.Add(args.Message);
                }
            };
        }
    }
}
