using System;
using System.Collections.Generic;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Resources;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.UI;
using Halfling.Geometry;

namespace CrewlessLogistics.Belts;

/// <summary>
/// What a part can take in / give out, from its rules only (docs/BELTS_DESIGN.md §20): the engine's own crew-supply
/// classification per component (<see cref="PartComponentRules.CanBeResourceSupplierTargetForTypes"/> = inputs,
/// <see cref="PartComponentRules.CanBeResourceSupplierForTypes"/> = outputs, <see cref="ResourceRules.Stackable"/> =
/// generic storage), over the components the engine actually creates (Part.cs:778-805) with ToggledComponents expanded
/// only in their current mode. Bit i = <see cref="BeltTypes.Ids"/>[i].
/// </summary>
internal static class PartFlow
{
    public const uint All = BeltTypes.AllMask;

    static PartFlow()
    {
        if (BeltTypes.Ids.Length != 21)
        {
            throw new InvalidOperationException("BeltTypes.AllMask must cover BeltTypes.Ids");
        }
    }

    public static (uint In, uint Out) Classify(PartRules rules, int rotation, bool flipX, Func<ID<PartToggleGuiRules>, int?> mode)
    {
        uint i = 0, o = 0;
        Walk(rules, rules.PhysicalComponents, rotation, flipX, top: true, mode, ref i, ref o);
        return (i, o);
    }

    /// <summary>Toggle modes of a live part (sim state, identical on every peer).</summary>
    public static Func<ID<PartToggleGuiRules>, int?> LiveModes(Part part) => id => PartUIToggle.GetOnPart(part, id)?.Mode;

    /// <summary>Toggle modes of a build-mode blueprint part (BaseBuildState.GetToggleMode).</summary>
    public static Func<ID<PartToggleGuiRules>, int?> BlueprintModes(CommonBasePart part, bool editingBlueprints) =>
        id => part.Ship?.Metadata.PartUIToggleStates.Get((new PartSaveData(part), id), editingBlueprints);

    private static void Walk(PartRules r, IEnumerable<PartComponentRules> list, int rot, bool flip, bool top, Func<ID<PartToggleGuiRules>, int?> mode, ref uint i, ref uint o)
    {
        foreach (PartComponentRules c in list)
        {
            // Same filters as Part.cs:778-805 (top level) and PartToggledComponents.cs:118 (nested).
            if ((c.OnlyWithRotation is { } cr && cr != rot) || (c.OnlyWithFlip is { } cf && cf != flip) || (top && !c.IncludeWhenNotUnderConstruction))
            {
                continue;
            }
            if (c is PartToggledComponentsRules t)
            {
                // Its own CanBe* unions every mode (PartToggledComponentsRules.cs:58-63): expand the active mode only.
                if (IsOn(r, t, mode))
                {
                    Walk(r, t.Components, rot, flip, top: false, mode, ref i, ref o);
                }
                continue;
            }
            i |= Mask(c.CanBeResourceSupplierTargetForTypes);
            o |= Mask(c.CanBeResourceSupplierForTypes);
        }
    }

    private static bool IsOn(PartRules r, PartToggledComponentsRules t, Func<ID<PartToggleGuiRules>, int?> mode)
    {
        try
        {
            return t.IsBlueprintToggleOn(r, mode);
        }
        catch (InvalidOperationException)
        {
            return true; // toggle without blueprint state (status/trigger driven): may be on, count its types
        }
    }

    private static uint Mask(IEnumerable<ID<ResourceRules>> ids)
    {
        uint m = 0;
        foreach (ID<ResourceRules> id in ids)
        {
            if (id == ResourceRules.Stackable)
            {
                return All;
            }
            int k = PartRoom.IndexOf(id);
            if (k >= 0)
            {
                m |= 1u << k;
            }
        }
        return m;
    }

    private static IntVector2 Cell(CommonBasePart p, int x, int y) => p.Rules.GetShipRelativeCell(new IntVector2(x, y), p.Location, p.Rotation, p.FlipX);

    /// <summary>Front direction of a belt part, as BeltManager.Rebuild (crossings run along part x).</summary>
    private static IntVector2 Dir(CommonBasePart p) => Cell(p, BeltState.IsCrossing(p.Rules) ? 1 : 0, BeltState.IsCrossing(p.Rules) ? 0 : -1) - Cell(p, 0, 0);

    /// <summary>
    /// Allowed set of a connector (§20 D2, §21 D8) on any parts manager: live ship (<paramref name="isSite"/> = under
    /// construction) or build-mode blueprint. Source = union of Out(room) over the back, left and right rooms; it does not
    /// narrow when a belt output feeds the connector there (or a belt / site stands at the back) or no room has flows.
    /// </summary>
    public static uint ForConnector(CommonBasePart conn, Func<IntVector2, CommonBasePart?> at, Func<CommonBasePart, bool> isSite,
        Func<CommonBasePart, Func<ID<PartToggleGuiRules>, int?>> modes)
    {
        IntVector2 c = Cell(conn, 0, 0), d = Dir(conn);
        uint src = 0;
        bool any = false;
        foreach (IntVector2 cell in new[] { c - d, c + new IntVector2(-d.Y, d.X), c + new IntVector2(d.Y, -d.X) })
        {
            bool back = cell == c - d;
            if (at(cell) is not { } p)
            {
                continue;
            }
            if (isSite(p) || BeltState.KindOf(p.Rules) != null)
            {
                any |= back || SideFeeds(c, cell, at, isSite);
                continue;
            }
            src |= Classify(p.Rules, p.Rotation, p.FlipX, modes(p)).Out;
        }
        IntVector2 f = c + d;
        uint frontIn = at(f) is { } q && !isSite(q) && BeltState.KindOf(q.Rules) == null ? Classify(q.Rules, q.Rotation, q.FlipX, modes(q)).In : All;
        return Allowed(any ? All : src, frontIn);
    }
    /// <summary>A built belt/splitter/connector output at <paramref name="side"/> points into <paramref name="c"/> (sites are not in the topology).</summary>
    private static bool SideFeeds(IntVector2 c, IntVector2 side, Func<IntVector2, CommonBasePart?> at, Func<CommonBasePart, bool> isSite)
    {
        if (at(side) is not { } q || isSite(q) || BeltState.KindOf(q.Rules) is not { } kind)
        {
            return false;
        }
        IntVector2 d = Dir(q);
        return Cell(q, 0, 0) + d == c || (kind == NodeKind.Splitter && Cell(q, 1, 0) + d == c);
    }

    /// <summary>
    /// Types a connector may pass (§20 D2): Out(back part) and In(front part); a belt/empty/construction side or a part
    /// without flows does not narrow; two buildings without a common type give 0 (only "none").
    /// </summary>
    public static uint Allowed(uint backOut, uint frontIn) => (backOut == 0 ? All : backOut) & (frontIn == 0 ? All : frontIn);

    /// <summary>Filter choices offered (§20 D5, §22): Auto and None always; all -> every choice; one type -> it; else any + the set.</summary>
    public static bool ChoiceAllowed(uint allowed, int mode)
    {
        if (allowed == All || mode == BeltTypes.ModeAuto || mode == BeltTypes.ModeNone)
        {
            return true;
        }
        int f = BeltTypes.FilterFromMode(mode);
        return f >= 0 ? (allowed >> f & 1) != 0 : mode == BeltTypes.ModeAny && allowed != 0 && !IsSingle(allowed);
    }

    /// <summary>Normalised toggle mode (§22): a choice the neighbours rule out -> Auto; else unchanged (Auto and None are never rewritten).</summary>
    public static int Normalize(uint allowed, int mode) => ChoiceAllowed(allowed, mode) ? mode : BeltTypes.ModeAuto;

    /// <summary>Sim filter of a normalised mode (§22): Auto passes the single allowed type, else nothing; other modes as chosen.</summary>
    public static int Filter(uint allowed, int mode) => mode != BeltTypes.ModeAuto ? BeltTypes.FilterFromMode(mode)
        : IsSingle(allowed) ? System.Numerics.BitOperations.TrailingZeroCount(allowed) : BeltTypes.FilterNone;

    private static bool IsSingle(uint m) => m != 0 && (m & (m - 1)) == 0;
}
