namespace ForkBlockerRetryScenarios;

public static class InitialSites
{
    // Deliberate line shift for the append scenario.

    public static string Second()
    {
        // TODO: Remove this temporary fallback after https://github.com/mthalman/sdk/issues/35 is fixed.
        return "temporary fallback";
    }
}
