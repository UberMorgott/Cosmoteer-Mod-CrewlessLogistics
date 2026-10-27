using Cosmoteer.Data;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Networks;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Resources;
using Cosmoteer.Ships.Resources;

namespace CrewlessLogistics.Belts;

/// <summary>
/// A vanilla part as connector endpoint via its registered crew sources/sinks (§7). No cache: the live ship registry is
/// scanned per transfer (cargo tiles and launcher modes (un)register endpoints). Confirmed MP values only.
/// </summary>
internal sealed class PartRoom : IRoom
{
    private static ID<ResourceRules>[]? _ids;

    private readonly Part _part;
    private readonly Ship _ship;

    public PartRoom(Part part, Ship ship)
    {
        _part = part;
        _ship = ship;
    }

    public static ID<ResourceRules>[] Ids => _ids ??= System.Array.ConvertAll(BeltTypes.Ids, s => new ID<ResourceRules>(s));

    public static int IndexOf(ID<ResourceRules> id)
    {
        ID<ResourceRules>[] ids = Ids;
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] == id)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>P2 gate: storages and grid tiles only; never construction/repair/status sinks, airlocks or energy-network storages.</summary>
    private static bool IsEndpoint(object o) =>
        o is TypedResourceGrid.ResourceTile or FlexResourceGrid.ResourceTile
        || (o is BaseResourceStorage and not BasePartNetworkResourceStorage and not TypedResourceGrid);

    private IResourceSource? FindSource(int filter, IRoom? acceptor)
    {
        ResourceManager rm = _ship.Resources;
        foreach (ResourceManager.SourceInfo si in rm._sources)
        {
            IResourceSource s = si.Source;
            if (s.Part != _part || !IsEndpoint(s) || !s.SuppliesResources)
            {
                continue;
            }
            int type = IndexOf(s.ResourceType);
            if (type < 0 || (filter >= 0 && type != filter) || s.Resources - rm.GetAnticipatedPickUp(s, MPValueType.Confirmed) <= 0
                || (acceptor != null && !acceptor.CanAccept(type)))
            {
                continue;
            }
            return s;
        }
        return null;
    }

    public int FindSupply(int filter, IRoom? acceptor) => FindSource(filter, acceptor) is { } s ? IndexOf(s.ResourceType) : -1;

    public bool Take(int type) => FindSource(type, null)?.SubtractResources(1) == 1;

    /// <summary>Vanilla GetAllAvailableResourceSinks gates; best = highest sort priority, then most room.</summary>
    private IResourceSink? BestSink(int type)
    {
        ID<ResourceRules> id = Ids[type];
        ResourceManager rm = _ship.Resources;
        IResourceSink? best = null;
        int bestPrio = int.MinValue, bestCap = 0;
        foreach (ResourceManager.SinkInfo si in rm._sinks)
        {
            IResourceSink k = si.Sink;
            if (k.Part != _part || !IsEndpoint(k) || !k.CanReceive)
            {
                continue;
            }
            ID<ResourceRules> t = k.GetResourceType(MPValueType.Confirmed);
            if (t != id && t != default)
            {
                continue;
            }
            int cap = k.GetRemainingCapacity(id, false) - rm.GetAnticipatedDelivery(k, MPValueType.Confirmed);
            int prio = k.GetSortPriority(false, MPValueType.Confirmed);
            if (cap > 0 && (prio > bestPrio || (prio == bestPrio && cap > bestCap)))
            {
                best = k;
                bestPrio = prio;
                bestCap = cap;
            }
        }
        return best;
    }

    public bool CanAccept(int type) => BestSink(type) != null;

    // Returned quantity decides (TypedResourceGrid.ReceiveResources does not check type; gates above do).
    public bool Put(int type) => BestSink(type)?.ReceiveResources(Ids[type], 1, isCrewDelivery: false, sticky: false, playSound: false) == 1;
}
