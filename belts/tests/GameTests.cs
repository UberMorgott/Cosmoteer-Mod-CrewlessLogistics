using Xunit;

namespace CrewlessLogistics.Belts.Tests;

/// <summary>Probes against the installed game: P1 patch targets + loader Harmony, §20 part-flow classification on real Data, rules R1.</summary>
public class GameTests
{
    static void Report(List<(bool Ok, string What)> results) => Probe.Run(check =>
    {
        foreach (var (ok, what) in results)
        {
            check(ok, what);
        }
    });

    [Fact]
    public void P1PatchesApplyVerifyAndRollBack() => Report(Env.SelfTest("Patcher"));

    [Fact]
    public void PartFlowClassifiesVanillaParts() => Report(Env.SelfTest("PartFlowProbe", Env.GameRoot));

    [Fact]
    public void R1NestedComponentIdsUnique()
    {
        Env.RequireGame();
        Probe.Run(check => RulesProbes.Run(check, Env.GameRoot, Env.ProjectRoot));
    }

    /// <summary>Runs without the game too (mod-local references only); with it, vanilla "./Data/" references as well.</summary>
    [Fact]
    public void R2RulesFileListsAndAssets() =>
        Probe.Run(check => RulesProbes.Assets(check, File.Exists(Path.Combine(Env.GameRoot, "Bin", "Cosmoteer.dll")) ? Env.GameRoot : null, Env.ProjectRoot));
}
