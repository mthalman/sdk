namespace ForkBlockerRetryScenarios;

public static class CrossFileSites
{
    // Deliberate line shift for the completed-refile scenario.

    public static string Third()
    {
        // TODO: Remove this temporary fallback after https://github.com/mthalman/sdk/issues/35 is fixed.
        return "temporary fallback";
    }
}
