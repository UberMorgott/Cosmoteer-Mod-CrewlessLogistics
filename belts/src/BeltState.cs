using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships.Parts;
using Halfling.Serialization.Generic;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Part -> <see cref="NodeState"/> (weak table), so contents move with the part on ship split (§3).
/// Saved as versioned subtree Components/morgott.belt (§10); vanilla ignores the unknown path.
/// </summary>
internal static class BeltState
{
    public const string PayloadPath = "Components/morgott.belt";
    /// <summary>Integrity-hash protocol (§10); bump with any sim/payload change.</summary>
    public const int ProtocolVersion = 7;

    public const string BeltId = "morgott.crewless_logistics.belt";
    public const string SplitterId = "morgott.crewless_logistics.belt_splitter";
    public const string ConnectorId = "morgott.crewless_logistics.belt_connector";
    /// <summary>
    /// 1x1 straight belt over a heat pipe or an electric cable (network on the part Up/Down axis, §25). The former
    /// belt_heat_crossing / belt_cable_crossing IDs load as this part through its OtherIDs.
    /// </summary>
    public const string CrossingId = "morgott.crewless_logistics.belt_crossing";
    public const string FilterToggleId = "morgott.crewless_logistics.belt_filter";
    /// <summary>§23 splitter / connector settings (mod/belt_*_priority_toggle.rules, belt_splitter_filter_toggle.rules).</summary>
    public const string InputPriorityToggleId = "morgott.crewless_logistics.belt_input_priority";
    public const string OutputPriorityToggleId = "morgott.crewless_logistics.belt_output_priority";
    public const string SplitterFilterToggleId = "morgott.crewless_logistics.belt_splitter_filter";
    public const string SourcePriorityToggleId = "morgott.crewless_logistics.belt_source_priority";

    private static readonly ConditionalWeakTable<Part, NodeState> States = new();

    public static NodeKind? KindOf(Part part) => KindOf(part.Rules);

    public static NodeKind? KindOf(PartRules rules) => rules.ID.ToString() switch
    {
        BeltId or CrossingId => NodeKind.Belt,
        SplitterId => NodeKind.Splitter,
        ConnectorId => NodeKind.Connector,
        _ => null,
    };

    /// <summary>Crossings are belts that never auto-curve and run along the part Left/Right axis (front = part cell (1, 0)).</summary>
    public static bool IsCrossing(Part part) => IsCrossing(part.Rules);

    public static bool IsCrossing(PartRules rules) => rules.ID.ToString() == CrossingId;

    public static bool TryGet(Part part, [NotNullWhen(true)] out NodeState? state) => States.TryGetValue(part, out state);

    public static NodeState GetOrCreate(Part part, NodeKind kind) => States.GetValue(part, _ => new NodeState(kind));

    public static void Read(Part part, GenericSerialReader r)
    {
        if (KindOf(part) is not { } kind)
        {
            return;
        }
        int version = r.ReadOptionalFromPath("V", 0);
        NodeState? s = version == NodeState.PayloadVersion ? NodeState.Decode(kind, r.ReadOptionalFromPath<int[]?>("D", null)) : null;
        if (s == null)
        {
            Log.Info($"payload v{version} on {part.Rules.ID} invalid or unsupported, contents dropped");
            return;
        }
        States.AddOrUpdate(part, s);
    }

    public static void Write(NodeState s, GenericSerialWriter w)
    {
        w.WriteToPath("V", NodeState.PayloadVersion);
        w.WriteToPath("D", s.Encode());
    }

    public static void Hash(NodeState s, IntegrityHash h) => h.AddHash(s.Digest());
}
