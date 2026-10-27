using System.Collections.Generic;
using System.Diagnostics;
using Cosmoteer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Halfling.Scene2D;
using Halfling.Timing;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Per-ship deterministic belt sim (§1): FixedUpdate in bucket -11 Commands, which SimRoot runs sequentially
/// (no SetCustomFixedUpdateBucketHandler for -11, SimRoot.cs:605-618). Not serialized; rebuilt from parts.
/// </summary>
internal sealed class BeltManager : ShipComponent
{
    public const int Bucket = FixedUpdateBuckets.Commands;

    /// <summary>shortcut: probe tracks vanilla corridors as stand-in belt parts until belt rules parts exist (step 2).</summary>
    public const string ProbePartId = "cosmoteer.corridor";

    private readonly List<BeltState> _tracked = new();
    private bool _dirty = true;
    private int _lastTick = int.MinValue;
    private int _dumpAtTick = -1;

    public BeltManager()
    {
        FixedUpdateBucket = Bucket;
    }

    public int ConfiguredBucket => FixedUpdateBucket;

    // public: lib\ is publicized; runtime original is protected and CLR allows widening on override.
    public override void OnShipComponentAttaching(Ship ship)
    {
        ship.Parts.PartAdded += OnPartsChanged;
        ship.Parts.PartRemoved += OnPartsChanged;
    }

    public override void OnShipComponentDetached(Ship ship)
    {
        ship.Parts.PartAdded -= OnPartsChanged;
        ship.Parts.PartRemoved -= OnPartsChanged;
    }

    // Dirty flag only; topology is rebuilt inside FixedUpdate (§3).
    private void OnPartsChanged(Part _) => _dirty = true;

    public override void FixedUpdate(FixedUpdater fixedUpdater, SceneRoot root)
    {
        Ship? ship = Ship;
        var sim = Sim;
        if (ship == null || sim == null)
        {
            return;
        }
        long t0 = Stopwatch.GetTimestamp();
        int tick = sim.Tick;
        bool duplicate = tick == _lastTick;
        _lastTick = tick;
        if (_dirty)
        {
            _dirty = false;
            _tracked.Clear();
            foreach (Part part in ship.Parts) // ordered list, never hash-set order
            {
                if (part.Rules.ID.ToString() == ProbePartId)
                {
                    _tracked.Add(BeltState.GetOrCreate(part));
                }
            }
            _dumpAtTick = tick + 90;
        }
        foreach (BeltState s in _tracked)
        {
            s.Counter++;
        }
        if (tick == _dumpAtTick)
        {
            Probes.DumpEndpoints(ship);
        }
        Probes.PushP5(ship, tick);
        Probes.OnTick(sim, tick, duplicate, _tracked.Count, Stopwatch.GetTimestamp() - t0);
    }
}
