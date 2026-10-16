// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.Build.Tasks;

namespace Microsoft.CoreSdkTasks.Tests;

[TestClass]
public class RemoveAssetFromDepsPackagesTests : SdkTest
{
    private const string SampleDepsJson = """
        {
          "targets": {
            ".NETCoreApp,Version=v9.0": {
              "MyApp/1.0.0": {
                "runtime": {
                  "MyApp.dll": {},
                  "Helper.dll": {}
                },
                "resources": {
                  "en/MyApp.resources.dll": {}
                }
              }
            }
          }
        }
        """;

    [TestMethod]
    public void ItRemovesSpecificAssetFromDeps()
    {
      var dir = TestAssetsManager.CreateTestDirectory().Path;
        var depsPath = Path.Combine(dir, "specific.deps.json");
        File.WriteAllText(depsPath, SampleDepsJson);

        RemoveAssetFromDepsPackages.DoRemoveAssetFromDepsPackages(depsPath, "runtime", "Helper.dll");

        var result = File.ReadAllText(depsPath);
        result.Should().NotContain("Helper.dll");
        result.Should().Contain("MyApp.dll");
    }

    [TestMethod]
    public void ItRemovesWildcardSection()
    {
      var dir = TestAssetsManager.CreateTestDirectory().Path;
        var depsPath = Path.Combine(dir, "wildcard.deps.json");
        File.WriteAllText(depsPath, SampleDepsJson);

        RemoveAssetFromDepsPackages.DoRemoveAssetFromDepsPackages(depsPath, "resources", "*");

        var result = File.ReadAllText(depsPath);
        result.Should().NotContain("resources");
        result.Should().Contain("runtime");
    }

    [TestMethod]
    public void ItDoesNotModifyFileWhenAssetNotFound()
    {
      var dir = TestAssetsManager.CreateTestDirectory().Path;
        var depsPath = Path.Combine(dir, "noop.deps.json");
        File.WriteAllText(depsPath, SampleDepsJson);
        var originalContent = File.ReadAllText(depsPath);

        RemoveAssetFromDepsPackages.DoRemoveAssetFromDepsPackages(depsPath, "runtime", "NonExistent.dll");

        // File should not be rewritten when nothing was found
        File.ReadAllText(depsPath).Should().Be(originalContent);
    }

    /// <summary>
    /// Verifies filtering to a separate destination leaves raw deps untouched, writes
    /// stable results even when no asset matches, and repairs a missing output.
    /// </summary>
    [TestMethod]
    [DataRow("runtime", "Helper.dll")]
    [DataRow("runtime", "Missing.dll")]
    [DataRow("resources", "*")]
    public void SeparateDestinationsPreserveRawInputsAndStableResults(string section, string asset)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: Guid.NewGuid().ToString("N")).Path;
        string source = Path.Combine(directory, "raw.deps.json");
        string destination = Path.Combine(directory, "final", "app.deps.json");
        File.WriteAllText(source, SampleDepsJson);
        byte[] raw = File.ReadAllBytes(source);
        DateTime sourceTimestamp = File.GetLastWriteTimeUtc(source);

        RemoveAssetFromDepsPackages.DoRemoveAssetFromDepsPackages(source, section, asset, destination);
        byte[] expected = File.ReadAllBytes(destination);
        File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddMinutes(-5));
        DateTime timestamp = File.GetLastWriteTimeUtc(destination);
        RemoveAssetFromDepsPackages.DoRemoveAssetFromDepsPackages(source, section, asset, destination);
        File.GetLastWriteTimeUtc(destination).Should().Be(timestamp);
        File.Delete(destination);
        RemoveAssetFromDepsPackages.DoRemoveAssetFromDepsPackages(source, section, asset, destination);
        File.ReadAllBytes(destination).Should().Equal(expected);
        File.ReadAllBytes(source).Should().Equal(raw);
        File.GetLastWriteTimeUtc(source).Should().Be(sourceTimestamp);
    }
}
