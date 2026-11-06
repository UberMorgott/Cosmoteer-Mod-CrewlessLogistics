using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Halfling;
using Halfling.ObjectBits;
using Halfling.ObjectText;
using Halfling.Serialization.Generic;
using HarmonyLib;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Installs the four belt patches and the network-coverage hook all-or-nothing (SharedShip pattern, docs/BELTS_DESIGN.md §10 version guard).
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
        // Private Assets.ApplyPreLoadMods(OTFile) (Assets.cs:426): after every mod action, before Read<Rules>.
        AccessTools.Method(typeof(Assets), "ApplyPreLoadMods", new[] { typeof(OTFile) })
            ?? throw new MissingMethodException("Assets.ApplyPreLoadMods(OTFile)"),
        // Filter toggle modes are ints in ship metadata (ShipMetadata.cs:311): saves and design files carry the type
        // table they were written under; loads remap modded modes before any part reads them (§7).
        AccessTools.Method(typeof(ShipMetadata), nameof(ShipMetadata.ReadFrom), new[] { typeof(GenericSerialReader) })
            ?? throw new MissingMethodException("ShipMetadata.ReadFrom(GenericSerialReader)"),
        AccessTools.Method(typeof(ShipMetadata), nameof(ShipMetadata.WriteTo), new[] { typeof(GenericSerialWriter) })
            ?? throw new MissingMethodException("ShipMetadata.WriteTo(GenericSerialWriter)"),
        AccessTools.Method(typeof(Ship), nameof(Ship.SaveDesign), new[] { typeof(OBNode) })
            ?? throw new MissingMethodException("Ship.SaveDesign(OBNode)"),
        // Every design load (bytes, stream, PNG, file, base64) ends here; toggle states are read before the parts (Ship.cs:3198, 3484).
        AccessTools.Method(typeof(Ship), nameof(Ship.Load), new[] { typeof(OBNode), typeof(string), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(ulong?), typeof(ObjectID) })
            ?? throw new MissingMethodException("Ship.Load(OBNode, ...)"),
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
            harmony.Patch(t[4], postfix: new HarmonyMethod(AccessTools.Method(typeof(NetworkCoverage), nameof(NetworkCoverage.Postfix))));
            harmony.Patch(t[5], postfix: Hook(nameof(Hooks.MetadataReadPostfix)));
            harmony.Patch(t[6], postfix: Hook(nameof(Hooks.MetadataWritePostfix)));
            harmony.Patch(t[7], postfix: Hook(nameof(Hooks.SaveDesignPostfix)));
            harmony.Patch(t[8], prefix: Hook(nameof(Hooks.LoadDesignPrefix)));
            foreach (MethodBase m in t)
            {
                if (Harmony.GetPatchInfo(m) is not { } info || info.Postfixes.Concat(info.Prefixes).Count(p => p.owner == HarmonyId) != 1)
                {
                    throw new InvalidOperationException("patch missing after Patch: " + m.FullDescription());
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
        r.Add((Enabled, $"P1 Apply enabled ({t.Length} postfixes installed and verified)"));
        foreach (MethodBase m in t)
        {
            r.Add((Harmony.GetPatchInfo(m) is { } info && info.Postfixes.Concat(info.Prefixes).Count(p => p.owner == HarmonyId) == 1, $"P1 one patch on {m.DeclaringType?.Name}.{m.Name}"));
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
        iHash.AddHash(BeltTypes.Fingerprint);
        foreach (Part part in __instance.Parts)
        {
            if (BeltState.TryGet(part, out NodeState? s))
            {
                BeltState.Hash(s, iHash);
            }
        }
    }

    /// <summary>The table to persist: none while only vanilla types exist (their modes never move, old saves stay byte-identical).</summary>
    private static string[]? Table => BeltTypes.Ids.Length > BeltTypes.Vanilla.Length ? BeltTypes.Ids : null;

    private static bool NeedsRemap([NotNullWhen(true)] string[]? saved) => saved != null && !saved.SequenceEqual(BeltTypes.Ids);

    /// <summary>Save games / MP joins: metadata is read before the parts (Ship.cs:1206-1211), which take their toggle modes from it.</summary>
    public static void MetadataReadPostfix(ShipMetadata __instance, GenericSerialReader reader)
    {
        try
        {
            string[]? saved = reader.ReadOptionalFromPath<string[]?>(BeltState.TypesPath, null);
            if (!NeedsRemap(saved))
            {
                return;
            }
            var changes = new List<((PartSaveData, ID<PartToggleGuiRules>) Key, int? State, int? Blueprint)>();
            foreach (var (key, (state, blueprint)) in __instance.PartUIToggleStates)
            {
                if (BeltState.IsFilter(key.ComponentID, out bool splitter))
                {
                    changes.Add((key, state is int a ? BeltTypes.Remap(a, splitter, saved) : null, blueprint is int b ? BeltTypes.Remap(b, splitter, saved) : null));
                }
            }
            foreach (var (key, state, blueprint) in changes)
            {
                __instance.PartUIToggleStates.Set(key, state);
                __instance.PartUIToggleStates.Set(key, blueprint, blueprintMode: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("metadata filter remap (modes kept as saved)", ex);
        }
    }

    public static void MetadataWritePostfix(GenericSerialWriter writer)
    {
        if (Table is { } t)
        {
            writer.WriteToPath(BeltState.TypesPath, t);
        }
    }

    public static void SaveDesignPostfix(OBNode node)
    {
        if (Table is { } t)
        {
            App.ObjectBitsSerializer.Write(node.GetOrMakeChild(BeltState.TypesPath), t);
        }
    }

    /// <summary>Design files: rewrites the (in-memory) toggle states to the current table, and the table with them, so a reload is a no-op.</summary>
    public static void LoadDesignPrefix(OBNode node)
    {
        try
        {
            if (!node.ContainsChild(BeltState.TypesPath) || !node.ContainsChild("PartUIToggleStates"))
            {
                return;
            }
            string[] saved = App.ObjectBitsSerializer.Read<string[]>(node[BeltState.TypesPath]);
            if (!NeedsRemap(saved))
            {
                return;
            }
            var states = App.ObjectBitsSerializer.Read<Dictionary<(PartSaveData, ID<PartToggleGuiRules>), int>>(node["PartUIToggleStates"]);
            foreach (var key in states.Keys.ToList())
            {
                if (BeltState.IsFilter(key.Item2, out bool splitter))
                {
                    states[key] = BeltTypes.Remap(states[key], splitter, saved);
                }
            }
            node.RemoveChild("PartUIToggleStates");
            App.ObjectBitsSerializer.Write(node.GetOrMakeChild("PartUIToggleStates"), states);
            node.RemoveChild(BeltState.TypesPath);
            App.ObjectBitsSerializer.Write(node.GetOrMakeChild(BeltState.TypesPath), BeltTypes.Ids);
        }
        catch (Exception ex)
        {
            Log.Error("design filter remap (modes kept as saved)", ex);
        }
    }
}
