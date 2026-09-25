namespace GManager.Runtime.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GMANAGER_RUN_LIVE_TESTS") != "1")
            Skip = "Set GMANAGER_RUN_LIVE_TESTS=1 to run Google network/account tests explicitly.";
    }
}
