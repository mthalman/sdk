namespace ForkBlockerRetryScenarios;

public static class InitialSites
{
    // Deliberate line shift for the append scenario.

    public static string Second()
    {
        // TODO: Remove this temporary fallback after https://github.com/mthalman/sdk/issues/35 is fixed.
        return "temporary fallback";
    }

    public static string SeparateA()
    {
        // TODO: Remove this temporary fallback after https://github.com/mthalman/sdk/issues/35 is fixed.
        return "temporary fallback";
    }

    public static string SeparateB()
    {
        // TODO: Remove this temporary fallback after https://github.com/mthalman/sdk/issues/36 is fixed.
        return "temporary fallback";
    }

    public static string CombinedA()
    {
        // TODO: Remove this temporary fallback only after both https://github.com/mthalman/sdk/issues/35 and https://github.com/mthalman/sdk/issues/36 are fixed.
        return "temporary fallback";
    }
}
