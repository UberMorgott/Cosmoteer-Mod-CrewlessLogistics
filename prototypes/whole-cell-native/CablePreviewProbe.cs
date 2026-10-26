using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

sealed partial class NativeProbe
{
    void CablePreviewRegression()
    {
        object Open(string path) => Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
        var current = Open(Path.Combine(projectRoot, "mod", "cable.rules"));
        var vanilla = Open(Path.Combine(gameRoot, "Data", "ships", "terran", "heat_pipe_adaptive", "heat_pipe_blend_sprites.rules"));
        var rendererType = Game("Cosmoteer.Ships.Blueprints.Graphics.BlueprintToggledBlendSpritesOverlayRenderer");
        var renderer = Activator.CreateInstance(rendererType, true)!;
        var shipRules = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.ShipRules"));
        SetField(shipRules, "ShipScale", 1f);
        var partRules = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.PartRules"));
        SetField(partRules, "Size", Vector(1, 1));
        SetField(partRules, "ShipRules", shipRules);
        object Data(int rotation, bool flip) {
            var info = Activator.CreateInstance(Game("Cosmoteer.Ships.Parts.PartInfo"), new[] { partRules, Vector(3, 4), (object)rotation, flip })!;
            return Activator.CreateInstance(Game("Cosmoteer.Ships.Blueprints.Graphics.PartOverlayRenderData"), new object?[] { info, null })!;
        }
        // Negative control: the exact empty-array shape shipped by 0.2.0 must still crash the native renderer.
        var unsafePath = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "preview-regression.rules");
        File.WriteAllText(unsafePath, "UnsafePreview { Type = BlueprintToggledBlendSprites; Location = [.5, .5]; SituationToggles = [Port_Up]; AmbiguousSprites [ { SituationCode = \"0\"; AllowRotation = false; DamageLevelSprites [] } { SituationCode = \"1\"; AllowRotation = false; DamageLevelSprites [ { File = \"./Data/roof_textures/_none_.png\"; Size = [.2, .4] } ] } ] }");
        bool reproduced = false;
        try { Call(renderer, "CalculateBlendSpriteData", Data(0, false), Rules("UnsafePreview", "Cosmoteer.Ships.Parts.PartComponentRules", Open(unsafePath))); }
        catch (TargetInvocationException error) when (error.InnerException is IndexOutOfRangeException) { reproduced = true; }
        Require(reproduced, "Negative control must reproduce the original empty-array blueprint crash");
        var rules = Rules("Part/Components/CableBlueprints", "Cosmoteer.Ships.Parts.PartComponentRules", current);
        var native = Rules("BlueprintBlendSprites", "Cosmoteer.Ships.Parts.PartComponentRules", vanilla);
        void Visible(object sprite) {
            var size = Property(sprite, "Size");
            Require(Convert.ToSingle(Property(size, "X")) > 0f && Convert.ToSingle(Property(size, "Y")) > 0f, "Placement preview sprite has zero area");
            var color = Property(sprite, "VertexColor");
            Require(Convert.ToSingle(Field(color.GetType(), "A").GetValue(color)) > 0f, "Placement preview sprite is fully transparent");
            var file = ((Array)Property(sprite, "Files")).GetValue(0)!.ToString()!;
            Require(File.Exists(Path.IsPathRooted(file) ? file : Path.Combine(gameRoot, file)), "Placement preview texture is missing");
        }
        var shapes = new HashSet<string>();
        for (uint state = 0; state < 16; state++) {
            var selected = Call(rules, "GetUnambiguousSprite", state)!;
            var expected = Call(native, "GetUnambiguousSprite", state)!;
            var sprites = (Array)Field(selected.GetType(), "DamageLevelSprites").GetValue(selected)!;
            var originals = (Array)Field(expected.GetType(), "DamageLevelSprites").GetValue(expected)!;
            Require(sprites.Length > 0, "Adaptive placement preview contains an unsafe empty sprite array");
            var sprite = sprites.GetValue(0)!; Visible(sprite);
            var file = ((Array)Property(sprite, "Files")).GetValue(0)!.ToString()!;
            Require(file == ((Array)Property(originals.GetValue(0)!, "Files")).GetValue(0)!.ToString(), "Adaptive preview must reuse the selected native thermal shape");
            shapes.Add(file);
        }
        Require(shapes.Count == 6, "Adaptive preview lost a native connection shape");
        int cases = 0;
        for (int rotation = 0; rotation < 4; rotation++) foreach (bool flip in new[] { false, true }) {
            var list = (IList)Field(rendererType, "_data").GetValue(renderer)!; list.Clear();
            // Part=null is the actual held-part path: it selects state zero.
            Call(renderer, "CalculateBlendSpriteData", Data(rotation, flip), rules);
            Require(list.Count == 1, "Held adaptive cable preview produced no native quad");
            Visible(Property(list[0]!, "Sprite")); cases++;
        }
        var fixedParts = new List<object>();
        foreach (string name in new[] { "cable_crossing", "cable_heat_crossing" }) {
            var source = Open(Path.Combine(projectRoot, "mod", name + ".rules"));
            var spriteRules = Rules("Part/Components/Blueprints", "Cosmoteer.Ships.Parts.PartComponentRules", source);
            var sprites = ((IEnumerable)Call(spriteRules, "GetBlueprintAtlasSprites")!).Cast<object>().ToArray();
            Require(sprites.Length == 1, "Fixed crossing must expose one native preview sprite");
            Visible(sprites[0]);
            var file = ((Array)Property(sprites[0], "Files")).GetValue(0)!.ToString()!;
            Require(file.Replace('\\', '/').EndsWith("/assets/" + name + "_icon.png"), "Fixed placement preview must show the matching cable artwork, not inherited armor");
            Require(Call(spriteRules, "CreateBlueprintComponent") != null, "Fixed crossing lacks its native blueprint component");
            fixedParts.Add(new { name, file, spriteSize = new[] { Convert.ToSingle(Property(Property(sprites[0], "Size"), "X")), Convert.ToSingle(Property(Property(sprites[0], "Size"), "Y")) } });
        }
        var report = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "cable-placement-validation.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow, adaptiveStates = 16, nativeShapes = shapes, heldRendererCases = cases, fixedParts, originalEmptyArrayCrashReproduced = reproduced, limits = "Native blueprint rules, sprite selection and held-part CPU quad calculation; no game UI/GPU drawing." }, new JsonSerializerOptions { WriteIndented = true }));
        scenarioResults.Add(new { name = "visible native cable placement previews", adaptiveStates = 16, heldRendererCases = cases, fixedParts = 2, reproducedOriginalIndexOutOfRange = reproduced, gpuDraw = false });
        Console.WriteLine($"PASS: visible cable placement previews; native six-shape adaptive selection, {cases} held-part renderer cases and both fixed crossing sprites; evidence={report}");
    }
}

