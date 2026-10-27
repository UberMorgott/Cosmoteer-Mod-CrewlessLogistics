using Xunit;

namespace CrewlessLogistics.Belts.Tests;

/// <summary>Pure belt world probes (topology, splitter, connector, render, save payload); see <see cref="WorldProbes"/>.</summary>
public class WorldTests
{
    [Fact]
    public void AutoCurve() => Probe.Run(WorldProbes.AutoCurve);

    [Fact]
    public void Crossings() => Probe.Run(WorldProbes.Crossings);

    [Fact]
    public void ItemRender() => Probe.Run(WorldProbes.ItemRender);

    [Fact]
    public void Sideload() => Probe.Run(WorldProbes.Sideload);

    [Fact]
    public void SplitterSplitMerge() => Probe.Run(WorldProbes.SplitterSplitMerge);

    [Fact]
    public void Connectors() => Probe.Run(WorldProbes.Connectors);

    [Fact]
    public void AdjacencyMatrix() => Probe.Run(WorldProbes.AdjacencyMatrix);

    [Fact]
    public void Chains() => Probe.Run(WorldProbes.Chains);

    [Fact]
    public void SaveRoundTrip() => Probe.Run(WorldProbes.SaveRoundTrip);

    [Fact]
    public void FilterChoicesMatchRules() => Probe.Run(WorldProbes.FilterChoicesMatchRules);

    [Fact]
    public void RenderSelect() => Probe.Run(WorldProbes.RenderSelect);

    [Fact]
    public void SplitterHalvesRender() => Probe.Run(WorldProbes.SplitterHalvesRender);

    [Fact]
    public void RenderClientOnly() => Probe.Run(WorldProbes.RenderClientOnly);

    [Fact]
    public void RenderMoving() => Probe.Run(WorldProbes.RenderMoving);

    [Fact]
    public void MultiSideIntake() => Probe.Run(WorldProbes.MultiSideIntake);

    [Fact]
    public void SplitterInputPriority() => Probe.Run(WorldProbes.SplitterInputPriority);

    [Fact]
    public void SplitterOutputPriority() => Probe.Run(WorldProbes.SplitterOutputPriority);

    [Fact]
    public void SplitterFilter() => Probe.Run(WorldProbes.SplitterFilter);

    [Fact]
    public void DefaultsUnchanged() => Probe.Run(WorldProbes.DefaultsUnchanged);

    [Fact]
    public void ConnectorSourcePriority() => Probe.Run(WorldProbes.ConnectorSourcePriority);

    [Fact]
    public void SettingsOutsidePayload() => Probe.Run(WorldProbes.SettingsOutsidePayload);

    [Fact]
    public void ClaimCascade() => Probe.Run(WorldProbes.ClaimCascade);

    /// <summary>§26 belt surfaces switch damage frames at the vanilla thresholds (1/3, 2/3 of max health lost).</summary>
    [Fact]
    public void DamageLevels()
    {
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, new[] { 0f, .33f, .34f, .66f, .67f, 1f }.Select(f => BeltRenderState.DamageLevel(f, 3)).ToArray());
        Assert.Equal(0, BeltRenderState.DamageLevel(1f, 1)); // a single-level sprite never changes
    }

    /// <summary>§26 removal: a split keeps the items with the Part, everything else drops them (destroyed or intact).</summary>
    [Fact]
    public void RemovalDrops()
    {
        Assert.Equal(RemovalDrop.Keep, NodeState.OnRemoved(splitting: true, destroyed: false));
        Assert.Equal(RemovalDrop.Keep, NodeState.OnRemoved(splitting: true, destroyed: true));
        Assert.Equal(RemovalDrop.Destroyed, NodeState.OnRemoved(splitting: false, destroyed: true));
        Assert.Equal(RemovalDrop.Intact, NodeState.OnRemoved(splitting: false, destroyed: false));
    }

    /// <summary>
    /// §26 C3: the engine drops a destroyed / deconstructed part's own Resources (PartsManager.OnPartDestroyed); ours stay
    /// vanilla-like: belt and connector 4 steel + 2 coils, splitter twice that for 2 tiles, crossing = vanilla heat pipe
    /// crossing (inherited, no override).
    /// </summary>
    [Fact]
    public void PartCosts()
    {
        string Res(string file) => System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(Env.ProjectRoot, "mod", file)), @"\n\s*Resources = (\[.*\])").Groups[1].Value;
        Assert.Equal("[[steel, 4], [coil, 2]]", Res("belt.rules"));
        Assert.Equal("[[steel, 8], [coil, 4]]", Res("belt_splitter.rules"));
        Assert.Equal("", Res("belt_connector.rules")); // inherits belt.rules
        Assert.Equal("", Res("belt_crossing.rules")); // inherits the vanilla crossing
        Assert.Contains("Part : <belt.rules>/Part", File.ReadAllText(Path.Combine(Env.ProjectRoot, "mod", "belt_connector.rules")));
        Assert.Contains("heat_pipe_crossing.rules>/Part\n", File.ReadAllText(Path.Combine(Env.ProjectRoot, "mod", "belt_crossing.rules")).Replace("\r", ""));
    }

    /// <summary>§25 adaptive crossing art for every Up/Down link pair; belt.rules lists one surface per Variant, in order, each with its §26 damage levels 0 / 33 / 66.</summary>
    [Fact]
    public void CrossingVariants()
    {
        CrossLink N = CrossLink.None, H = CrossLink.Heat, C = CrossLink.Cable;
        var cases = new (CrossLink Up, CrossLink Down, Variant V)[]
        {
            (N, N, Variant.PlainCrossing), (H, N, Variant.HeatCrossing), (N, H, Variant.HeatCrossing), (H, H, Variant.HeatCrossing),
            (C, N, Variant.CableCrossing), (N, C, Variant.CableCrossing), (C, C, Variant.CableCrossing),
            (H, C, Variant.HeatCableCrossing), (C, H, Variant.CableHeatCrossing),
        };
        foreach (var (up, down, v) in cases)
        {
            Assert.Equal(v, BeltRenderState.CrossingVariant(up, down));
        }
        string rules = File.ReadAllText(Path.Combine(Env.ProjectRoot, "mod", "belt.rules"));
        var listed = System.Text.RegularExpressions.Regex.Matches(rules, @"\[ \{ AnimationFiles \[ ""assets/belt_anim/(\w+)_00\.png"".*_33_00\.png.*_66_00\.png").Select(m => m.Groups[1].Value).ToArray();
        string[] expected = { "straight", "join_l", "join_r", "join_lr", "curve", "heat_crossing", "cable_crossing", "plain_crossing", "heat_cable_crossing", "cable_heat_crossing" };
        Assert.Equal(expected.Length, Enum.GetValues<Variant>().Length);
        Assert.Equal(expected, listed);
    }
}
