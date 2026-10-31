using System.Reflection;
using System.Runtime.Loader;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)] // benches measure per-thread alloc and wall time

namespace CrewlessLogistics.Belts.Tests;

/// <summary>Runs a probe written against a <c>check(ok, what)</c> callback: every line goes to the test output, any FAIL fails the test.</summary>
static class Probe
{
    public static void Run(Action<Action<bool, string>> probe)
    {
        var failed = new List<string>();
        probe((ok, what) =>
        {
            TestContext.Current.TestOutputHelper?.WriteLine((ok ? "PASS " : "FAIL ") + what);
            if (!ok)
            {
                failed.Add(what);
            }
        });
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    public static void Info(string what) => TestContext.Current.TestOutputHelper?.WriteLine("INFO " + what);
}

/// <summary>Installed game, mod loader and repo locations; game-backed tests skip without the game.</summary>
static class Env
{
    public const string GameRoot = @"D:\Steam\steamapps\common\Cosmoteer";
    public const string ModLoaderDir = @"D:\Steam\steamapps\workshop\content\799600\3577650065";
    static string GameBin => Path.Combine(GameRoot, "Bin");

    public static string ProjectRoot { get; } = FindProjectRoot();

    static string FindProjectRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CrewlessLogistics.sln")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("CrewlessLogistics.sln not found above " + AppContext.BaseDirectory);
    }

    public static void RequireGame() => Assert.SkipUnless(File.Exists(Path.Combine(GameBin, "Cosmoteer.dll")), "Cosmoteer not installed at " + GameRoot);

    static Assembly? _mod;

    /// <summary>The mod DLL on this runtime, with game assemblies from the game folder and Harmony from the loader (the only build it accepts in game).</summary>
    public static Assembly Mod()
    {
        RequireGame();
        Assert.SkipUnless(File.Exists(Path.Combine(ModLoaderDir, "0Harmony.dll")), "Yet Another Mod Loader not installed at " + ModLoaderDir);
        if (_mod == null)
        {
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string path = Path.Combine(name.Name == "0Harmony" ? ModLoaderDir : GameBin, name.Name + ".dll");
                return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
            };
            _mod = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "CrewlessLogistics.Belts.dll"));
        }
        return _mod;
    }

    public static List<(bool Ok, string What)> SelfTest(string type, params object[] args)
        => (List<(bool, string)>)Mod().GetType("CrewlessLogistics.Belts." + type, true)!.GetMethod("SelfTest", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args)!;
}
