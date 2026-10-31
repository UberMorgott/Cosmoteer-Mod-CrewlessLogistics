using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Halfling.Serialization.Generic;
using HarmonyLib;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Installs the four belt patches all-or-nothing (SharedShip pattern, docs/BELTS_DESIGN.md §10 version guard).
/// Any failure, including a ReadyToRun-inlined copy of a target that a detour would miss, rolls back and leaves
/// the game vanilla (<see cref="Enabled"/> = false).
/// </summary>
internal static class Patcher
{
    public const string HarmonyId = "morgott.crewless_logistics.belts";

    public static bool Enabled { get; private set; }

    /// <summary>Patched originals, exposed for the headless probe.</summary>
    public static MethodBase[] Targets() => new MethodBase[]
    {
        // Private ctor every Ship ctor chains into (new, loaded, split): attach point for BeltManager.
        AccessTools.Constructor(typeof(Ship),
            new[] { typeof(ShipRules), typeof(string), typeof(int), typeof(ulong?), typeof(bool), typeof(ObjectID) })
            ?? throw new MissingMethodException("Ship..ctor(ShipRules,string,int,ulong?,bool,ObjectID)"),
        // Load ctor Part(GenericSerialReader) (Part.cs:827): read payload.
        AccessTools.Constructor(typeof(Part), new[] { typeof(GenericSerialReader) })
            ?? throw new MissingMethodException("Part..ctor(GenericSerialReader)"),
        // Explicit IGenericSerializable.WriteTo (Part.cs:1371): write payload.
        ExplicitWriteTo(),
        // Ship.GetIntegrityHash (Ship.cs:1294): hash payload + protocol version.
        AccessTools.Method(typeof(Ship), nameof(Ship.GetIntegrityHash), new[] { typeof(IntegrityHash) })
            ?? throw new MissingMethodException("Ship.GetIntegrityHash(IntegrityHash)"),
    };

    private static MethodInfo ExplicitWriteTo()
    {
        InterfaceMapping map = typeof(Part).GetInterfaceMap(typeof(IGenericSerializable));
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name == nameof(IGenericSerializable.WriteTo))
            {
                return map.TargetMethods[i];
            }
        }
        throw new MissingMethodException("Part.IGenericSerializable.WriteTo");
    }

    public static void Apply()
    {
        var sw = Stopwatch.StartNew();
        var harmony = new Harmony(HarmonyId);
        try
        {
            MethodBase[] t = Targets();
            RefuseInlinedTargets(t);
            harmony.Patch(t[0], postfix: Hook(nameof(Hooks.ShipCtorPostfix)));
            harmony.Patch(t[1], postfix: Hook(nameof(Hooks.PartLoadPostfix)));
            harmony.Patch(t[2], postfix: Hook(nameof(Hooks.PartWritePostfix)));
            harmony.Patch(t[3], postfix: Hook(nameof(Hooks.ShipHashPostfix)));
            foreach (MethodBase m in t)
            {
                if (Harmony.GetPatchInfo(m)?.Postfixes.Any(p => p.owner == HarmonyId) != true)
                {
                    throw new InvalidOperationException("postfix missing after Patch: " + m.FullDescription());
                }
            }
            Enabled = true;
            FilterUi.Apply(harmony);
            Log.Info($"P1 patched {t.Length} targets in {sw.ElapsedMilliseconds} ms: "
                + string.Join("; ", t.Select(m => m.FullDescription())));
        }
        catch (Exception ex)
        {
            Enabled = false;
            Log.Error("patch", ex);
            harmony.UnpatchAll(HarmonyId);
            Log.Info("rolled back, any patches left: " + Harmony.HasAnyPatches(HarmonyId));
        }
    }

    /// <summary>Headless P1 (belts/tests GameTests, called by reflection): apply, verify, roll back.</summary>
    public static List<(bool Ok, string What)> SelfTest()
    {
        var r = new List<(bool, string)>();
        Assembly game = typeof(Ship).Assembly;
        r.Add((true, $"P1 game {game.Location} {FileVersionInfo.GetVersionInfo(game.Location).FileVersion} runtime {Environment.Version}"));
        Guid mvid = typeof(Harmony).Assembly.ManifestModule.ModuleVersionId;
        r.Add((mvid == new Guid("dc2e7251-4b84-4883-90eb-eb05a041522c"), $"P1 Harmony is the loader build (MVID {mvid})"));
        MethodBase[] t = Targets();
        RefuseInlinedTargets(t);
        r.Add((true, "P1 no R2R-inlined copies of: " + string.Join("; ", t.Select(m => m.FullDescription()))));
        Apply();
        r.Add((Enabled, "P1 Apply enabled (4 postfixes installed and verified)"));
        foreach (MethodBase m in t)
        {
            r.Add((Harmony.GetPatchInfo(m)?.Postfixes.Count(p => p.owner == HarmonyId) == 1, $"P1 one postfix on {m.DeclaringType?.Name}.{m.Name}"));
        }
        foreach (MethodBase m in FilterUi.Targets())
        {
            r.Add((Harmony.GetPatchInfo(m)?.Owners.Contains(HarmonyId) == true, $"F5 filter UI patch on {m.DeclaringType?.Name}.{m.Name}"));
        }
        var manager = new BeltManager();
        r.Add((manager.ConfiguredBucket == Cosmoteer.FixedUpdateBuckets.Commands, "P1 BeltManager type loads on game runtime, FixedUpdateBucket = -11"));
        r.Add((manager.Awake, "P1 new BeltManager starts awake (first tick rebuilds)"));
        manager.Awake = false;
        r.Add((!manager.Awake, "P1 BeltManager sleeps (FixedUpdate + Update off)"));
        manager.Awake = true;
        r.Add((manager.Awake, "P1 BeltManager wakes again"));
        new Harmony(HarmonyId).UnpatchAll(HarmonyId);
        r.Add((!Harmony.HasAnyPatches(HarmonyId), "P1 rollback leaves no patches"));
        return r;
    }

    private static HarmonyMethod Hook(string name) =>
        new(AccessTools.Method(typeof(Hooks), name) ?? throw new MissingMethodException(nameof(Hooks), name));

    /// <summary>
    /// Cosmoteer.dll is ReadyToRun: a precompiled caller holding an inlined copy of a target would bypass the detour.
    /// SharedShip patches such inliners; belts have none today, so any appearing after a game update fails closed.
    /// </summary>
    public static void RefuseInlinedTargets(IEnumerable<MethodBase> targets)
    {
        Dictionary<int, List<int>>? table = R2RInlineInfo.Read(typeof(Ship).Assembly.Location);
        if (table == null)
        {
            return;
        }
        foreach (MethodBase m in targets)
        {
            if (table.TryGetValue(R2RInlineInfo.Rid(m), out List<int>? inliners) && inliners.Count > 0)
            {
                throw new InvalidOperationException($"{m.FullDescription()} is R2R-inlined into {inliners.Count} methods");
            }
        }
    }
}

internal static class Hooks
{
    public static void ShipCtorPostfix(Ship __instance) => __instance.Components.Add(new BeltManager());

    public static void PartLoadPostfix(Part __instance, GenericSerialReader reader)
    {
        if (reader.TryGetReaderForPath(BeltState.PayloadPath, out GenericSerialReader? r))
        {
            BeltState.Read(__instance, r);
        }
    }

    public static void PartWritePostfix(Part __instance, GenericSerialWriter writer)
    {
        if (BeltState.TryGet(__instance, out NodeState? s))
        {
            BeltState.Write(s, writer.GetWriterForPath(BeltState.PayloadPath));
        }
    }

    public static void ShipHashPostfix(Ship __instance, IntegrityHash iHash)
    {
        iHash.AddHash(BeltState.ProtocolVersion);
        foreach (Part part in __instance.Parts)
        {
            if (BeltState.TryGet(part, out NodeState? s))
            {
                BeltState.Hash(s, iHash);
            }
        }
    }
}
