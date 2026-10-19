// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
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
    /// Verifies source edits/reverts and deleted destinations copy only the affected
    /// file, without rewriting siblings or disturbing unowned output.
    /// </summary>
    [TestMethod]
    public void ChangedAndMissingFilesAreRepairedIndependently()
    {
        CopyProject project = CreateProject("PublishMSBuildExtensions");
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
    public void NewPayloadFilesAreDiscoveredOnWarmBuilds()
    {
        CopyProject project = CreateProject("PublishMSBuildExtensions");
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

    private sealed record Observation(int Copies, int Publishes, string Text);

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
            File.Copy(LayoutFiles.GetPath("GenerateLayout.targets",
                Path.Combine("src", "Layout", "redist", "targets", "GenerateLayout.targets")),
                Path.Combine(root, "GenerateLayout.targets"));
            _project = Write(Path.Combine(root, "copy-layout.proj"), $$"""
                <Project>
                  {{usingTasks}}
                  <PropertyGroup>
                    <RepoRoot>{{Escape(Path.Combine(root, "repo") + Path.DirectorySeparatorChar)}}</RepoRoot>
                    <OutputPath>{{Escape(OutputDirectory + Path.DirectorySeparatorChar)}}</OutputPath>
                    <ArtifactsBinDir>{{Escape(Path.Combine(root, "bin") + Path.DirectorySeparatorChar)}}</ArtifactsBinDir>
                    <Configuration>Debug</Configuration>
                    <SdkTargetFramework>{{_framework}}</SdkTargetFramework>
                    <NuGetPackageRoot>{{Escape(PackageRoot + Path.DirectorySeparatorChar)}}</NuGetPackageRoot>
                    <MicrosoftNETCoreAppHostPackageVersion>1.0.0</MicrosoftNETCoreAppHostPackageVersion>
                    <SharedFrameworkRid>linux-x64</SharedFrameworkRid>
                    <ExeExtension>{{_extension}}</ExeExtension>
                  </PropertyGroup>
                  <Import Project="GenerateLayout.targets" />
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
                case "LayoutAppHostTemplate":
                    Write(AppHostSource, "linux-x64 1.0.0");
                    Destinations = [Destination];
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
        public string Source => _target switch
        {
            "PublishContainersSdk" => Path.Combine(Root, "repo", "src", "Containers", "packaging", "build", "Microsoft.NET.Build.Containers.props"),
            "PublishMSBuildExtensions" => Path.Combine(Root, "MSBuildImports", "import.props"),
            "LayoutAppHostTemplate" => AppHostSource,
            _ => throw new InvalidOperationException("Unknown copy target.")
        };
        public string Destination => _target switch
        {
            "PublishContainersSdk" => Output("Containers", "build", "Microsoft.NET.Build.Containers.props"),
            "PublishMSBuildExtensions" => Output("import.props"),
            "LayoutAppHostTemplate" => Output("AppHostTemplate", "apphost" + _extension),
            _ => throw new InvalidOperationException("Unknown copy target.")
        };
        private string AppHostSource => Path.Combine(PackageRoot, "microsoft.netcore.app.host.linux-x64",
            "1.0.0", "runtimes", "linux-x64", "native", "apphost" + _extension);
        public string ContainerTask => Output("Containers", "tasks", _framework, "container-task.txt");
        public string[] ExpectedFiles => _target == "PublishContainersSdk"
            ? [.. Destinations, ContainerTask, Output("unowned.keep")]
            : [.. Destinations, Output("unowned.keep")];
        public string Output(params string[] parts) => Path.Combine([OutputDirectory, .. parts]);

        public Observation Build(Dictionary<string, string>? properties = null, bool success = true)
        {
            using var collection = new ProjectCollection(properties ?? []);
            var logger = new CopyLogger();
            SdkLayoutBuild.Run(collection, _project, Root, [_target], logger, () => logger.Text, success);
            return new Observation(logger.Copies, logger.Publishes, logger.Text);
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
