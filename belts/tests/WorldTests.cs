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
}
