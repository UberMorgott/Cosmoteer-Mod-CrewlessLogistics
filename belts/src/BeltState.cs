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
    public const int ProtocolVersion = 2;

    public const string BeltId = "morgott.crewless_logistics.belt";
    public const string SplitterId = "morgott.crewless_logistics.belt_splitter";
    public const string ConnectorId = "morgott.crewless_logistics.belt_connector";
    public const string FilterToggleId = "morgott.crewless_logistics.belt_filter";

    private static readonly ConditionalWeakTable<Part, NodeState> States = new();

    public static NodeKind? KindOf(Part part) => part.Rules.ID.ToString() switch
    {
        BeltId => NodeKind.Belt,
        SplitterId => NodeKind.Splitter,
        ConnectorId => NodeKind.Connector,
        _ => null,
    };

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
