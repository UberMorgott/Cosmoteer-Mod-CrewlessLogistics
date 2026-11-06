using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Halfling.ObjectText;
using Halfling.Serialization;
using Halfling.Serialization.ObjectText;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Automatic electrical network coverage (docs/PORT_COVERAGE.md). Postfix of the private
/// <c>Cosmoteer.Data.Assets.ApplyPreLoadMods(OTFile)</c>: runs once after every enabled mod's native actions and before
/// <c>Read&lt;Rules&gt;</c>, so modded parts are covered like vanilla ones. Each registered part is read through native
/// OT resolution (inheritance context kept) and classified structurally; adapters are added only to the part's own
/// Components overlay, the same edit ModAddBaseAction makes. Two-phase: every plan is built and its text parsed on the
/// unmodified root before the first edit, in registry order, so all lockstep clients produce identical rules.
/// </summary>
internal static partial class NetworkCoverage
{
    public const string Registry = "<ships/terran/terran.rules>/Terran/Parts";
    public const string CableId = "morgott.crewless_logistics.cable";
    private const string Prefix = "CrewlessLogistics";

    private static readonly ObjectTextSerializer S = new(true);

    public sealed class Report
    {
        public readonly SortedDictionary<DeviceKind, int> Covered = new();
        public readonly List<string> Skipped = new();
        public int Ports;
        public int Derived;
        public string? NotApplied;

        public string Summary() => NotApplied != null
            ? "automatic network coverage not applied: " + NotApplied
            : $"automatic network coverage: {Covered.Values.Sum()} parts ({string.Join(", ", Covered.Select(k => k.Key + " " + k.Value))}), {Ports} sockets, {Derived} inheriting overlays, {Skipped.Count} skipped";
    }

    /// <summary>Harmony postfix (belt resource table, then coverage); the parameter name matches the original's.</summary>
    public static void Postfix(OTFile rulesFile)
    {
        try
        {
            Log.Info(ResourceTable.Apply(rulesFile));
        }
        catch (Exception ex)
        {
            BeltTypes.Set(BeltTypes.Vanilla);
            Log.Error("belt resource types (vanilla table used)", ex);
        }
        try
        {
            Report r = Apply(rulesFile);
            Log.Info(r.Summary());
            foreach (string s in r.Skipped)
            {
                Log.Info("  skipped " + s);
            }
        }
        catch (Exception ex)
        {
            Log.Error("coverage", ex);
            Log.Info("automatic network coverage DISABLED for this session: devices get no cable sockets");
        }
    }

    private sealed class Entry
    {
        public int Index;
        public string Id = "";
        public OTGroupNodeEx Part = null!;
        public OTContext? Ctx;
        public readonly HashSet<OTGroupNode> PartClosure = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<OTGroupNode> ComponentsClosure = new(ReferenceEqualityComparer.Instance);
        public DeviceDecision? Decision;
        public string? Template;
        public List<(string Name, string Text)> Children = new();
        /// <summary>Existing local Components group, or null: create <c>Components : ^/K/Components {}</c>.</summary>
        public OTGroupNodeEx? Overlay;
        public int K = -1;
        public readonly List<Entry> Ancestors = new();

        /// <summary>Already holds CrewlessLogistics* components (own parts, another source, or a second run): left as is.</summary>
        public bool Networked;

        public bool Planned => Template != null;
    }

    public static Report Apply(OTFile root)
    {
        var report = new Report();
        var entries = Read(root, out OTGroupNode? cable, out string[] cableProhibitedBy);
        if (cable == null)
        {
            report.NotApplied = "Crewless Logistics part rules are absent from the registry (mods not applied)";
            return report;
        }
        // Templates sit next to the cable rules; PathWithFile is the absolute form ModAddBaseAction adds.
        IOTNode Tmpl(string path) => cable.FindAtPath(path);
        string PathOf(string rel) => Tmpl(rel).PathWithFile;

        // Phase 1: plans on the unmodified root.
        foreach (Entry e in entries)
        {
            if (e.Decision is not { } d || d.Kind == DeviceKind.None)
            {
                if (e.Decision?.Skip != null)
                {
                    report.Skipped.Add($"{e.Id}: {e.Decision.Skip}");
                }
                continue;
            }
            if (d.Skip == null)
            {
                try
                {
                    List<PortEdge> ports = Ports(e, cableProhibitedBy);
                    if (ports.Count == 0)
                    {
                        d = e.Decision = d with { Skip = "No cable-reachable perimeter edge." };
                    }
                    else
                    {
                        (e.Template, e.Children) = DeviceClassifier.Adapter(d, ports, PathOf);
                        Overlay(e);
                    }
                }
                catch (Exception ex)
                {
                    e.Template = null;
                    d = e.Decision = d with { Skip = ex.Message };
                }
            }
            if (d.Skip != null)
            {
                report.Skipped.Add($"{e.Id} ({d.Kind}): {d.Skip}");
            }
        }
        // A part inheriting a covered part's overlay would inherit its adapter too: give it its own overlay holding
        // its full adapter (or nothing) locally, voiding every inherited adapter child it must not have. A part that
        // cannot get its own overlay drops the adapters it would inherit (fixpoint, registry order).
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (Entry q in entries)
            {
                q.Ancestors.Clear();
                q.Ancestors.AddRange(entries.Where(a => a != q && a.Planned
                    && (a.Overlay != null ? q.ComponentsClosure.Contains(a.Overlay) : q.PartClosure.Contains(a.Part))));
                if (q.Ancestors.Count == 0 || q.Planned)
                {
                    continue;
                }
                try
                {
                    if (q.Networked)
                    {
                        throw new InvalidOperationException("it has its own Crewless Logistics components, which are kept");
                    }
                    Overlay(q);
                }
                catch (Exception ex)
                {
                    foreach (Entry a in q.Ancestors)
                    {
                        a.Template = null;
                        report.Skipped.Add($"{a.Id}: its adapter would leak into {q.Id} ({ex.Message})");
                    }
                    changed = true;
                }
            }
        }
        foreach (Entry e in entries.Where(e => e.Ancestors.Count > 0))
        {
            e.Children = Derived(e, Tmpl);
        }
        var apply = entries.Where(e => e.Planned || e.Ancestors.Count > 0).ToList();
        foreach (Entry e in apply)
        {
            // Validate against the real overlay: every added name must be new there, and its text must parse.
            var scratch = new OTFile(new StringReader(""));
            foreach (var (name, text) in e.Children)
            {
                if (e.Overlay?.LocalChildren.Contains(name) == true)
                {
                    throw new InvalidOperationException($"{e.Id}: Components already declares {name} locally");
                }
                scratch.LocalChildren.Add(text);
            }
            if (e.Ancestors.Count == 0 && e.Overlay?.InheritanceList.LocalChildren.ContainsReference(e.Template!) == true)
            {
                throw new InvalidOperationException($"{e.Id}: Components already inherits {e.Template}");
            }
        }
        Commit(apply);
        foreach (Entry e in apply)
        {
            if (e.Planned)
            {
                report.Covered[e.Decision!.Kind] = report.Covered.GetValueOrDefault(e.Decision.Kind) + 1;
                report.Ports += e.Children.Count(c => c.Name.StartsWith("CrewlessLogisticsPort_", StringComparison.Ordinal));
            }
            report.Derived += e.Ancestors.Count > 0 ? 1 : 0;
        }
        return report;
    }

    /// <summary>Test seam: throw before edit number N to prove the rollback (negative = never).</summary>
    internal static int FailAfterEdits = -1;

    /// <summary>Phase 2, in registry order. Any failure undoes every edit made so far: the rules stay as the mods left them.</summary>
    private static void Commit(List<Entry> apply)
    {
        var undo = new List<(OTChildCollection Owner, IOTNode Node)>();
        void Add(OTChildCollection owner, string text)
        {
            if (undo.Count == FailAfterEdits)
            {
                throw new InvalidOperationException("test failure after " + undo.Count + " edits");
            }
            undo.AddRange(owner.Add(text).Select(n => (owner, (IOTNode)n)));
        }
        try
        {
            foreach (Entry e in apply)
            {
                OTGroupNodeEx? overlay = e.Overlay;
                if (overlay == null)
                {
                    Add(e.Part.LocalChildren, $"Components : ^/{e.K.ToString(CultureInfo.InvariantCulture)}/Components {{ }}");
                    overlay = (OTGroupNodeEx)undo[^1].Node;
                }
                if (e.Ancestors.Count == 0)
                {
                    Add(overlay.InheritanceList.LocalChildren, e.Template!); // ModAddBaseAction.ApplyAction
                }
                foreach (var (_, text) in e.Children)
                {
                    Add(overlay.LocalChildren, text);
                }
            }
        }
        catch (Exception ex)
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                undo[i].Owner.Remove(undo[i].Node);
            }
            throw new InvalidOperationException($"applying coverage failed, {undo.Count} edits rolled back: {ex.Message}", ex);
        }
    }

    /// <summary>The part's own local Components group, else the inheritance index that supplies Components.</summary>
    private static void Overlay(Entry e)
    {
        if (e.Part.LocalChildren.TryGetNode("Components", out IOTGroupedNode? local))
        {
            e.Overlay = local as OTGroupNodeEx ?? throw new InvalidOperationException("Components is not a group that can take an inheritance overlay.");
            return;
        }
        int i = 0;
        foreach (OTInheritanceReferenceNode r in e.Part.InheritanceList)
        {
            if (r.TryFindFinalTarget(out IOTNode? t, e.Ctx, out OTContext? c) && t is OTGroupNode g && g.TryGetNode("Components", out IOTGroupedNode? _, c))
            {
                e.K = i;
                return;
            }
            i++;
        }
        throw new InvalidOperationException("No local or inherited Components group.");
    }

    /// <summary>
    /// Children of an overlay that inherits other adapters: the full desired adapter declared locally (template children
    /// by explicit path) plus a void node (native "Name;", skipped by PartRules) for every other inherited adapter child.
    /// </summary>
    private static List<(string, string)> Derived(Entry e, Func<string, IOTNode> tmpl)
    {
        var desired = new List<(string Name, string Text)>();
        if (e.Planned)
        {
            var own = e.Children.ToDictionary(c => c.Name, c => c.Text, StringComparer.Ordinal);
            foreach (string name in TemplateNames(e.Template!, tmpl))
            {
                desired.Add((name, own.Remove(name, out string? text) ? text : $"{name} : {e.Template}/{name} {{ }}"));
            }
            desired.AddRange(e.Children.Where(c => own.ContainsKey(c.Name)));
        }
        var keep = new HashSet<string>(desired.Select(d => d.Name), StringComparer.Ordinal);
        var inherited = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Entry a in e.Ancestors)
        {
            inherited.UnionWith(TemplateNames(a.Template!, tmpl));
            inherited.UnionWith(a.Children.Select(c => c.Name));
        }
        desired.AddRange(inherited.Where(n => !keep.Contains(n)).Select(n => (n, n + ";")));
        return desired;
    }

    private static IEnumerable<string> TemplateNames(string template, Func<string, IOTNode> tmpl) =>
        ((OTGroupNode)tmpl(template)).GetChildren().Where(c => c is not OTVoidNode).Select(c => c.Name).Distinct(StringComparer.Ordinal);

    private static List<PortEdge> Ports(Entry e, string[] cableProhibitedBy)
    {
        int[] size = Req<int[]>(e.Part, e.Ctx, "Size");
        if (size.Length != 2)
        {
            throw new ArgumentException("Part.Size must contain two cell dimensions.");
        }
        CellRect? physical = null;
        if (Try(e.Part, e.Ctx, "PhysicalRect", out int[]? pr) && pr != null)
        {
            physical = pr.Length == 4 ? new CellRect(pr[0], pr[1], pr[2], pr[3]) : throw new ArgumentException("Part.PhysicalRect must be [x, y, width, height].");
        }
        var rects = new List<(string, CellRect)>();
        if (e.Part.TryFindAtPath("ProhibitRects", out IOTNode? found, e.Ctx, out OTContext? rc) && found is OTParentNode list)
        {
            for (int n = 0; n < list.GetCount(rc); n++)
            {
                int[] r = Req<int[]>(e.Part, e.Ctx, $"ProhibitRects/{n}/1");
                rects.Add((Req<string>(e.Part, e.Ctx, $"ProhibitRects/{n}/0"), r.Length == 4 ? new CellRect(r[0], r[1], r[2], r[3]) : throw new ArgumentException("Each ProhibitRects entry must contain [x, y, width, height].")));
            }
        }
        Try(e.Part, e.Ctx, "Prohibits", out string[]? prohibits);
        int Side(string field) => Try(e.Part, e.Ctx, field, out int v) ? v : 0;
        rects.AddRange(PortGeometry.SideRects(size[0], size[1], Side("ProhibitLeft"), Side("ProhibitRight"), Side("ProhibitAbove"), Side("ProhibitBelow"), prohibits));
        return PortGeometry.Perimeter(size[0], size[1], physical, rects, cableProhibitedBy);
    }

    /// <summary>Registered parts in registry order with their effective component facts and classification.</summary>
    private static List<Entry> Read(OTFile root, out OTGroupNode? cable, out string[] cableProhibitedBy)
    {
        cable = null;
        cableProhibitedBy = new[] { "default" };
        var entries = new List<Entry>();
        if (!root.TryFindAtPath(Registry, out IOTNode? registry) || registry is not OTParentNode parts)
        {
            return entries;
        }
        var seen = new HashSet<OTGroupNode>(ReferenceEqualityComparer.Instance);
        int count = parts.GetCount();
        for (int i = 0; i < count; i++)
        {
            if (root.FindAtPath(Registry + "/" + i.ToString(CultureInfo.InvariantCulture), null, out OTContext? ctx) is not OTGroupNodeEx part || !seen.Add(part))
            {
                continue;
            }
            var e = new Entry { Index = i, Part = part, Ctx = ctx, Id = "#" + i.ToString(CultureInfo.InvariantCulture) };
            entries.Add(e);
            try
            {
                e.Id = Req<string>(part, ctx, "ID");
                if (e.Id == CableId)
                {
                    cable = part;
                    if (Try(part, ctx, "ProhibitedBy", out string[]? by) && by != null)
                    {
                        cableProhibitedBy = by;
                    }
                }
                Closure(part, ctx, e.PartClosure);
                if (part.FindAtPath("Components", ctx, out OTContext? cc) is OTGroupNode components)
                {
                    Closure(components, cc, e.ComponentsClosure);
                    e.Networked = components.GetChildren(cc).Any(c => c.Name.StartsWith(Prefix, StringComparison.Ordinal));
                    e.Decision = e.Networked ? new DeviceDecision(DeviceKind.None) : Classify(part, ctx, components, cc);
                }
            }
            catch (Exception ex)
            {
                e.Decision = new DeviceDecision(DeviceKind.None, "Unreadable part rules: " + ex.Message);
            }
        }
        return entries;
    }

    private static DeviceDecision Classify(OTGroupNodeEx part, OTContext? ctx, OTGroupNode components, OTContext? cc)
    {
        var comps = new Dictionary<string, DeviceComponent>(StringComparer.Ordinal);
        string? scanError = null;
        try
        {
            Scan(components, cc, comps);
        }
        catch (Exception ex)
        {
            scanError = ex.Message; // unknown schema: classified as far as it goes, then skipped with this reason
        }
        Try(part, ctx, "TypeCategories", out string[]? categories);
        return DeviceClassifier.Classify(categories ?? Array.Empty<string>(), comps, scanError);
    }

    private static void Closure(OTGroupNode g, OTContext? ctx, HashSet<OTGroupNode> set)
    {
        if (!set.Add(g) || g is not OTGroupNodeEx ex)
        {
            return;
        }
        foreach (OTInheritanceReferenceNode r in ex.InheritanceList)
        {
            if (r.TryFindFinalTarget(out IOTNode? t, ctx, out OTContext? c) && t is OTGroupNode tg)
            {
                Closure(tg, c, set);
            }
        }
    }

    private static readonly string[] RefFields = { "Storage", "ResourceStorage", "FromStorage", "ToStorage", "ComponentID", "ProxyToggle", "OperationalToggle" };

    /// <summary>Flat component facts, first declaration wins, nested ToggledComponents included (as the catalog did).</summary>
    private static void Scan(OTGroupNode group, OTContext? ctx, Dictionary<string, DeviceComponent> comps, bool conditional = false)
    {
        foreach (var (node, c) in group.GetChildrenWithContext(ctx))
        {
            if (node is OTVoidNode || !Try(node, c, "Type", out string? type) || type == null)
            {
                continue;
            }
            Try(node, c, "ResourceType", out string? resource);
            if (comps.TryGetValue(node.Name, out DeviceComponent? first))
            {
                first.Conditional &= conditional; // an unconditional declaration exists too
                if (resource == "battery")
                {
                    throw new InvalidOperationException("Repeated battery component ID in conditional branches requires explicit mapping: " + node.Name);
                }
            }
            else
            {
                var x = new DeviceComponent { Id = node.Name, Type = type, ResourceType = resource, Conditional = conditional };
                x.HasViaBuffs = node.TryFindAtPath("ViaBuffs", out IOTNode? _, c, out OTContext? _);
                foreach (string f in RefFields)
                {
                    if (Try(node, c, f, out string? v) && v != null)
                    {
                        x.Refs[f] = v;
                    }
                }
                Try(node, c, "ResourceStorages", out x.ResourceStorages);
                Try(node, c, "PartLocation", out x.PartLocation);
                Try(node, c, "BuffType", out x.BuffType);
                Try(node, c, "ViaBuffs/OutgoingBuffProviders", out x.OutgoingBuffProviders);
                if (Try(node, c, "AllowNonOperationalMinPriorityDelivery", out bool allow))
                {
                    x.AllowNonOperationalMinPriorityDelivery = allow;
                }
                try
                {
                    x.MaxResources = Try(node, c, "MaxResources", out int max) ? max : null;
                }
                catch (Exception)
                {
                    x.MaxResources = null; // a native modifiable value, not a plain integer
                }
                try
                {
                    x.QuantityScale = Try(node, c, "QuantityScale", out float scale) ? scale : null;
                }
                catch (Exception)
                {
                    x.QuantityScaleUnreadable = true;
                }
                comps.Add(node.Name, x);
            }
            if (type == "ToggledComponents" && node.FindAtPath("Components", c, out OTContext? nc) is OTGroupNode nested)
            {
                Scan(nested, nc, comps, true); // exists only while the ToggledComponents branch is on
            }
        }
    }

    /// <summary>Native read with the node's inheritance context, as PartRules reads its fields.</summary>
    internal static bool Try<T>(IOTNode node, OTContext? ctx, string path, out T? value)
    {
        using SerialState state = SerialState.BeginOperation();
        state.SetOTContext(ctx);
        return S.TryReadFromPath(node, path, out value);
    }

    private static T Req<T>(IOTNode node, OTContext? ctx, string path) =>
        Try(node, ctx, path, out T? v) && v != null ? v : throw new ArgumentException($"Part.{path} is missing.");
}
