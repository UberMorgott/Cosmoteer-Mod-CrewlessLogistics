using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Halfling.ObjectText;

namespace CrewlessLogistics.Belts;

/// <summary>Headless resource-table checks (belts/tests GameTests, called by reflection) on a synthetic rules tree.</summary>
internal static partial class ResourceTable
{
    public static List<(bool Ok, string What)> SelfTest(string projectRoot)
    {
        var r = new List<(bool, string)>();
        string dir = Path.Combine(Path.GetTempPath(), "cl-resources-fixture-" + Environment.ProcessId);
        try
        {
            void Put(string rel, string text)
            {
                string path = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            foreach (string t in new[] { "belt_filter_toggle.rules", "belt_splitter_filter_toggle.rules" })
            {
                Put("mod/" + t, File.ReadAllText(Path.Combine(projectRoot, "mod", t)));
            }
            Put("Data/gui/game/parts/part_toggles.rules", "PartToggles [ &<../../../../mod/belt_filter_toggle.rules>/Toggle, &<../../../../mod/belt_splitter_filter_toggle.rules>/Toggle ]");
            Put("Data/cosmoteer.rules", "Resources [ &<resources/derived.rules>/Res, &<resources/plain.rules>/Res, &<resources/noicon.rules>/Res, &<resources/flat.rules>/Res ]");
            // Codex P1: the base Icon reads a field only the derived resource sets (the ":" inheritor step resolves only in the derived context).
            Put("Data/resources/base.rules", "Res { MaxStackSize = 5; NameKey = \"Fx/Base\"; Icon { Texture { File = &../../:/Image }; Size = [64, 64] } }");
            Put("Data/resources/derived.rules", "Res : <base.rules>/Res { ID = Fx_Derived; Image = \"d.png\" }");
            Put("Data/resources/plain.rules", "Res { ID = fx_plain; MaxStackSize = 3; NameKey = \"Fx/Plain\"; Icon { Texture { File = \"p.png\" }; Size = [64, 64] } }");
            Put("Data/resources/noicon.rules", "Res { ID = fx_noicon; MaxStackSize = 2; NameKey = \"Fx/NoIcon\" }");
            Put("Data/resources/flat.rules", "Res { ID = fx_flat; NameKey = \"Fx/Flat\" }");
            var root = new OTFile(Path.Combine(dir, "Data", "cosmoteer.rules"));
            // The former reference (the Icon node's own path, in the base file) loses that context and breaks the rules load.
            string oldPath = root.FindAtPath("Resources/0/Icon", null, out OTContext? _).PathWithFile;
            root.LocalChildren.Add("OldProbe = &" + oldPath);
            bool oldFails;
            try
            {
                oldFails = !NetworkCoverage.Try(root, null, "OldProbe/Texture/File", out string? _);
            }
            catch (OTNavigateException)
            {
                oldFails = true;
            }
            r.Add((oldFails, "T1 fixture reproduces the context loss of a direct reference to " + oldPath));
            string summary = Apply(root);
            string[] modded = BeltTypes.Ids.Skip(BeltTypes.Vanilla.Length).ToArray();
            r.Add((modded.SequenceEqual(new[] { "fx_derived", "fx_noicon", "fx_plain" }), "T1 " + summary));
            string? Choice(string toggle, int choice, out int mode)
            {
                IOTNode t = root.FindAtPath(Toggles + "/" + toggle, null, out OTContext? ctx);
                mode = NetworkCoverage.Try(t, ctx, $"Choices/{choice}/Mode", out int m) ? m : -1;
                return NetworkCoverage.Try(t, ctx, $"Choices/{choice}/ButtonSprite/Texture/File", out string? f) ? f : null;
            }
            int vanillaConnector = BeltTypes.ModeNone + 1, vanillaSplitter = BeltTypes.Vanilla.Length + 1;
            string?[] got = new[] { Choice("0", vanillaConnector, out int m0), Choice("0", vanillaConnector + 1, out int m1), Choice("0", vanillaConnector + 2, out int m2), Choice("1", vanillaSplitter, out int s0) };
            r.Add((got[0] == "d.png" && got[3] == "d.png", $"T1 inherited icon resolves in the derived context: connector {got[0]}, splitter {got[3]}"));
            r.Add((got[1] == "./Data/ships/terran/storage_2x2/icon.png" && got[2] == "p.png", $"T1 no icon -> cargo icon ({got[1]}), plain icon {got[2]}"));
            r.Add((m0 == 24 && m1 == 25 && m2 == 26 && s0 == 22, $"T1 explicit modes connector {m0},{m1},{m2} splitter {s0}"));
            string again = Apply(root);
            r.Add((again.Contains("belt_filter +0", StringComparison.Ordinal) && again.Contains("belt_splitter_filter +0", StringComparison.Ordinal), "T1 second run adds nothing: " + again));
        }
        catch (Exception ex)
        {
            r.Add((false, "T1 fixture threw " + ex));
        }
        finally
        {
            BeltTypes.Set(BeltTypes.Vanilla);
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
        return r;
    }
}
