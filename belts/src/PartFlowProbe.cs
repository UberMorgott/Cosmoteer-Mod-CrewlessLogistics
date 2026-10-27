using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.UI;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Headless §20 check (tests/Run-BeltProbes.ps1, called by reflection): deserializes the real vanilla component rules of
/// a part from the game's Data with the game's own serializer and runs <see cref="PartFlow.Classify"/> on them.
/// </summary>
internal static class PartFlowProbe
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static List<(bool Ok, string What)> SelfTest(string gameRoot)
    {
        var r = new List<(bool, string)>();
        var loader = new Loader(gameRoot);
        string Set(uint m) => m == PartFlow.All ? "ALL" : m == 0 ? "-" : string.Join(",", Enumerable.Range(0, BeltTypes.Ids.Length).Where(i => (m >> i & 1) != 0).Select(i => BeltTypes.Ids[i]));
        uint M(params string[] ids) => ids.Aggregate(0u, (m, s) => m | 1u << Array.IndexOf(BeltTypes.Ids, s));
        void Expect(string part, uint inSet, uint outSet, int? missileType = null)
        {
            PartRules rules;
            try
            {
                rules = loader.Part(part);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException { InnerException: { } inner })
                {
                    ex = inner;
                }
                r.Add((false, $"F1 {part}: rules load threw {ex}"));
                return;
            }
            var (i, o) = PartFlow.Classify(rules, 0, false, id => id.ToString() == "missile_type" ? missileType : null);
            string mode = missileType is { } m ? $" missile_type={m}" : "";
            r.Add((i == inSet && o == outSet, $"F1 {part}{mode}: in {Set(i)} out {Set(o)} (expect in {Set(inSet)} out {Set(outSet)}; {loader.Skipped(part)})"));
        }
        Expect("cannon_med", M("bullet"), 0);
        Expect("cannon_large", M("bullet"), 0);
        Expect("chaingun", M("bullet"), 0);
        Expect("railgun_loader", M("bullet"), 0);
        Expect("flak_cannon_large", M("bullet"), 0);
        // missile_type modes (missile_launcher.rules:126-199): 0 HE, 1 EMP, 2 nuke, 3 mines, 4 thermal.
        string[] launcher = { "missile_part_he", "missile_part_emp", "missile_part_nuke", "mine_part", "missile_part_thermal" };
        for (int k = 0; k < launcher.Length; k++)
        {
            Expect("missile_launcher", M(launcher[k]), 0, k);
        }
        Expect("missile_launcher", M(launcher[0]), 0); // no stored state -> Default 0
        Expect("factory_ammo", M("sulfur"), M("bullet"));
        Expect("factory_emp", M("iron", "copper"), M("missile_part_emp"));
        Expect("factory_coil2", M("coil", "copper"), M("coil2"));
        Expect("factory_steel", M("iron"), M("steel"));
        Expect("reactor_small", 0, 0); // battery only: no belt types -> does not narrow
        Expect("storage_2x2", PartFlow.All, PartFlow.All);
        Expect("storage_4x4", PartFlow.All, PartFlow.All);

        uint bullet = M("bullet"), sulfur = M("sulfur"), coilCopper = M("coil", "copper");
        // Connector sets (D2): cargo -> cannon, factory_ammo -> cannon, belt -> factory_coil2, factory_he -> cannon, armor.
        r.Add((PartFlow.Allowed(PartFlow.All, bullet) == bullet && PartFlow.Allowed(bullet, bullet) == bullet
            && PartFlow.Allowed(PartFlow.All, coilCopper) == coilCopper && PartFlow.Allowed(M("missile_part_he"), bullet) == 0
            && PartFlow.Allowed(0, 0) == PartFlow.All && PartFlow.Allowed(M("steel"), 0) == M("steel"),
            "F2 allowed = Out(back) & In(front); no-flow side does not narrow; disjoint buildings -> empty"));
        // §22: a ruled-out choice -> Auto; Auto and explicit None (23) never rewritten; generic untouched.
        const int auto = BeltTypes.ModeAuto, none = BeltTypes.ModeNone;
        r.Add((PartFlow.Normalize(bullet, auto) == auto && PartFlow.Normalize(bullet, none) == none && PartFlow.Normalize(bullet, 1) == auto
            && PartFlow.Normalize(bullet, 2 + 5) == auto && PartFlow.Normalize(bullet, 17) == 17
            && PartFlow.Normalize(coilCopper, 2 + 5) == auto && PartFlow.Normalize(coilCopper, 2 + 9) == 11 && PartFlow.Normalize(coilCopper, 1) == 1
            && PartFlow.Normalize(coilCopper, none) == none && PartFlow.Normalize(PartFlow.All, 7) == 7 && PartFlow.Normalize(0, 2) == auto
            && PartFlow.Normalize(0, 1) == auto && PartFlow.Normalize(0, none) == none && PartFlow.Normalize(coilCopper, 99) == auto,
            "F3 normalize: ruled-out choice -> auto; auto, explicit none, any/types inside kept; ALL unchanged"));
        // Effective filter: Auto = the single allowed type, else nothing; explicit None always nothing (owner: respect None).
        r.Add((PartFlow.Filter(bullet, auto) == 15 && PartFlow.Filter(bullet, none) == BeltTypes.FilterNone && PartFlow.Filter(bullet, 17) == 15
            && PartFlow.Filter(coilCopper, auto) == BeltTypes.FilterNone && PartFlow.Filter(PartFlow.All, auto) == BeltTypes.FilterNone
            && PartFlow.Filter(0, auto) == BeltTypes.FilterNone && PartFlow.Filter(coilCopper, 1) == BeltTypes.FilterAny,
            "F3b filter: auto + single (gun) -> bullet; explicit none with single -> nothing; auto with a set / ALL / disjoint -> nothing"));
        // UI choices (D5): modes offered per set (0..23).
        int[] Offered(uint a) => Enumerable.Range(0, none + 1).Where(m => PartFlow.ChoiceAllowed(a, m)).ToArray();
        r.Add((Offered(bullet).SequenceEqual(new[] { auto, 17, none }) && Offered(coilCopper).SequenceEqual(new[] { auto, 1, 4, 11, none })
            && Offered(PartFlow.All).Length == 24 && Offered(0).SequenceEqual(new[] { auto, none }) && Offered(sulfur).SequenceEqual(new[] { auto, 2, none }),
            "F4 UI choices: gun {auto, bullet, none}; factory_coil2 input {auto, any, copper, coil, none}; generic all 24; disjoint {auto, none}"));
        return r;
    }

    /// <summary>Game ObjectText serializer over Data/cosmoteer.rules (same calls as prototypes/whole-cell-native).</summary>
    private sealed class Loader
    {
        private readonly object _root;
        private readonly object _serializer;
        private readonly Type _state;
        private readonly Assembly _core = typeof(Halfling.Geometry.IntVector2).Assembly;
        private readonly Dictionary<string, List<string>> _skipped = new();

        public Loader(string gameRoot)
        {
            _serializer = Activator.CreateInstance(_core.GetType("Halfling.Serialization.ObjectText.ObjectTextSerializer", true)!, new object[] { true })!;
            _root = Activator.CreateInstance(_core.GetType("Halfling.ObjectText.OTFile", true)!, System.IO.Path.Combine(gameRoot, "Data", "cosmoteer.rules"))!;
            _state = _core.GetType("Halfling.Serialization.SerialState", true)!;
        }

        public string Skipped(string part) => _skipped.TryGetValue(part, out List<string>? s) && s.Count > 0 ? "headless shells (flow/toggle fields from Data, engine CanBe* code): " + string.Join(",", s) : "all components loaded";

        private object Read(string path, Type type, string? componentId)
        {
            using var state = (IDisposable)_state.GetMethod("BeginOperation", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object?[] { null })!;
            MethodInfo set = _state.GetMethods(All).Single(m => m.Name == "SetCustomObject" && !m.IsGenericMethod && m.GetParameters().Length == 2);
            set.Invoke(state, new object?[] { typeof(PartRules), null });
            if (componentId != null)
            {
                set.Invoke(state, new object[] { typeof(ID<PartComponentRules>), new ID<PartComponentRules>(componentId) });
            }
            MethodInfo read = _serializer.GetType().GetMethods(All).Single(m => m.Name == "ReadFromPath" && !m.IsGenericMethod && m.GetParameters().Length == 6 && m.GetParameters()[2].ParameterType == typeof(Type));
            return read.Invoke(_serializer, new object?[] { _root, path, type, Activator.CreateInstance(read.GetParameters()[3].ParameterType), null, null })!;
        }

        /// <summary>
        /// PartRules shell holding the real Components/ComponentsByID/UIToggleComponentsByID (all Classify reads). A
        /// component whose media (sounds) cannot be built headless becomes an uninitialised instance of its real rules
        /// class with only the flow/toggle fields read from Data; its CanBe*/IsBlueprintToggleOn stay the engine's code.
        /// </summary>
        public PartRules Part(string name)
        {
            var rules = (PartRules)RuntimeHelpers.GetUninitializedObject(typeof(PartRules));
            var byId = new Dictionary<ID<PartComponentRules>, PartComponentRules>();
            var toggles = new Dictionary<ID<PartToggleGuiRules>, PartUIToggleRules>();
            var shells = _skipped[name] = new List<string>();
            List<PartComponentRules> list = Group($"<ships/terran/{name}/{name}.rules>/Part/Components", shells);
            foreach (PartComponentRules c in list)
            {
                byId[c.ID] = c;
                if (c is PartUIToggleRules t)
                {
                    toggles[t.ToggleID] = t;
                }
            }
            typeof(PartRules).GetField(nameof(PartRules.Components))!.SetValue(rules, list);
            typeof(PartRules).GetField(nameof(PartRules.PhysicalComponents))!.SetValue(rules, list.Where(c => c.HasPhysicalComponent).ToList());
            typeof(PartRules).GetField(nameof(PartRules.ComponentsByID))!.SetValue(rules, byId);
            typeof(PartRules).GetField(nameof(PartRules.UIToggleComponentsByID))!.SetValue(rules, toggles);
            return rules;
        }

        private static readonly string[] FlowFields = { "ResourceType", "SuppliesResources", "Toggle", "Invert", "Mode", "ToggleID", "Default", "ToggledOnChoices", "OnlyWithRotation", "OnlyWithFlip", "IncludeWhenNotUnderConstruction" };
        private Dictionary<string, Type>? _types;

        private List<PartComponentRules> Group(string path, List<string> shells)
        {
            Type nodeType = _core.GetType("Halfling.ObjectText.OTNode", true)!;
            var list = new List<PartComponentRules>();
            foreach (string id in ((IDictionary)Read(path, typeof(Dictionary<,>).MakeGenericType(typeof(string), nodeType), null)).Keys.Cast<string>())
            {
                try
                {
                    list.Add((PartComponentRules)Read(path + "/" + id, typeof(PartComponentRules), id));
                }
                catch (Exception)
                {
                    if (Shell(path + "/" + id, id, shells) is { } c)
                    {
                        list.Add(c);
                    }
                }
            }
            return list;
        }

        private T? Opt<T>(string path)
        {
            try
            {
                return (T)Read(path, typeof(T), null);
            }
            catch (Exception)
            {
                return default;
            }
        }

        private PartComponentRules? Shell(string path, string id, List<string> shells)
        {
            _types ??= typeof(PartRules).Assembly.GetTypes()
                .Where(t => typeof(PartComponentRules).IsAssignableFrom(t) && !t.IsAbstract)
                .SelectMany(t => t.GetCustomAttributes<Halfling.Serialization.SerialDerivedTypeAttribute>(false).Select(a => (a.TypeName, t)))
                .Where(p => p.TypeName != null).GroupBy(p => p.TypeName!).ToDictionary(g => g.Key, g => g.First().t);
            string type = Opt<string>(path + "/Type") ?? "?";
            if (!_types.TryGetValue(type, out Type? cls))
            {
                shells.Add("skip:" + type + ":" + id);
                return null;
            }
            var c = (PartComponentRules)RuntimeHelpers.GetUninitializedObject(cls);
            c.ID = new ID<PartComponentRules>(id);
            c.IncludeWhenNotUnderConstruction = true; // field initializer skipped by GetUninitializedObject
            for (Type? k = cls; k != null && k != typeof(object); k = k.BaseType)
            {
                foreach (FieldInfo f in k.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!FlowFields.Contains(f.Name))
                    {
                        continue;
                    }
                    Type ft = Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType;
                    object? v = ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(ID<>)
                        ? Opt<string>(path + "/" + f.Name) is { } s ? Activator.CreateInstance(ft, s) : null
                        : typeof(PartFlowProbe.Loader).GetMethod(nameof(Opt), BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(ft.IsValueType ? typeof(Nullable<>).MakeGenericType(ft) : ft).Invoke(this, new object[] { path + "/" + f.Name });
                    if (v != null)
                    {
                        f.SetValue(c, v);
                    }
                }
            }
            if (c is PartModeToggleRules mt)
            {
                var on = new HashSet<int>(Opt<int[]>(path + "/OnModes") ?? Array.Empty<int>());
                if (Opt<int?>(path + "/OnMode") is { } one)
                {
                    on.Add(one);
                }
                typeof(PartModeToggleRules).GetField(nameof(PartModeToggleRules.OnModes))!.SetValue(mt, on);
            }
            if (c is PartToggledComponentsRules tc)
            {
                typeof(PartToggledComponentsRules).GetField(nameof(PartToggledComponentsRules.Components))!.SetValue(tc, Group(path + "/Components", shells));
            }
            shells.Add(type);
            return c;
        }
    }
}