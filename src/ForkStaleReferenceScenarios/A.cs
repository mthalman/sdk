namespace ForkStaleReferenceScenarios;

public static class Cases
{
    public static string A()
    {
        // TODO: Remove this fallback when https://github.com/mthalman/sdk/issues/26 is fixed.
        return "temporary fallback";
    }

    public static string B()
    {
        // TODO: Remove this fallback when https://github.com/mthalman/sdk/issues/26 is fixed.
        return "temporary fallback";
    }

    public static string C()
    {
        // TODO: Remove this fallback after https://github.com/mthalman/sdk/issues/27 is fixed, the fixed dependency version is consumed, and compatibility is verified.
        return "compatibility fallback";
    }
}
