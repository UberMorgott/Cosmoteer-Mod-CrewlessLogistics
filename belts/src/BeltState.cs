using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships.Parts;
using Halfling.Serialization.Generic;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Per-part sim state; lives on the Part (weak table) so it moves with the part on ship split (§3).
/// Saved as versioned subtree Components/morgott.belt (§10); vanilla ignores the unknown path.
/// shortcut: probe payload is one tick counter; lane items, speed remainder, splitter/connector state replace it in step 3.
/// </summary>
internal sealed class BeltState
{
    public const string PayloadPath = "Components/morgott.belt";
    public const int ProtocolVersion = 1;

    private static readonly ConditionalWeakTable<Part, BeltState> States = new();

    public int Counter;

    public static bool TryGet(Part part, [NotNullWhen(true)] out BeltState? state) => States.TryGetValue(part, out state);

    public static BeltState GetOrCreate(Part part) => States.GetValue(part, _ => new BeltState());

    public static void Read(Part part, GenericSerialReader r)
    {
        int version = r.ReadOptionalFromPath("V", 0);
        if (version != ProtocolVersion)
        {
            Log.Info($"payload version {version} != {ProtocolVersion} on {part.Rules.ID}, dropped");
            return;
        }
        GetOrCreate(part).Counter = r.ReadOptionalFromPath("Counter", 0);
    }

    public void Write(GenericSerialWriter w)
    {
        w.WriteToPath("V", ProtocolVersion);
        w.WriteToPath("Counter", Counter);
    }

    public void Hash(IntegrityHash h) => h.AddHash(Counter);
}
