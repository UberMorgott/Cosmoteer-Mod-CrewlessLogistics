using System.Numerics;
using Xunit;

namespace CrewlessLogistics.Belts.Tests;

[CollectionDefinition(nameof(TypeTable), DisableParallelization = true)]
public sealed class TypeTable
{
}

/// <summary>
/// Modded resource types (§7): table order, masks past 32/64 types, filter mode remap and payload v3. They replace the
/// process-wide table, so they run alone and restore the vanilla table.
/// </summary>
[Collection(nameof(TypeTable))]
public sealed class TypesTests : IDisposable
{
    private static readonly int V = BeltTypes.Vanilla.Length;

    public void Dispose() => BeltTypes.Set(BeltTypes.Vanilla);

    private static string[] Modded(int n) => Enumerable.Range(0, n).Select(i => $"mod_{i:D3}").ToArray();

    [Fact]
    public void OrderKeepsVanillaThenSortsLowerCaseOrdinal()
    {
        string[] t = BeltTypes.Order(new[] { "Zeta", "iron", "alpha", "ALPHA", "Beta", "sulfur" });
        Assert.Equal(BeltTypes.Vanilla, t.Take(V));
        Assert.Equal(new[] { "alpha", "beta", "zeta" }, t.Skip(V));
        Assert.Equal(BeltTypes.Vanilla, BeltTypes.Order(Array.Empty<string>()));
    }

    [Fact]
    public void RepeatedLoadRebuildsTableMaskAndFingerprint()
    {
        int vanilla = BeltTypes.Fingerprint;
        BeltTypes.Set(BeltTypes.Order(new[] { "b", "a" }));
        Assert.Equal(V + 2, BeltTypes.Ids.Length);
        Assert.Equal((BigInteger.One << (V + 2)) - 1, BeltTypes.AllMask);
        int ab = BeltTypes.Fingerprint;
        BeltTypes.Set(BeltTypes.Order(new[] { "A", "B" }));
        Assert.Equal(ab, BeltTypes.Fingerprint); // same mods, any registry order or case -> same table
        BeltTypes.Set(BeltTypes.Order(new[] { "a" }));
        Assert.NotEqual(ab, BeltTypes.Fingerprint);
        BeltTypes.Set(BeltTypes.Order(Array.Empty<string>()));
        Assert.Equal(vanilla, BeltTypes.Fingerprint);
        Assert.Equal((BigInteger.One << V) - 1, BeltTypes.AllMask);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(20)]
    [InlineData(21)]
    public void MasksPastWordBoundaries(int type)
    {
        BeltTypes.Set(BeltTypes.Order(Modded(70)));
        BigInteger one = BigInteger.One << type, two = one | BigInteger.One << (type - 1);
        int mode = BeltTypes.ModeOf(type);
        Assert.Equal(type, BeltTypes.FilterFromMode(mode));
        Assert.Equal(type, BeltTypes.Filter(one, BeltTypes.ModeAuto));
        Assert.Equal(BeltTypes.FilterNone, BeltTypes.Filter(two, BeltTypes.ModeAuto));
        Assert.True(BeltTypes.ChoiceAllowed(one, mode));
        Assert.False(BeltTypes.ChoiceAllowed(one, BeltTypes.ModeOf(type - 1)));
        Assert.False(BeltTypes.ChoiceAllowed(one, BeltTypes.ModeAny));
        Assert.True(BeltTypes.ChoiceAllowed(two, BeltTypes.ModeAny));
        Assert.Equal(BeltTypes.ModeAuto, BeltTypes.Normalize(one, BeltTypes.ModeOf(type - 1)));
        Assert.Equal(one, BeltTypes.Allowed(BeltTypes.AllMask, one));
        Assert.Equal(one, BeltTypes.Allowed(two, one));
        Assert.Equal(BigInteger.Zero, BeltTypes.Allowed(one, BigInteger.One << (type + 1)));
        Assert.True(BeltTypes.ChoiceAllowed(BeltTypes.AllMask, BeltTypes.ModeOf(BeltTypes.Ids.Length - 1)));
        Assert.Equal(BeltTypes.FilterNone, BeltTypes.FilterFromMode(BeltTypes.ModeOf(BeltTypes.Ids.Length)));
    }

    [Fact]
    public void ConnectorModesKeepVanillaLayout()
    {
        BeltTypes.Set(BeltTypes.Order(Modded(3)));
        Assert.Equal(2, BeltTypes.ModeOf(0));
        Assert.Equal(22, BeltTypes.ModeOf(20));
        Assert.Equal(24, BeltTypes.ModeOf(21));
        Assert.Equal(BeltTypes.FilterNone, BeltTypes.FilterFromMode(BeltTypes.ModeNone));
        Assert.Equal(BeltTypes.FilterNone, BeltTypes.FilterFromMode(BeltTypes.ModeAuto));
        Assert.Equal(BeltTypes.FilterAny, BeltTypes.FilterFromMode(BeltTypes.ModeAny));
        for (int m = -1; m <= BeltTypes.ModeNone; m++)
        {
            Assert.Equal(m, BeltTypes.Remap(m, splitter: false, BeltTypes.Order(Modded(5))));
        }
        for (int m = -1; m <= V; m++)
        {
            Assert.Equal(m, BeltTypes.Remap(m, splitter: true, BeltTypes.Order(Modded(5))));
        }
    }

    [Fact]
    public void RemapFollowsIdsWhenOneIsInsertedBefore()
    {
        string[] saved = BeltTypes.Order(new[] { "b_res" });
        BeltTypes.Set(BeltTypes.Order(new[] { "a_res", "b_res" }));
        Assert.Equal(BeltTypes.ModeOf(V + 1), BeltTypes.Remap(BeltTypes.ModeOf(V), splitter: false, saved));
        Assert.Equal(V + 2, BeltTypes.Remap(V + 1, splitter: true, saved));
    }

    [Fact]
    public void RemovedTypeFilterBecomesNone()
    {
        string[] saved = BeltTypes.Order(new[] { "gone", "kept" });
        BeltTypes.Set(BeltTypes.Order(new[] { "kept" }));
        Assert.Equal(BeltTypes.ModeNone, BeltTypes.Remap(BeltTypes.ModeOf(V), splitter: false, saved)); // gone
        Assert.Equal(BeltTypes.ModeOf(V), BeltTypes.Remap(BeltTypes.ModeOf(V + 1), splitter: false, saved)); // kept moved down
        Assert.Equal(0, BeltTypes.Remap(V + 1, splitter: true, saved));
        Assert.Equal(BeltTypes.ModeNone, BeltTypes.Remap(BeltTypes.ModeOf(40), splitter: false, saved)); // past the saved table
        Assert.Equal(0, BeltTypes.Remap(40, splitter: true, saved));
    }

    private static NodeState Belt(params int[] lane0Types)
    {
        var s = new NodeState(NodeKind.Belt);
        LaneTile t = s.Tiles[0];
        for (int i = 0; i < lane0Types.Length; i++)
        {
            t.Pos[i] = LaneSim.EndPos - i * LaneSim.MinGap;
            t.Type[i] = lane0Types[i];
        }
        t.Count[0] = lane0Types.Length;
        return s;
    }

    [Fact]
    public void PayloadV3NamesFollowIdsAndDropOnlyUnknownItems()
    {
        BeltTypes.Set(BeltTypes.Order(new[] { "b_res", "gone" }));
        NodeState s = Belt(1, V, V + 1, 1); // iron, b_res, gone, iron
        s.Tiles[0].Drain[1] = V + 1; // gone held at the lane end
        var names = new List<string>();
        int[] d = s.Encode(names);
        Assert.Equal(new[] { "iron", "b_res", "gone" }, names);

        BeltTypes.Set(BeltTypes.Order(new[] { "a_res", "b_res" })); // a_res inserted before b_res, gone removed
        NodeState? r = NodeState.Decode(NodeKind.Belt, d, names.ToArray());
        Assert.NotNull(r);
        LaneTile t = r.Tiles[0];
        Assert.Equal(3, t.Count[0]);
        Assert.Equal(new[] { 1, V + 1, 1 }, t.Type.Take(3));
        Assert.Equal(new[] { LaneSim.EndPos, LaneSim.EndPos - LaneSim.MinGap, LaneSim.EndPos - 3 * LaneSim.MinGap }, t.Pos.Take(3));
        Assert.Equal(LaneTile.Closed, t.Drain[1]);

        Assert.Null(NodeState.Decode(NodeKind.Belt, d, new string?[] { "iron", null, "gone" })); // Codex P2: null entry, no NRE
        d[5] = 3; // a local type index past the name table
        Assert.Null(NodeState.Decode(NodeKind.Belt, d, names.ToArray()));
    }

    [Fact]
    public void RemapReadsNullSavedNamesAsGone()
    {
        BeltTypes.Set(BeltTypes.Order(new[] { "kept" }));
        Assert.Equal(BeltTypes.ModeNone, BeltTypes.Remap(BeltTypes.ModeOf(V), splitter: false, BeltTypes.Vanilla.Append(null).ToArray()));
    }

    [Fact]
    public void QuadPointFollowsNativeUvTransform()
    {
        // RotateQuadUVsAndTangents rot 1: the top-left vertex takes the bottom-left UV, so that texel shows top-left.
        Assert.Equal((-0.5f, -0.5f), BeltRenderState.QuadPoint((-0.5f, 0.5f), false, false, 1));
        // rot 3: the top-left vertex takes the top-right UV.
        Assert.Equal((-0.5f, -0.5f), BeltRenderState.QuadPoint((0.5f, -0.5f), false, false, 3));
        Assert.Equal((0.2f, 0.3f), BeltRenderState.QuadPoint((-0.2f, -0.3f), false, false, 2));
        Assert.Equal((0.2f, -0.3f), BeltRenderState.QuadPoint((-0.2f, -0.3f), true, false, 0));
        Assert.Equal((-0.2f, 0.3f), BeltRenderState.QuadPoint((-0.2f, -0.3f), false, true, 0));
        Assert.Equal((0.3f, 0.2f), BeltRenderState.QuadPoint((-0.2f, -0.3f), true, false, 1)); // mirror first, then rotate
        Assert.Equal((-0.2f, -0.3f), BeltRenderState.QuadPoint((-0.2f, -0.3f), false, false, 4)); // native rotates only 1..3
    }

    [Fact]
    public void PopoutWidthIsTheRoomBesideTheButton()
    {
        Assert.Equal(600f, BeltRenderState.PopoutWidth(0, 1000, 400, 450)); // Codex: window 1000, popout at x 400
        Assert.Equal(950f, BeltRenderState.PopoutWidth(0, 1000, 900, 950)); // near the right edge it opens to the left
        Assert.Equal(1000f, BeltRenderState.PopoutWidth(0, 1000, 0, 50));
    }

    [Fact]
    public void PopoutOpensOnASideItFits()
    {
        var (left, width) = BeltRenderState.PopoutLayout(0, 1000, 900, 950, 5000); // Codex: right-hand room only 100
        Assert.True(left);
        Assert.Equal(950f, width);
        Assert.False(BeltRenderState.PopoutLayout(0, 1000, 900, 950, 80).Left); // small popouts keep the vanilla side
        for (float x = 0; x <= 950; x += 25)
        {
            foreach (float desired in new[] { 60f, 500f, 999f, 5050f })
            {
                var (l, w) = BeltRenderState.PopoutLayout(0, 1000, x, x + 50, desired);
                Assert.True(w <= (l ? x + 50 : 1000 - x), $"button {x}, desired {desired}: {w} px on the {(l ? "left" : "right")}");
            }
        }
    }

    [Fact]
    public void StackSpriteContentCentre()
    {
        // iron_stack_1.png: one ingot at x 5..33, y 4..34 of 64 px; the shift must bring it to the cell centre.
        var (x, y) = BeltRenderState.ContentCentre(64, 64, (px, py) => (byte)(px is >= 5 and <= 33 && py is >= 4 and <= 34 ? 255 : 0));
        Assert.Equal((19.5f / 64) - 0.5f, x, 4);
        Assert.Equal((19.5f / 64) - 0.5f, y, 4);
        Assert.Equal((0f, 0f), BeltRenderState.ContentCentre(64, 64, (_, _) => 0));
        Assert.Equal((0f, 0f), BeltRenderState.ContentCentre(64, 64, (px, py) => (byte)(px is >= 16 and < 48 && py is >= 16 and < 48 ? 200 : 30)));
    }

    [Fact]
    public void PayloadV2ReadsTheVanillaTableOnly()
    {
        BeltTypes.Set(BeltTypes.Order(Modded(10)));
        int[] ok = Belt(V - 1).Encode(), bad = Belt(V).Encode();
        Assert.Equal(V - 1, NodeState.Decode(NodeKind.Belt, ok)?.Tiles[0].Type[0]);
        Assert.Null(NodeState.Decode(NodeKind.Belt, bad)); // v2 never held modded types, even with a longer table now
    }
}
