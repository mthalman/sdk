using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ForkBlockerRetryScenarios.Tests;

[TestClass]
public sealed class IgnoreSites
{
    [TestMethod]
    [Ignore("https://github.com/mthalman/sdk/issues/35")]
    public void IgnoredA()
    {
    }
}
