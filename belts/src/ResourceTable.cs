using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Halfling.ObjectText;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Belt type table and filter choices per rules load (same chokepoint as <see cref="NetworkCoverage"/>, before
/// <c>Read&lt;Rules&gt;</c>): every registered resource with MaxStackSize &gt; 0, read through native OT resolution, feeds
/// <see cref="BeltTypes.Set"/>; each non-vanilla type gets a choice (its own Icon and NameKey, explicit Mode) appended to
/// the connector and splitter filter toggles. Two-phase: all choice texts parse before the first edit; a failed edit
/// rolls every edit back (the table stays: belts still carry the types, only their filter choices are missing).
/// </summary>
internal static partial class ResourceTable
{
    public const string Registry = "Resources";
    public const string Toggles = "<gui/game/parts/part_toggles.rules>/PartToggles";
    private const string FallbackIcon = "ButtonSprite { Texture { File = \"./Data/ships/terran/storage_2x2/icon.png\"; MipLevels = 2; SampleMode = Linear }; Size = [64, 64] }";

    private sealed record Resource(string Id, int Index, IOTNode Node, OTContext? Ctx);

    public static string Apply(OTFile root)
    {
        var stackable = new List<Resource>();
        var skipped = new List<string>();
        if (root.TryFindAtPath(Registry, out IOTNode? reg) && reg is OTParentNode list)
        {
            for (int i = 0; i < list.GetCount(); i++)
            {
                string path = Registry + "/" + i.ToString(CultureInfo.InvariantCulture);
                try
                {
                    IOTNode node = root.FindAtPath(path, null, out OTContext? ctx);
                    if (!NetworkCoverage.Try(node, ctx, "ID", out string? id) || id == null)
                    {
                        throw new InvalidOperationException("no ID");
                    }
                    // ResourceRules.IsStackable; "__stackable" (ResourceRules.Stackable) is a set marker, never a type.
                    if (NetworkCoverage.Try(node, ctx, "MaxStackSize", out int max) && max > 0 && !id.Equals("__stackable", StringComparison.OrdinalIgnoreCase))
                    {
                        stackable.Add(new Resource(id.ToLowerInvariant(), i, node, ctx));
                    }
                }
                catch (Exception ex)
                {
                    skipped.Add($"{path}: {ex.Message}");
                }
            }
        }
        string[] ids = BeltTypes.Order(stackable.Select(r => r.Id));
        BeltTypes.Set(ids);
        string choices;
        try
        {
            choices = AddChoices(root, ids, stackable);
        }
        catch (Exception ex)
        {
            choices = "filter choices NOT added: " + ex.Message;
        }
        return $"belt resource types: {ids.Length} ({BeltTypes.Vanilla.Length} vanilla"
            + (ids.Length > BeltTypes.Vanilla.Length ? " + " + string.Join(", ", ids.Skip(BeltTypes.Vanilla.Length)) : "")
            + $"), fingerprint {BeltTypes.Fingerprint:x8}; {choices}" + string.Concat(skipped.Select(s => "; unreadable " + s));
    }

    private static string AddChoices(OTFile root, string[] ids, List<Resource> stackable)
    {
        var plan = new List<(OTChildCollection Owner, string Text)>();
        var counts = new List<string>();
        foreach (var (toggleId, splitter) in new[] { (BeltState.FilterToggleId, false), (BeltState.SplitterFilterToggleId, true) })
        {
            OTParentNode choices = FindChoices(root, toggleId) ?? throw new InvalidOperationException(toggleId + " has no Choices list");
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (node, c) in choices.GetChildrenWithContext(null))
            {
                if (NetworkCoverage.Try(node, c, "ChoiceID", out string? cid) && cid != null)
                {
                    existing.Add(cid);
                }
            }
            int added = 0;
            for (int i = BeltTypes.Vanilla.Length; i < ids.Length; i++)
            {
                string choiceId = toggleId + "." + ids[i];
                if (existing.Contains(choiceId))
                {
                    continue; // a repeated run on the same rules tree
                }
                Resource r = stackable.First(x => x.Id == ids[i]);
                string tip = NetworkCoverage.Try(r.Node, r.Ctx, "NameKey", out string? key) && key != null ? $"ButtonToolTipKey = {Quote(key)}; " : "";
                // By the registered path, not the Icon node's own: the reference then resolves in the resource's inheritance context.
                string icon = r.Node.TryFindAtPath("Icon", out IOTNode? _, r.Ctx, out OTContext? _)
                    ? $"ButtonSprite = &{root.PathWithFile}/{Registry}/{r.Index.ToString(CultureInfo.InvariantCulture)}/Icon" : FallbackIcon;
                int mode = splitter ? i + 1 : BeltTypes.ModeOf(i);
                plan.Add((choices.LocalChildren, $"{{ ChoiceID = {Quote(choiceId)}; Mode = {mode.ToString(CultureInfo.InvariantCulture)}; {tip}{icon} }}"));
                added++;
            }
            counts.Add($"{toggleId} +{added}");
        }
        var scratch = (OTParentNode)new OTFile(new StringReader("L [ ]")).FindAtPath("L");
        foreach (var (_, text) in plan)
        {
            scratch.LocalChildren.Add(text); // phase 1: every text parses before the first real edit
        }
        var undo = new List<(OTChildCollection Owner, IOTNode Node)>();
        try
        {
            foreach (var (owner, text) in plan)
            {
                undo.AddRange(owner.Add(text).Select(n => (owner, n)));
            }
        }
        catch (Exception ex)
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                undo[i].Owner.Remove(undo[i].Node);
            }
            throw new InvalidOperationException($"{undo.Count} choice edits rolled back: {ex.Message}", ex);
        }
        return "filter choices " + string.Join(", ", counts);
    }

    private static OTParentNode? FindChoices(OTFile root, string toggleId)
    {
        if (!root.TryFindAtPath(Toggles, out IOTNode? reg) || reg is not OTParentNode toggles)
        {
            return null;
        }
        for (int i = 0; i < toggles.GetCount(); i++)
        {
            IOTNode t = root.FindAtPath(Toggles + "/" + i.ToString(CultureInfo.InvariantCulture), null, out OTContext? ctx);
            if (NetworkCoverage.Try(t, ctx, "ToggleID", out string? id) && id == toggleId)
            {
                return t.FindAtPath("Choices", ctx, out OTContext? _) as OTListNode;
            }
        }
        return null;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
