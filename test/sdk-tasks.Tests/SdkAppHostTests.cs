// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
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
/// Exercises production compiler/MSBuild apphost targets with real native templates,
/// including independent host invalidation and always-running payload preparation.
/// </summary>
[TestClass]
// In-process MSBuild saves/restores process-wide state shared with other layout tests.
[DoNotParallelize]
public class SdkAppHostTests : SdkTest
{
    /// <summary>
    /// Verifies unchanged invocations skip native apphost creation without rewriting
    /// binaries, while newly introduced Roslyn payload files are still copied.
    /// </summary>
    [TestMethod]
    public void WarmBuildsSkipGenerationWithoutSkippingPayloadCopying()
    {
        AppHostProject project = CreateProject();
        project.Build().CreatedHosts.Should().Be(4);
        Dictionary<string, DateTime> timestamps = Snapshot(project.Hosts);
        Dictionary<string, byte[]> contents = project.Hosts.ToDictionary(path => path, File.ReadAllBytes);
        foreach (string host in project.Hosts)
        {
            Encoding.UTF8.GetString(contents[host]).Should().Contain(Path.GetFileNameWithoutExtension(host) + ".dll\0");
        }

        project.Build().CreatedHosts.Should().Be(0);
        Write(Path.Combine(project.RoslynPackageDirectory, "new-payload.txt"), "new payload");
        project.Build().CreatedHosts.Should().Be(0);

        File.ReadAllText(project.Output("Roslyn", "bincore", "new-payload.txt")).Should().Be("new payload");
        AssertTimestamps(timestamps);
        foreach ((string path, byte[] bytes) in contents)
        {
            File.ReadAllBytes(path).Should().Equal(bytes);
        }
    }

    /// <summary>
    /// Verifies deleting any one apphost repairs only that host and leaves settled
    /// compiler/MSBuild siblings untouched.
    /// </summary>
    [TestMethod]
    public void DeletedHostsAreRepairedIndependently()
    {
        AppHostProject project = CreateProject();
        project.Build();
        foreach (string missing in project.Hosts)
        {
            byte[] expected = File.ReadAllBytes(missing);
            Dictionary<string, DateTime> siblings = Snapshot(project.Hosts.Where(path => path != missing));
            File.Delete(missing);

            project.Build().CreatedHosts.Should().Be(1);

            File.ReadAllBytes(missing).Should().Equal(expected);
            AssertTimestamps(siblings);
            project.Build().CreatedHosts.Should().Be(0);
        }
    }

    /// <summary>
    /// Verifies edits and reverts to one managed assembly invalidate only its host,
    /// even when the edit does not change the native resources or final host bytes.
    /// </summary>
    [TestMethod]
    [DataRow("csc")]
    [DataRow("vbc")]
    [DataRow("VBCSCompiler")]
    [DataRow("MSBuild")]
    public void ManagedAssemblyEditsAndRevertsInvalidateOnlyTheirHost(string name)
    {
        AppHostProject project = CreateProject();
        project.Build();
        string source = project.ManagedInput(name);
        byte[] original = File.ReadAllBytes(source);
        Dictionary<string, DateTime> siblings = Snapshot(project.Hosts.Where(path => Path.GetFileNameWithoutExtension(path) != name));

        File.WriteAllBytes(source, [.. original, 0]);
        MakeNewer(source, project.Completion(name));
        project.Build().CreatedHosts.Should().Be(1);
        project.Build().CreatedHosts.Should().Be(0);
        AssertTimestamps(siblings);

        File.WriteAllBytes(source, original);
        MakeNewer(source, project.Completion(name));
        project.Build().CreatedHosts.Should().Be(1);
        project.Build().CreatedHosts.Should().Be(0);
        AssertTimestamps(siblings);
    }

    /// <summary>
    /// Verifies reprocessing a newer template with identical bytes invalidates all
    /// hosts once, then settles without using native files as processing-freshness stamps.
    /// </summary>
    [TestMethod]
    public void NewerIdenticalTemplatesInvalidateAllHostsAndThenSettle()
    {
        AppHostProject project = CreateProject();
        project.Build();
        byte[] original = File.ReadAllBytes(project.Template);
        File.WriteAllBytes(project.Template, original);
        MakeNewer(project.Template, project.Completion("MSBuild"));

        project.Build().CreatedHosts.Should().Be(4);
        project.Build().CreatedHosts.Should().Be(0);

        File.WriteAllBytes(project.Template, original);
        MakeNewer(project.Template, project.Completion("MSBuild"));
        project.Build().CreatedHosts.Should().Be(4);
        project.Build().CreatedHosts.Should().Be(0);
    }

    /// <summary>
    /// Verifies missing or corrupt per-host input/completion state is repaired without
    /// rewriting settled siblings or trusting invalid completion markers.
    /// </summary>
    [TestMethod]
    [DataRow("inputs", false)]
    [DataRow("inputs", true)]
    [DataRow("complete", false)]
    [DataRow("complete", true)]
    public void StateFilesAreIndependentlyRepairable(string extension, bool corrupt)
    {
        AppHostProject project = CreateProject();
        project.Build();
        foreach (string name in new[] { "csc", "vbc", "VBCSCompiler", "MSBuild" })
        {
            string state = Path.ChangeExtension(project.Completion(name), extension);
            Dictionary<string, DateTime> siblings = Snapshot(project.Hosts.Where(path => Path.GetFileNameWithoutExtension(path) != name));
            if (corrupt)
            {
                File.WriteAllText(state, "invalid");
            }
            else
            {
                File.Delete(state);
            }

            project.Build().CreatedHosts.Should().Be(1);
            File.ReadAllText(project.Completion(name)).Should().Be("complete" + Environment.NewLine);
            AssertTimestamps(siblings);
            project.Build().CreatedHosts.Should().Be(0);
        }
    }

    /// <summary>
    /// Verifies property-only template/destination remaps to older files invalidate
    /// generation, stabilize, and regenerate correctly when the mapping is reverted.
    /// </summary>
    [TestMethod]
    [DataRow("SharedFrameworkRid")]
    [DataRow("MicrosoftNETCoreAppHostPackageVersion")]
    [DataRow("NuGetPackageRoot")]
    [DataRow("ExeExtension")]
    [DataRow("OutputPath")]
    public void PropertyRemapsAndRevertsAreTracked(string property)
    {
        AppHostProject project = CreateProject();
        project.Build();
        var properties = new Dictionary<string, string>();
        string packages = Path.Combine(project.Root, "packages");
        string rid = project.Rid;
        string version = "1.0.0";
        string extension = AppHostProject.Extension;
        string output = project.OutputDirectory;
        switch (property)
        {
            case "SharedFrameworkRid":
                properties[property] = rid += "-alternate";
                break;
            case "MicrosoftNETCoreAppHostPackageVersion":
                properties[property] = version = "0.9.0";
                break;
            case "NuGetPackageRoot":
                packages = Path.Combine(project.Root, "other-packages");
                properties[property] = packages + Path.DirectorySeparatorChar;
                foreach (string source in Directory.GetFiles(project.RoslynPackageDirectory))
                {
                    Copy(source, Path.Combine(packages, "microsoft.net.compilers.toolset", "1.0.0", "tasks", "netcore", "bincore", Path.GetFileName(source)));
                }
                break;
            case "ExeExtension":
                properties[property] = extension += ".alternate";
                break;
            case "OutputPath":
                output = Path.Combine(project.Root, "other output");
                properties[property] = output + Path.DirectorySeparatorChar;
                Copy(project.Output("MSBuild.dll"), Path.Combine(output, "MSBuild.dll"));
                break;
        }
        string template = project.TemplateAt(packages, rid, version, extension);
        if (template != project.Template)
        {
            Copy(project.Template, template);
            File.SetLastWriteTimeUtc(template, DateTime.UtcNow.AddDays(-1));
        }

        project.Build(properties).CreatedHosts.Should().Be(4);
        string[] remapped = project.HostsAt(output, extension);
        remapped.Should().OnlyContain(path => File.Exists(path));
        Dictionary<string, DateTime> timestamps = Snapshot(remapped);
        project.Build(properties).CreatedHosts.Should().Be(0);
        AssertTimestamps(timestamps);

        project.Build().CreatedHosts.Should().Be(4);
        project.Build().CreatedHosts.Should().Be(0);
    }

    /// <summary>
    /// Verifies changes to the defining targets or task implementation timestamps
    /// invalidate all hosts and settle on the next unchanged invocation.
    /// </summary>
    [TestMethod]
    [DataRow("targets")]
    [DataRow("sdk-tasks")]
    [DataRow("build-tasks")]
    [DataRow("host-model")]
    public void ImplementationTimestampsInvalidateGeneration(string implementation)
    {
        AppHostProject project = CreateProject();
        var properties = new Dictionary<string, string>();
        string input;
        if (implementation == "targets")
        {
            input = Path.Combine(project.Root, "GenerateLayout.targets");
        }
        else
        {
            string source = implementation switch
            {
                "sdk-tasks" => typeof(CompleteIncrementalLayout).Assembly.Location,
                "build-tasks" => project.BuildTasksAssembly,
                _ => Path.Combine(SdkTestContext.Current.ToolsetUnderTest.SdkFolderUnderTest, "Microsoft.NET.HostModel.dll")
            };
            input = Path.Combine(project.Root, "implementation", Path.GetFileName(source));
            Copy(source, input);
            string property = implementation switch
            {
                "sdk-tasks" => "SdkTasksAssembly",
                "build-tasks" => "MicrosoftNETBuildTasksAssembly",
                _ => "_SdkAppHostHostModelAssembly"
            };
            if (implementation == "host-model")
            {
                string targets = Path.Combine(project.Root, "GenerateLayout.targets");
                XDocument document = XDocument.Load(targets);
                document.Descendants(property).First().Value = input;
                document.Save(targets);
            }
            else
            {
                properties[property] = input;
            }
        }
        project.Build(properties);
        project.Build(properties).CreatedHosts.Should().Be(0);
        MakeNewer(input, project.Completion("MSBuild"));

        project.Build(properties).CreatedHosts.Should().Be(4);
        project.Build(properties).CreatedHosts.Should().Be(0);
    }

    /// <summary>
    /// Verifies an interrupted generation cannot retain completion for a newly written
    /// host, and recovery regenerates only the interrupted host.
    /// </summary>
    [TestMethod]
    [DataRow("csc")]
    [DataRow("MSBuild")]
    public void InterruptedGenerationCannotCertifyANewHost(string name)
    {
        AppHostProject project = CreateProject();
        project.Build();
        Dictionary<string, DateTime> siblings = Snapshot(project.Hosts.Where(path => Path.GetFileNameWithoutExtension(path) != name));
        MakeNewer(project.ManagedInput(name), project.Completion(name));

        project.Build(new() { ["InterruptHost"] = name }, success: false);
        File.Exists(project.Completion(name)).Should().BeFalse();
        File.Exists(project.Hosts.Single(path => Path.GetFileNameWithoutExtension(path) == name)).Should().BeTrue();
        AssertTimestamps(siblings);

        project.Build().CreatedHosts.Should().Be(1);
        project.Build().CreatedHosts.Should().Be(0);
        AssertTimestamps(siblings);
    }

    /// <summary>
    /// Verifies a missing or malformed template fails generation, removes the affected
    /// completion marker, and recovers after restoring valid input.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidTemplatesFailAndRecover(bool malformed)
    {
        AppHostProject project = CreateProject();
        project.Build();
        byte[] original = File.ReadAllBytes(project.Template);
        if (malformed)
        {
            File.WriteAllText(project.Template, "invalid apphost");
            MakeNewer(project.Template, project.Completion("MSBuild"));
        }
        else
        {
            File.Delete(project.Template);
        }

        project.Build(success: false);
        File.Exists(project.Completion("csc")).Should().BeFalse();

        File.WriteAllBytes(project.Template, original);
        project.Build().CreatedHosts.Should().Be(4);
        project.Build().CreatedHosts.Should().Be(0);
    }

    /// <summary>
    /// Simulates entitlement signing to verify family-specific invalidation and that
    /// a signing failure cannot leave completion for an unsigned/partially signed host.
    /// This is not a substitute for real macOS signing coverage.
    /// </summary>
    [TestMethod]
    [DataRow("csc", "roslyn-entitlements.plist", 3)]
    [DataRow("MSBuild", "msbuild-entitlements.plist", 1)]
    public void SigningCompletionAndEntitlementsAreTracked(string name, string entitlements, int count)
    {
        AppHostProject project = CreateProject(simulateSigning: true);
        project.Build().SigningOperations.Should().Be(4);
        project.Build().Should().Be(new Observation(0, 0));
        string input = Path.Combine(project.Root, entitlements);
        File.AppendAllText(input, Environment.NewLine);
        MakeNewer(input, project.Completion("MSBuild"));

        project.Build(new() { ["FailSigningHost"] = name }, success: false).SigningOperations.Should().BeGreaterThan(0);
        File.Exists(project.Completion(name)).Should().BeFalse();
        Observation recovery = project.Build();
        recovery.Should().Be(new Observation(count, count));
        project.Build().Should().Be(new Observation(0, 0));
    }

    /// <summary>
    /// Verifies optional Windows type-library copying remains active when native host
    /// generation skips, including deleted outputs and package-version changes.
    /// </summary>
    [TestMethod]
    public void TypeLibraryCopyRemainsIndependentOfHostFreshness()
    {
        AppHostProject project = CreateProject();
        string rid = "win-test";
        Copy(project.Template, project.TemplateAt(Path.Combine(project.Root, "packages"), rid, "1.0.0", AppHostProject.Extension));
        var properties = new Dictionary<string, string> { ["SharedFrameworkRid"] = rid };
        string source = Write(Path.Combine(project.Root, "packages", "microsoft.build.framework", "1.0.0",
            "tools", "net472", "Microsoft.Build.Framework.tlb"), "type library");
        project.Build(properties);
        Dictionary<string, DateTime> timestamps = Snapshot(project.Hosts);
        string destination = project.Output("Microsoft.Build.Framework.tlb");
        File.ReadAllText(destination).Should().Be("type library");

        File.Delete(destination);
        project.Build(properties).CreatedHosts.Should().Be(0);
        File.ReadAllText(destination).Should().Be("type library");
        properties["MicrosoftBuildVersion"] = "0.9.0";
        Write(source.Replace("1.0.0", "0.9.0"), "different type library");
        project.Build(properties).CreatedHosts.Should().Be(0);
        File.ReadAllText(destination).Should().Be("different type library");
        AssertTimestamps(timestamps);
    }

    /// <summary>
    /// Verifies either public apphost producer can run independently, without copying
    /// or generating the other family's payload, and skips its own warm generation.
    /// </summary>
    [TestMethod]
    [DataRow("PublishRoslyn", 3)]
    [DataRow("CreateMSBuildAppHost", 1)]
    public void PublicProducersRemainIndependentlyInvokable(string target, int count)
    {
        AppHostProject project = CreateProject();
        project.Build(targets: target).CreatedHosts.Should().Be(count);
        project.Hosts.Count(File.Exists).Should().Be(count);
        project.Build(targets: target).CreatedHosts.Should().Be(0);
    }

    /// <summary>
    /// Verifies adding a compiler host creates only that host without regenerating
    /// existing siblings or requiring a family-wide input-membership stamp.
    /// </summary>
    [TestMethod]
    public void AddedCompilerHostsDoNotInvalidateExistingSiblings()
    {
        AppHostProject project = CreateProject();
        project.Build();
        Dictionary<string, DateTime> timestamps = Snapshot(project.Hosts);
        Copy(project.ManagedInput("csc"), Path.Combine(project.RoslynPackageDirectory, "additional.dll"));
        var properties = new Dictionary<string, string> { ["AdditionalCompiler"] = "additional" };

        project.Build(properties).CreatedHosts.Should().Be(1);
        File.Exists(project.Output("Roslyn", "bincore", "additional" + AppHostProject.Extension)).Should().BeTrue();
        project.Build(properties).CreatedHosts.Should().Be(0);
        AssertTimestamps(timestamps);
    }

    /// <summary>
    /// Verifies a missing managed input fails even when completion exists, and restoring
    /// the input repairs only its native host.
    /// </summary>
    [TestMethod]
    public void MissingManagedInputsCannotBeHiddenByCompletion()
    {
        AppHostProject project = CreateProject();
        project.Build();
        string source = project.ManagedInput("MSBuild");
        byte[] contents = File.ReadAllBytes(source);
        File.Delete(source);

        project.Build(success: false);
        File.Exists(project.Completion("MSBuild")).Should().BeFalse();

        File.WriteAllBytes(source, contents);
        project.Build().CreatedHosts.Should().Be(1);
        project.Build().CreatedHosts.Should().Be(0);
    }

    /// <summary>
    /// Simulates switching signing policy through RID selection, verifying unsigned
    /// and signed invocations invalidate each other even with older template inputs.
    /// </summary>
    [TestMethod]
    public void SigningPolicyChangesAndRevertsInvalidateGeneration()
    {
        AppHostProject project = CreateProject(simulateSigning: true);
        project.Build().Should().Be(new Observation(4, 4));
        string rid = "unsigned-test";
        string template = project.TemplateAt(Path.Combine(project.Root, "packages"), rid, "1.0.0", AppHostProject.Extension);
        Copy(project.Template, template);
        File.SetLastWriteTimeUtc(template, DateTime.UtcNow.AddDays(-1));
        var properties = new Dictionary<string, string> { ["SharedFrameworkRid"] = rid };

        project.Build(properties).Should().Be(new Observation(4, 0));
        project.Build(properties).Should().Be(new Observation(0, 0));
        project.Build().Should().Be(new Observation(4, 4));
        project.Build().Should().Be(new Observation(0, 0));
    }

    /// <summary>
    /// Verifies the assembled SDK's native compiler/MSBuild hosts actually launch their
    /// intended managed entry points, rather than merely containing patched filenames.
    /// </summary>
    [TestMethod]
    [DataRow("csc")]
    [DataRow("vbc")]
    [DataRow("MSBuild")]
    public void AssembledSdkAppHostsLaunchTheirManagedTools(string name)
    {
        ToolsetInfo toolset = SdkTestContext.Current.ToolsetUnderTest;
        string file = name + AppHostProject.Extension;
        string executable = name == "MSBuild"
            ? Path.Combine(toolset.SdkFolderUnderTest, file)
            : Path.Combine(toolset.SdkFolderUnderTest, "Roslyn", "bincore", file);

        var result = new RunExeCommand(Log, executable)
            .WithEnvironmentVariable("DOTNET_ROOT", toolset.DotNetRoot)
            .WithEnvironmentVariable("DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant(), toolset.DotNetRoot)
            .WithEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "0")
            .Execute(name == "MSBuild" ? "-version" : "-help");

        result.Should().Pass();
        result.StdOut.Should().NotBeNullOrWhiteSpace();
    }

    private AppHostProject CreateProject(bool simulateSigning = false) =>
        new(TestAssetsManager.CreateTestDirectory(identifier: Guid.NewGuid().ToString("N")).Path, simulateSigning);

    private static Dictionary<string, DateTime> Snapshot(IEnumerable<string> paths) =>
        paths.ToDictionary(path => path, File.GetLastWriteTimeUtc);

    private static void AssertTimestamps(Dictionary<string, DateTime> timestamps)
    {
        foreach ((string path, DateTime timestamp) in timestamps)
        {
            File.GetLastWriteTimeUtc(path).Should().Be(timestamp, "{0} is unchanged", path);
        }
    }

    private static string Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static void MakeNewer(string path, string completion)
    {
        DateTime timestamp = File.GetLastWriteTimeUtc(completion);
        do
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            if (File.GetLastWriteTimeUtc(path) > timestamp)
            {
                return;
            }
            Thread.Sleep(20);
        }
        while (true);
    }

    private static string Escape(string value) => SecurityElement.Escape(value)!;

    private sealed record Observation(int CreatedHosts, int SigningOperations);

    private sealed class AppHostProject
    {
        private readonly string _project;
        private readonly bool _simulateSigning;

        public AppHostProject(string root, bool simulateSigning)
        {
            Root = root;
            _simulateSigning = simulateSigning;
            string sdk = SdkTestContext.Current.ToolsetUnderTest.SdkFolderUnderTest;
            string extension = OperatingSystem.IsWindows() ? ".exe" : "";
            string sdkTasks = typeof(CompleteIncrementalLayout).Assembly.Location;
            string builtIn = Path.Combine(Path.GetDirectoryName(typeof(Project).Assembly.Location)!, "Microsoft.Build.Tasks.Core.dll");
            BuildTasksAssembly = Path.Combine(sdk, "Sdks", "Microsoft.NET.Sdk", "tools",
                ToolsetInfo.SdkTargetFramework, "Microsoft.NET.Build.Tasks.dll");
            Copy(Path.Combine(sdk, "AppHostTemplate", "apphost" + extension), Template);
            foreach (string name in new[] { "csc", "vbc", "VBCSCompiler" })
            {
                Copy(Path.Combine(sdk, "Roslyn", "bincore", name + ".dll"),
                    Path.Combine(RoslynPackageDirectory, name + ".dll"));
            }
            Copy(Path.Combine(sdk, "MSBuild.dll"), Output("MSBuild.dll"));
            foreach (string name in new[] { "roslyn-entitlements.plist", "msbuild-entitlements.plist" })
            {
                Copy(LayoutFiles.GetPath(name, Path.Combine("src", "Layout", "redist", name)), Path.Combine(root, name));
            }
            XDocument targets = XDocument.Load(LayoutFiles.GetPath("GenerateLayout.targets",
                Path.Combine("src", "Layout", "redist", "targets", "GenerateLayout.targets")));
            foreach ((string target, string item) in new[]
            {
                ("_CreateRoslynAppHosts", "_RoslynAppHost"),
                ("_CreateMSBuildAppHosts", "_MSBuildAppHost")
            })
            {
                XElement generation = targets.Descendants("Target").Single(element => (string?)element.Attribute("Name") == target);
                generation.Element("CompleteIncrementalLayout")!.AddBeforeSelf(new XElement("Error",
                    new XAttribute("Condition", $"'$(InterruptHost)' == '%({item}.Filename)'"),
                    new XAttribute("Text", "Injected interruption before apphost completion.")));
                if (simulateSigning)
                {
                    // Exercise signing control flow on every platform without claiming
                    // that a substituted operation validates macOS native signatures.
                    generation.Element("CreateAppHost")!.SetAttributeValue("EnableMacOSCodeSign", "false");
                    XElement signing = generation.Element("Exec")!;
                    string condition = signing.Attribute("Condition")!.Value;
                    signing.ReplaceWith(
                        new XElement("WriteLinesToFile",
                            new XAttribute("File", Path.Combine(root, "signing.log")),
                            new XAttribute("Lines", $"%({item}.DestinationPath)"),
                            new XAttribute("Condition", condition)),
                        new XElement("Error",
                            new XAttribute("Condition", $"{condition} and '$(FailSigningHost)' == '%({item}.Filename)'"),
                            new XAttribute("Text", "Injected entitlement signing failure.")));
                }
            }
            targets.Save(Path.Combine(root, "GenerateLayout.targets"));
            string usingTasks = string.Join(Environment.NewLine,
                new[] { "Copy", "Delete", "WriteLinesToFile", "ReadLinesFromFile", "Exec", "Error" }
                    .Select(name => $"<UsingTask TaskName=\"Microsoft.Build.Tasks.{name}\" AssemblyFile=\"{Escape(builtIn)}\" />"));
            _project = Write(Path.Combine(root, "apphosts.proj"), $$"""
                <Project>
                  {{usingTasks}}
                  <UsingTask TaskName="CreateAppHost" AssemblyFile="{{Escape(BuildTasksAssembly)}}" />
                  <UsingTask TaskName="CompleteIncrementalLayout" AssemblyFile="{{Escape(sdkTasks)}}" />
                  <PropertyGroup>
                    <OutputPath>{{Escape(OutputDirectory + Path.DirectorySeparatorChar)}}</OutputPath>
                    <IntermediateOutputPath>{{Escape(Path.Combine(root, "obj") + Path.DirectorySeparatorChar)}}</IntermediateOutputPath>
                    <NuGetPackageRoot>{{Escape(Path.Combine(root, "packages") + Path.DirectorySeparatorChar)}}</NuGetPackageRoot>
                    <SdkTasksAssembly>{{Escape(sdkTasks)}}</SdkTasksAssembly>
                    <MicrosoftNETBuildTasksAssembly>{{Escape(BuildTasksAssembly)}}</MicrosoftNETBuildTasksAssembly>
                    <MicrosoftNETBuildTasksDirectory>{{Escape(Path.GetDirectoryName(BuildTasksAssembly)! + Path.DirectorySeparatorChar)}}</MicrosoftNETBuildTasksDirectory>
                    <MicrosoftNETCoreAppHostPackageVersion>1.0.0</MicrosoftNETCoreAppHostPackageVersion>
                    <MicrosoftNetCompilersToolsetVersion>1.0.0</MicrosoftNetCompilersToolsetVersion>
                    <MicrosoftBuildVersion>1.0.0</MicrosoftBuildVersion>
                    <SharedFrameworkRid>{{Rid}}</SharedFrameworkRid>
                    <ExeExtension>{{extension}}</ExeExtension>
                  </PropertyGroup>
                  <ItemGroup>
                    <_RoslynAppHost Include="$(OutputPath)Roslyn/bincore/csc.dll;$(OutputPath)Roslyn/bincore/vbc.dll;$(OutputPath)Roslyn/bincore/VBCSCompiler.dll" />
                    <_RoslynAppHost Include="$(OutputPath)Roslyn/bincore/$(AdditionalCompiler).dll" Condition="'$(AdditionalCompiler)' != ''" />
                    <_MSBuildAppHost Include="$(OutputPath)MSBuild.dll" />
                  </ItemGroup>
                  <Import Project="GenerateLayout.targets" />
                </Project>
                """);
        }

        public string Root { get; }
        public string BuildTasksAssembly { get; }
        public string Rid => _simulateSigning ? "osx-test" : RuntimeInformation.RuntimeIdentifier;
        public string OutputDirectory => Path.Combine(Root, "out");
        public string RoslynPackageDirectory => Path.Combine(Root, "packages", "microsoft.net.compilers.toolset", "1.0.0", "tasks", "netcore", "bincore");
        public string Template => TemplateAt(Path.Combine(Root, "packages"), Rid, "1.0.0", Extension);
        public string TemplateAt(string packages, string rid, string version, string extension) =>
            Path.Combine(packages, "microsoft.netcore.app.host." + rid, version, "runtimes", rid, "native", "apphost" + extension);
        public string[] Hosts => HostsAt(OutputDirectory, Extension);
        public string[] HostsAt(string output, string extension) =>
        [
            Path.Combine(output, "Roslyn", "bincore", "csc" + extension),
            Path.Combine(output, "Roslyn", "bincore", "vbc" + extension),
            Path.Combine(output, "Roslyn", "bincore", "VBCSCompiler" + extension),
            Path.Combine(output, "MSBuild" + extension)
        ];
        public static string Extension => OperatingSystem.IsWindows() ? ".exe" : "";
        public string Output(params string[] parts) => Path.Combine([OutputDirectory, .. parts]);
        public string ManagedInput(string name) => name == "MSBuild" ? Output(name + ".dll") : Path.Combine(RoslynPackageDirectory, name + ".dll");
        public string Completion(string name) => Path.Combine(Root, "obj", "incremental-layout", "apphosts",
            (name == "MSBuild" ? "msbuild-" : "roslyn-") + name + ".complete");

        public Observation Build(Dictionary<string, string>? properties = null, bool success = true,
            string targets = "PublishRoslyn;CreateMSBuildAppHost")
        {
            string signingLog = Path.Combine(Root, "signing.log");
            int signaturesBefore = File.Exists(signingLog) ? File.ReadAllLines(signingLog).Length : 0;
            using var collection = new ProjectCollection(properties ?? []);
            ProjectInstance instance = collection.LoadProject(_project).CreateProjectInstance();
            var logger = new AppHostLogger();
            string binlog = Path.Combine(Root, $"build-{Guid.NewGuid():N}.binlog");
            using var manager = new BuildManager();
            BuildResult result = manager.Build(new BuildParameters(collection)
                {
                    Loggers = [logger, new BinaryLogger { Parameters = binlog }]
                },
                new BuildRequestData(instance, targets.Split(';')));
            (result.OverallResult == BuildResultCode.Success).Should().Be(success,
                "MSBuild log:{0}{1}{0}Binlog: {2}", Environment.NewLine, logger.Text, binlog);
            int signatures = _simulateSigning
                ? (File.Exists(signingLog) ? File.ReadAllLines(signingLog).Length : 0) - signaturesBefore
                : logger.SigningOperations;
            return new Observation(logger.CreatedHosts, signatures);
        }
    }

    private sealed class AppHostLogger : Logger
    {
        private readonly List<string> _messages = [];
        public int CreatedHosts { get; private set; }
        public int SigningOperations { get; private set; }
        public string Text => string.Join(Environment.NewLine, _messages);

        public override void Initialize(IEventSource source)
        {
            source.TaskStarted += (_, args) =>
            {
                string name = args.TaskName.Split('.').Last();
                CreatedHosts += name == "CreateAppHost" ? 1 : 0;
                SigningOperations += name == "Exec" ? 1 : 0;
            };
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
