// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Security;
using System.Text;
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
    /// Verifies changing the selected Roslyn package invalidates compiler apphosts
    /// even when the replacement package files have older timestamps.
    /// </summary>
    [TestMethod]
    public void ChangingRoslynPackageInvalidatesCompilerAppHosts()
    {
        AppHostProject project = CreateProject();
        project.Build();
        string alternatePackage = project.RoslynPackageDirectoryAt("2.0.0");
        foreach (string name in new[] { "csc", "vbc", "VBCSCompiler" })
        {
            string source = Path.Combine(project.RoslynPackageDirectory, name + ".dll");
            string destination = Path.Combine(alternatePackage, name + ".dll");
            Copy(source, destination);
            File.SetLastWriteTimeUtc(destination, DateTime.UnixEpoch);
        }

        Dictionary<string, string> properties = new()
        {
            ["MicrosoftNetCompilersToolsetVersion"] = "2.0.0"
        };
        project.Build(properties).CreatedHosts.Should().Be(3);
        project.Build(properties).CreatedHosts.Should().Be(0);
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

    private AppHostProject CreateProject() =>
        new(TestAssetsManager.CreateTestDirectory(identifier: Guid.NewGuid().ToString("N")).Path);

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

    private sealed record Observation(int CreatedHosts);

    private sealed class AppHostProject
    {
        private readonly string _project;

        public AppHostProject(string root)
        {
            Root = root;
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
            File.Copy(LayoutFiles.GetPath("GenerateLayout.targets",
                Path.Combine("src", "Layout", "redist", "targets", "GenerateLayout.targets")),
                Path.Combine(root, "GenerateLayout.targets"));
            foreach (string name in new[] { "roslyn-entitlements.plist", "msbuild-entitlements.plist" })
            {
                File.Copy(LayoutFiles.GetPath(name, Path.Combine("src", "Layout", "redist", name)),
                    Path.Combine(root, name));
            }
            string usingTasks = string.Join(Environment.NewLine,
                new[] { "Copy", "Delete", "WriteLinesToFile", "ReadLinesFromFile", "Error" }
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
        public string Rid => RuntimeInformation.RuntimeIdentifier;
        public string OutputDirectory => Path.Combine(Root, "out");
        public string RoslynPackageDirectory => RoslynPackageDirectoryAt("1.0.0");
        public string RoslynPackageDirectoryAt(string version) =>
            Path.Combine(Root, "packages", "microsoft.net.compilers.toolset", version, "tasks", "netcore", "bincore");
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

        public Observation Build(Dictionary<string, string>? properties = null,
            string targets = "PublishRoslyn;CreateMSBuildAppHost")
        {
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
            result.OverallResult.Should().Be(BuildResultCode.Success,
                "MSBuild log:{0}{1}{0}Binlog: {2}", Environment.NewLine, logger.Text, binlog);
            return new Observation(logger.CreatedHosts);
        }
    }

    private sealed class AppHostLogger : Logger
    {
        private readonly List<string> _messages = [];
        public int CreatedHosts { get; private set; }
        public string Text => string.Join(Environment.NewLine, _messages);

        public override void Initialize(IEventSource source)
        {
            source.TaskStarted += (_, args) =>
            {
                string name = args.TaskName.Split('.').Last();
                CreatedHosts += name == "CreateAppHost" ? 1 : 0;
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
