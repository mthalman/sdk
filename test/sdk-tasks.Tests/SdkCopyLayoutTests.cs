// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Build.Utilities;

namespace Microsoft.CoreSdkTasks.Tests;

/// <summary>
/// Exercises SDK staging's production copy targets without bypassing their discovery,
/// package selection, error handling, output-item flow, or child-publish invocation.
/// </summary>
[TestClass]
// In-process MSBuild saves/restores process-wide state shared with other layout tests.
[DoNotParallelize]
public class SdkCopyLayoutTests : SdkTest
{
    /// <summary>
    /// Verifies warm copies perform no file copies while preserving discovery,
    /// exclusions, selected package metadata, and FileWrites for skipped outputs.
    /// </summary>
    [TestMethod]
    [DataRow("PublishContainersSdk")]
    [DataRow("PublishMSBuildExtensions")]
    [DataRow("PublishSdks")]
    [DataRow("LayoutBundledTools")]
    [DataRow("LayoutAppHostTemplate")]
    [DataRow("PublishDotnetWatchToRedist")]
    public void WarmCopiesPreservePreparationAndOutputItems(string target)
    {
        CopyProject project = CreateProject(target);
        project.Build().Copies.Should().Be(project.Destinations.Length);
        Directory.GetFiles(project.OutputDirectory, "*", SearchOption.AllDirectories)
            .Should().BeEquivalentTo(project.ExpectedFiles);
        Dictionary<string, (DateTime Timestamp, string Contents)> snapshot = Snapshot(project.ExpectedFiles);

        Observation warm = project.Build();

        warm.Copies.Should().Be(0);
        AssertSnapshot(snapshot);
        if (target == "PublishDotnetWatchToRedist")
        {
            warm.Project.GetItems("FileWrites").Select(item => item.GetMetadataValue("FullPath"))
                .Should().BeEquivalentTo(project.Destinations);
        }
        if (target == "LayoutBundledTools")
        {
            warm.Project.GetItems("_SelectedBundledDotnetToolPackage").Should().ContainSingle()
                .Which.GetMetadataValue("PackageId").Should().Be("Example.Tool.linux-x64");
        }
        if (target == "PublishContainersSdk")
        {
            warm.Publishes.Should().Be(1);
        }
    }

    /// <summary>
    /// Verifies source edits/reverts and deleted destinations copy only the affected
    /// file, without rewriting siblings or disturbing unowned output.
    /// </summary>
    [TestMethod]
    [DataRow("PublishContainersSdk")]
    [DataRow("PublishMSBuildExtensions")]
    [DataRow("PublishSdks")]
    [DataRow("LayoutBundledTools")]
    [DataRow("LayoutAppHostTemplate")]
    [DataRow("PublishDotnetWatchToRedist")]
    public void ChangedAndMissingFilesAreRepairedIndependently(string target)
    {
        CopyProject project = CreateProject(target);
        project.Build();
        string original = File.ReadAllText(project.Source);
        Dictionary<string, (DateTime Timestamp, string Contents)> siblings =
            Snapshot(project.ExpectedFiles.Where(path => path != project.Destination));

        foreach (string contents in new[] { original + " edited", original })
        {
            File.WriteAllText(project.Source, contents);
            project.Build().Copies.Should().Be(1);
            File.ReadAllText(project.Destination).Should().Be(contents);
            project.Build().Copies.Should().Be(0);
            AssertSnapshot(siblings);
        }

        File.Delete(project.Destination);
        project.Build().Copies.Should().Be(1);
        File.ReadAllText(project.Destination).Should().Be(original);
        project.Build().Copies.Should().Be(0);
        AssertSnapshot(siblings);
    }

    /// <summary>
    /// Verifies always-running globs discover newly added payload files even after an
    /// unchanged build, while existing files retain their contents and timestamps.
    /// </summary>
    [TestMethod]
    [DataRow("PublishMSBuildExtensions")]
    [DataRow("PublishSdks")]
    [DataRow("LayoutBundledTools")]
    [DataRow("PublishDotnetWatchToRedist")]
    public void NewPayloadFilesAreDiscoveredOnWarmBuilds(string target)
    {
        CopyProject project = CreateProject(target);
        project.Build();
        Dictionary<string, (DateTime Timestamp, string Contents)> snapshot = Snapshot(project.ExpectedFiles);
        Write(Path.Combine(Path.GetDirectoryName(project.Source)!, "additional.targets"), "additional payload");
        string destination = Path.Combine(Path.GetDirectoryName(project.Destination)!, "additional.targets");

        project.Build().Copies.Should().Be(1);

        File.ReadAllText(destination).Should().Be("additional payload");
        project.Build().Copies.Should().Be(0);
        AssertSnapshot(snapshot);
    }

    /// <summary>
    /// Verifies output-root changes, including paths with spaces, populate the new
    /// destination without modifying or unnecessarily recopying the original layout.
    /// </summary>
    [TestMethod]
    [DataRow("PublishContainersSdk")]
    [DataRow("PublishMSBuildExtensions")]
    [DataRow("PublishSdks")]
    [DataRow("LayoutBundledTools")]
    [DataRow("LayoutAppHostTemplate")]
    [DataRow("PublishDotnetWatchToRedist")]
    public void OutputRemapsDoNotRequireAProducerStamp(string target)
    {
        CopyProject project = CreateProject(target);
        project.Build();
        Dictionary<string, (DateTime Timestamp, string Contents)> snapshot = Snapshot(project.ExpectedFiles);
        string output = Path.Combine(project.Root, "other output");
        var properties = new Dictionary<string, string> { ["OutputPath"] = output + Path.DirectorySeparatorChar };

        project.Build(properties).Copies.Should().Be(project.Destinations.Length);
        foreach (string destination in project.ExpectedFiles.Where(path => path != project.Output("unowned.keep")))
        {
            File.ReadAllText(Path.Combine(output, Path.GetRelativePath(project.OutputDirectory, destination)))
                .Should().Be(File.ReadAllText(destination));
        }
        project.Build(properties).Copies.Should().Be(0);
        project.Build().Copies.Should().Be(0);
        AssertSnapshot(snapshot);
    }

    /// <summary>
    /// Verifies package-version and RID switches select older inputs correctly,
    /// including portable-tool fallback, and settle after switching or reverting.
    /// </summary>
    [TestMethod]
    [DataRow("PublishSdks", "BundledSdkVersion", "0.9.0")]
    [DataRow("LayoutAppHostTemplate", "MicrosoftNETCoreAppHostPackageVersion", "0.9.0")]
    [DataRow("LayoutAppHostTemplate", "SharedFrameworkRid", "win-x64")]
    [DataRow("LayoutBundledTools", "BundledToolVersion", "0.9.0")]
    [DataRow("LayoutBundledTools", "ProductMonikerRid", "win-x64")]
    [DataRow("LayoutBundledTools", "ProductMonikerRid", "osx-x64")]
    public void PackageAndRidRemapsPreserveSelection(string target, string property, string value)
    {
        CopyProject project = CreateProject(target);
        project.Build();
        var properties = new Dictionary<string, string> { [property] = value };
        foreach (string source in Directory.GetFiles(Path.GetDirectoryName(project.SourceFor(properties))!))
        {
            File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(project.Destination).AddDays(-1));
        }

        project.Build(properties).Copies.Should().Be(project.Destinations.Length);
        File.ReadAllText(project.DestinationFor(properties)).Should().Be(File.ReadAllText(project.SourceFor(properties)));
        project.Build(properties).Copies.Should().Be(0);
        project.Build();
        File.ReadAllText(project.Destination).Should().Be(File.ReadAllText(project.Source));
        project.Build().Copies.Should().Be(0);
    }

    /// <summary>
    /// Verifies Containers child Publish still receives its configuration, framework,
    /// and destination, repairs missing task output on warm invocations, and
    /// propagates failure.
    /// </summary>
    [TestMethod]
    public void ContainersChildPublishRemainsAuthoritative()
    {
        CopyProject project = CreateProject("PublishContainersSdk");
        project.Build().Publishes.Should().Be(1);
        File.ReadAllText(project.ContainerTask).Trim().Should().Be("Debug|" + ToolsetInfo.SdkTargetFramework);
        Observation warm = project.Build();
        warm.Copies.Should().Be(0);
        warm.Publishes.Should().Be(1);

        File.Delete(project.ContainerTask);
        Observation repaired = project.Build();
        repaired.Copies.Should().Be(0);
        repaired.Publishes.Should().Be(1);
        File.ReadAllText(project.ContainerTask).Trim().Should().Be("Debug|" + ToolsetInfo.SdkTargetFramework);

        project.Build(new() { ["Configuration"] = "Release" }).Publishes.Should().Be(1);
        File.ReadAllText(project.ContainerTask).Trim().Should().Be("Release|" + ToolsetInfo.SdkTargetFramework);

        Observation failed = project.Build(new() { ["FailChildPublish"] = "true" }, success: false);
        failed.Publishes.Should().Be(1);
        failed.Text.Should().Contain("Injected child Publish failure.");
        project.Build().Copies.Should().Be(0);
    }

    /// <summary>
    /// Verifies missing Containers build inputs and missing SDK restore markers fail
    /// explicitly even when an otherwise current destination already exists.
    /// </summary>
    [TestMethod]
    [DataRow("PublishContainersSdk")]
    [DataRow("PublishSdks")]
    public void MissingSourcesCannotBeHiddenByExistingOutputs(string target)
    {
        CopyProject project = CreateProject(target);
        project.Build();
        string missing = target == "PublishSdks"
            ? Path.Combine(project.PackageRoot, "example.sdk", "1.0.0", "example.sdk.nuspec")
            : project.Source;
        File.Delete(missing);

        project.Build(success: false).Text.Should().Contain(Path.GetFileName(missing));
    }

    /// <summary>
    /// Verifies apphost-template selection still rejects missing or ambiguous
    /// candidates on warm builds rather than trusting an old copied template.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AppHostTemplateSelectionRemainsValidated(bool duplicate)
    {
        CopyProject project = CreateProject("LayoutAppHostTemplate");
        project.Build();
        if (duplicate)
        {
            Write(Path.Combine(Path.GetDirectoryName(project.Source)!, "duplicate", Path.GetFileName(project.Source)), "duplicate");
        }
        else
        {
            File.Delete(project.Source);
        }

        project.Build(success: false).Text.Should().Contain("Failed to determine");
    }

    private CopyProject CreateProject(string target) =>
        new(TestAssetsManager.CreateTestDirectory(identifier: Guid.NewGuid().ToString("N")).Path, target);

    private static Dictionary<string, (DateTime Timestamp, string Contents)> Snapshot(IEnumerable<string> paths) =>
        paths.ToDictionary(path => path, path => (File.GetLastWriteTimeUtc(path), File.ReadAllText(path)));

    private static void AssertSnapshot(Dictionary<string, (DateTime Timestamp, string Contents)> snapshot)
    {
        foreach ((string path, (DateTime timestamp, string contents)) in snapshot)
        {
            File.GetLastWriteTimeUtc(path).Should().Be(timestamp, "{0} is unchanged", path);
            File.ReadAllText(path).Should().Be(contents);
        }
    }

    private static string Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Escape(string value) => SecurityElement.Escape(value)!;

    private sealed record Observation(int Copies, int Publishes, ProjectInstance Project, string Text);

    private sealed class CopyProject
    {
        private readonly string _project;
        private readonly string _target;
        private readonly string _framework = ToolsetInfo.SdkTargetFramework;
        private readonly string _extension = OperatingSystem.IsWindows() ? ".exe" : "";

        public CopyProject(string root, string target)
        {
            Root = root;
            _target = target;
            string builtIn = Path.Combine(Path.GetDirectoryName(typeof(Project).Assembly.Location)!, "Microsoft.Build.Tasks.Core.dll");
            string usingTasks = string.Join(Environment.NewLine,
                new[] { "Copy", "MSBuild", "WriteLinesToFile", "Error" }
                    .Select(name => $"<UsingTask TaskName=\"Microsoft.Build.Tasks.{name}\" AssemblyFile=\"{Escape(builtIn)}\" />"));
            foreach (string name in new[] { "GenerateLayout.targets", "BundledSdks.targets", "BundledDotnetTools.targets" })
            {
                File.Copy(LayoutFiles.GetPath(name, Path.Combine("src", "Layout", "redist", "targets", name)),
                    Path.Combine(root, name));
            }
            string buildTasks = Path.Combine(SdkTestContext.Current.ToolsetUnderTest.SdkFolderUnderTest,
                "Sdks", "Microsoft.NET.Sdk", "tools", _framework, "Microsoft.NET.Build.Tasks.dll");
            Write(Path.Combine(root, "runtime.json"),
                """{"runtimes":{"any":{},"linux-x64":{"#import":["any"]},"win-x64":{"#import":["any"]},"osx-x64":{"#import":["any"]}}}""");
            _project = Write(Path.Combine(root, "copy-layout.proj"), $$"""
                <Project>
                  {{usingTasks}}
                  <PropertyGroup>
                    <RepoRoot>{{Escape(Path.Combine(root, "repo") + Path.DirectorySeparatorChar)}}</RepoRoot>
                    <OutputPath>{{Escape(OutputDirectory + Path.DirectorySeparatorChar)}}</OutputPath>
                    <ArtifactsBinDir>{{Escape(Path.Combine(root, "bin") + Path.DirectorySeparatorChar)}}</ArtifactsBinDir>
                    <NuGetPackageRoot>{{Escape(PackageRoot + Path.DirectorySeparatorChar)}}</NuGetPackageRoot>
                    <Configuration>Debug</Configuration>
                    <SdkTargetFramework>{{_framework}}</SdkTargetFramework>
                    <Version>1.0.0</Version>
                    <BundledSdkVersion>1.0.0</BundledSdkVersion>
                    <BundledToolVersion>1.0.0</BundledToolVersion>
                    <MicrosoftNETCoreAppHostPackageVersion>1.0.0</MicrosoftNETCoreAppHostPackageVersion>
                    <SharedFrameworkRid>linux-x64</SharedFrameworkRid>
                    <ProductMonikerRid>linux-x64</ProductMonikerRid>
                    <ExeExtension>{{_extension}}</ExeExtension>
                    <IncludeAspNetCoreRuntime>false</IncludeAspNetCoreRuntime>
                    <MicrosoftNETBuildTasksAssembly>{{Escape(buildTasks)}}</MicrosoftNETBuildTasksAssembly>
                    <RuntimeIdentifierGraphPath>{{Escape(Path.Combine(root, "runtime.json"))}}</RuntimeIdentifierGraphPath>
                  </PropertyGroup>
                  <ItemGroup>
                    <BundledDotnetTool Include="Example.Tool" Version="$(BundledToolVersion)"
                                       RidSpecificPackageVersion="$(BundledToolVersion)" RidSpecificPackageRids="linux-x64;win-x64" />
                  </ItemGroup>
                  <Import Project="GenerateLayout.targets" />
                  <Import Project="BundledSdks.targets" />
                  <Import Project="BundledDotnetTools.targets" />
                  <ItemGroup>
                    <BundledSdk Remove="@(BundledSdk)" />
                    <BundledSdk Include="Example.Sdk" Version="$(BundledSdkVersion)" />
                  </ItemGroup>
                </Project>
                """);

            switch (target)
            {
                case "PublishContainersSdk":
                    Write(Source, "container props");
                    Write(Path.ChangeExtension(Source, ".targets"), "container targets");
                    Destinations = [Destination, Path.ChangeExtension(Destination, ".targets")];
                    Write(Path.Combine(root, "repo", "src", "Containers", "Microsoft.NET.Build.Containers", "Microsoft.NET.Build.Containers.csproj"), $$"""
                        <Project>
                          {{usingTasks}}
                          <Target Name="Publish">
                            <Error Condition="'$(FailChildPublish)' == 'true'" Text="Injected child Publish failure." />
                            <WriteLinesToFile File="$(PublishDir)/container-task.txt"
                                              Lines="$(Configuration)|$(TargetFramework)"
                                              Overwrite="true" WriteOnlyWhenDifferent="true" />
                          </Target>
                        </Project>
                        """);
                    break;
                case "PublishMSBuildExtensions":
                    Write(Source, "repo import");
                    string extensions = Path.Combine(root, "bin", "Debug", "Sdks", "Microsoft.NET.Build.Extensions");
                    Write(Path.Combine(extensions, "msbuildExtensions", "Microsoft", "payload.targets"), "extension payload");
                    Write(Path.Combine(extensions, "msbuildExtensions-ver", "version.targets"), "versioned extension");
                    Write(Path.Combine(extensions, "msbuildExtensions", "Microsoft", "Microsoft.NET.Build.Extensions", "tools", "net472", "excluded.dll"), "desktop excluded");
                    Destinations = [Destination, Output("Microsoft", "payload.targets"), Output("Current", "version.targets")];
                    break;
                case "PublishSdks":
                    foreach (string version in new[] { "1.0.0", "0.9.0" })
                    {
                        string package = Path.Combine(PackageRoot, "example.sdk", version);
                        Write(Path.Combine(package, "example.sdk.nuspec"), "<package />");
                        Write(Path.Combine(package, "Sdk", "Sdk.props"), version + " props");
                        Write(Path.Combine(package, "Sdk", "Sdk.targets"), version + " targets");
                        foreach (string excluded in new[] { "Icon.png", $"example.sdk.{version}.nupkg", $"example.sdk.{version}.nupkg.sha512", "Sdk/excluded.nupkg.metadata", "Sdk/.signature.p7s" })
                        {
                            Write(Path.Combine(package, excluded), "excluded");
                        }
                    }
                    Destinations = [Destination, Path.ChangeExtension(Destination, ".targets")];
                    break;
                case "LayoutBundledTools":
                    foreach (string version in new[] { "1.0.0", "0.9.0" })
                    {
                        foreach (string packageId in new[] { "example.tool", "example.tool.linux-x64", "example.tool.win-x64" })
                        {
                            string package = Path.Combine(PackageRoot, packageId, version);
                            Write(Path.Combine(package, "excluded.nuspec"), "excluded");
                            Write(Path.Combine(package, "tools", _framework, "any", "tool.dll"), packageId + version);
                            Write(Path.Combine(package, "tools", _framework, "any", "tool.json"), "tool configuration " + version);
                        }
                    }
                    Destinations = [Destination, Path.ChangeExtension(Destination, ".json")];
                    break;
                case "LayoutAppHostTemplate":
                    foreach (string version in new[] { "1.0.0", "0.9.0" })
                    {
                        foreach (string rid in new[] { "linux-x64", "win-x64" })
                        {
                            Write(AppHostSource(version, rid), rid + version);
                        }
                    }
                    Destinations = [Destination];
                    break;
                case "PublishDotnetWatchToRedist":
                    Write(Source, "watch payload");
                    string directory = Path.GetDirectoryName(Source)!;
                    Write(Path.Combine(directory, "Microsoft.CodeAnalysis.dll"), "shared compiler excluded");
                    Write(Path.Combine(directory, "cs", "Microsoft.CodeAnalysis.resources.dll"), "shared resources excluded");
                    Write(Path.Combine(directory, "BuildHost-netcore", "Microsoft.CodeAnalysis.dll"), "build host dependency");
                    Destinations = [Destination, Path.Combine(Path.GetDirectoryName(Destination)!, "BuildHost-netcore", "Microsoft.CodeAnalysis.dll")];
                    break;
                default:
                    throw new ArgumentException("Unknown copy target.", nameof(target));
            }
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));
            }
            Write(Output("unowned.keep"), "unowned");
        }

        public string Root { get; }
        public string PackageRoot => Path.Combine(Root, "packages");
        public string OutputDirectory => Path.Combine(Root, "out");
        public string[] Destinations { get; }
        public string Source => SourceFor([]);
        public string Destination => DestinationFor([]);
        public string ContainerTask => Output("Containers", "tasks", _framework, "container-task.txt");
        public string[] ExpectedFiles => _target == "PublishContainersSdk"
            ? [.. Destinations, ContainerTask, Output("unowned.keep")]
            : [.. Destinations, Output("unowned.keep")];
        public string Output(params string[] parts) => Path.Combine([OutputDirectory, .. parts]);

        public string SourceFor(Dictionary<string, string> properties) => _target switch
        {
            "PublishContainersSdk" => Path.Combine(Root, "repo", "src", "Containers", "packaging", "build", "Microsoft.NET.Build.Containers.props"),
            "PublishMSBuildExtensions" => Path.Combine(Root, "MSBuildImports", "import.props"),
            "PublishSdks" => Path.Combine(PackageRoot, "example.sdk", properties.GetValueOrDefault("BundledSdkVersion", "1.0.0"), "Sdk", "Sdk.props"),
            "LayoutBundledTools" => Path.Combine(PackageRoot,
                properties.GetValueOrDefault("ProductMonikerRid", "linux-x64") == "osx-x64"
                    ? "example.tool" : "example.tool." + properties.GetValueOrDefault("ProductMonikerRid", "linux-x64"),
                properties.GetValueOrDefault("BundledToolVersion", "1.0.0"), "tools", _framework, "any", "tool.dll"),
            "LayoutAppHostTemplate" => AppHostSource(properties.GetValueOrDefault("MicrosoftNETCoreAppHostPackageVersion", "1.0.0"),
                properties.GetValueOrDefault("SharedFrameworkRid", "linux-x64")),
            "PublishDotnetWatchToRedist" => Path.Combine(Root, "bin", "dotnet-watch", "Debug", _framework, "dotnet-watch.dll"),
            _ => throw new InvalidOperationException("Unknown copy target.")
        };

        public string DestinationFor(Dictionary<string, string> properties) => _target switch
        {
            "PublishContainersSdk" => Output("Containers", "build", "Microsoft.NET.Build.Containers.props"),
            "PublishMSBuildExtensions" => Output("import.props"),
            "PublishSdks" => Output("Sdks", "Example.Sdk", "Sdk", "Sdk.props"),
            "LayoutBundledTools" => Output("DotnetTools", "Example.Tool",
                properties.GetValueOrDefault("BundledToolVersion", "1.0.0"), "tools", _framework, "any", "tool.dll"),
            "LayoutAppHostTemplate" => Output("AppHostTemplate", "apphost" + _extension),
            "PublishDotnetWatchToRedist" => Output("DotnetTools", "dotnet-watch", "1.0.0", "tools", _framework, "any", "dotnet-watch.dll"),
            _ => throw new InvalidOperationException("Unknown copy target.")
        };

        private string AppHostSource(string version, string rid) =>
            Path.Combine(PackageRoot, "microsoft.netcore.app.host." + rid, version, "runtimes", rid, "native", "apphost" + _extension);

        public Observation Build(Dictionary<string, string>? properties = null, bool success = true)
        {
            using var collection = new ProjectCollection(properties ?? []);
            ProjectInstance instance = collection.LoadProject(_project).CreateProjectInstance();
            var logger = new CopyLogger();
            string binlog = Path.Combine(Root, $"build-{Guid.NewGuid():N}.binlog");
            using var manager = new BuildManager();
            BuildResult result = manager.Build(new BuildParameters(collection)
                {
                    Loggers = [logger, new BinaryLogger { Parameters = binlog }]
                },
                new BuildRequestData(instance, [_target]));
            (result.OverallResult == BuildResultCode.Success).Should().Be(success,
                "MSBuild log:{0}{1}{0}Binlog: {2}", Environment.NewLine, logger.Text, binlog);
            return new Observation(logger.Copies, logger.Publishes, instance, logger.Text);
        }
    }

    private sealed class CopyLogger : Logger
    {
        private readonly List<string> _messages = [];
        public int Copies { get; private set; }
        public int Publishes { get; private set; }
        public string Text => string.Join(Environment.NewLine, _messages);

        public override void Initialize(IEventSource source)
        {
            source.TargetStarted += (_, args) => Publishes += args.TargetName == "Publish" ? 1 : 0;
            source.MessageRaised += (_, args) =>
                Copies += args.Message?.StartsWith("Copying file from \"", StringComparison.Ordinal) == true ? 1 : 0;
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
