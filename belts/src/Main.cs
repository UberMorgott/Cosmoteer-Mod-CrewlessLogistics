namespace CrewlessLogistics.Belts;

/// <summary>Entry point called by Yet Another Mod Loader before game code runs.</summary>
public static class Main
{
    private static bool _initialized;

    public static void GameLoadInitializer()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        Log.Init();
        Patcher.Apply();
        Log.Info(Patcher.Enabled ? "belt sim enabled" : "belt sim DISABLED (patches rolled back), game runs vanilla");
    }
}
