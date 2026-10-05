using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ForkBlockerReferenceScenarios.Tests;

[TestClass]
public sealed class IgnoreSites
{
    [TestMethod]
    [Ignore("https://github.com/mthalman/sdk/issues/33")]
    public void IgnoredA()
    {
    }
}
