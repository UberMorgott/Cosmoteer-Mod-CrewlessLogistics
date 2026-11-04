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
        Log.Info(Patcher.Enabled ? "belt sim and automatic network coverage enabled" : "belt sim and automatic network coverage DISABLED (patches rolled back): devices get no cable sockets, game runs vanilla");
    }
}
