using System.Text.Json;
using System.Runtime.CompilerServices;

sealed partial class NativeProbe
{
    sealed record CableAppearancePrimitive(string Component, string Slot, string Layer, string? Gate, float[] Location, float[] Size, float[] Offset, float[] Color, string File, int UVRotation = 0, bool MirrorU = false, bool MirrorV = false, float Rotation = 0, int DamageLevel = 0);

    void CableAppearancePreview()
    {
        object Open(string path) => Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
        var vanillaCross = Open(Path.Combine(gameRoot, "Data", "ships", "terran", "heat_pipe_crossing", "heat_pipe_crossing.rules"));
        var vanillaBlend = Open(Path.Combine(gameRoot, "Data", "ships", "terran", "heat_pipe_adaptive", "heat_pipe_blend_sprites.rules"));
        var styles = new Dictionary<string, List<CableAppearancePrimitive>>();
        float[] Pair(object value) => new[] { Convert.ToSingle(Property(value, "X")), Convert.ToSingle(Property(value, "Y")) };
        float[] Channels(object value) => new[] { "R", "G", "B", "A" }.Select(name => Convert.ToSingle(Field(value.GetType(), name).GetValue(value))).ToArray();
        string FileName(object sprite) => ((Array)Property(sprite, "Files")).GetValue(0)!.ToString()!;
        // Baked art (tools/Bake-CableSprites.ps1) keeps the native sprite name stem:
        // floor_<shape><damage>.png -> cable_<kind>_<shape><damage>.png, doodads<damage>.png -> cable_crossing_<kind><damage>.png.
        string Baked(string native, string kind)
        {
            string leaf = Path.GetFileName(native);
            string name = leaf.StartsWith("floor_") ? "cable_" + kind + "_" + leaf[6..] : leaf.StartsWith("doodads") ? "cable_crossing_" + kind + leaf[7..] : throw new InvalidOperationException("Unexpected native cable art " + native);
            return Path.GetFullPath(Path.Combine(projectRoot, "mod", "assets", name));
        }
        // kind: null = unchanged native art, "body" = baked body, "lamp" = baked lights_add lamp (VertexColor x4 HDR).
        void SameSprite(object actual, object expected, string context, bool seamOverlap = false, string? kind = null)
        {
            foreach (string property in new[] { "Offset", "UVRotation", "MirrorU", "MirrorV" }) Require(Equals(Property(actual, property), Property(expected, property)), "Native sprite geometry differs: " + context + "/" + property);
            if (kind == "lamp") Require(Channels(Property(actual, "VertexColor")).SequenceEqual(new[] { 4f, 4f, 4f, 1f }), "Baked lamp must restore its quarter-scale HDR emission: " + context);
            else Require(Equals(Property(actual, "VertexColor"), Property(expected, "VertexColor")), "Native sprite color differs: " + context);
            Require(seamOverlap ? Pair(Property(actual, "Size")).SequenceEqual(new[] { 1f, 1.0625f }) : Equals(Property(actual, "Size"), Property(expected, "Size")), "Pipe size must retain native width and only the explicit two-pixel axial seam overlap: " + context);
            if (kind == null) Require(FileName(actual) == FileName(expected), "Original native damage art differs: " + context);
            else {
                string baked = Baked(FileName(expected), kind);
                Require(Path.GetFullPath(FileName(actual)) == baked && File.Exists(baked), "Cable art must be the baked sprite of the matching native shape/damage frame: " + context + " -> " + baked);
            }
        }
        foreach (string style in new[] { "armored", "crossing" }) {
            var source = Open(Path.Combine(projectRoot, "mod", style == "armored" ? "cable.rules" : "cable_crossing.rules"));
            object Read(string name) => Rules("Part/Components/" + name, "Cosmoteer.Ships.Parts.PartComponentRules", source);
            var primitives = new List<CableAppearancePrimitive>();
            void AddSprite(string name, string slot, string layer, string? gate, float[] location, object sprite, int damage, float[]? componentOffset = null)
            {
                componentOffset ??= new[] { 0f, 0f };
                var offset = Pair(Property(sprite, "Offset"));
                var fileName = FileName(sprite);
                var absolute = Path.IsPathRooted(fileName) ? fileName : Path.GetFullPath(Path.Combine(gameRoot, fileName));
                Require(File.Exists(absolute), "Cable appearance asset missing: " + absolute);
                primitives.Add(new(name, slot, layer, gate, location, Pair(Property(sprite, "Size")), new[] { offset[0] + componentOffset[0], offset[1] + componentOffset[1] }, Channels(Property(sprite, "VertexColor")), absolute,
                    (int)Property(sprite, "UVRotation"), (bool)Property(sprite, "MirrorU"), (bool)Property(sprite, "MirrorV"), 0, damage));
            }
            var graphics = style == "crossing" ? new[] { "CableBackground", "CableBody", "CableGlow" } : new[] { "CableBackground" };
            foreach (string name in graphics) {
                var rules = Read(name);
                var gate = Field(rules.GetType(), "OperationalToggle").GetValue(rules)?.ToString();
                var location = Pair(Field(rules.GetType(), "Location").GetValue(rules)!);
                Require(Field(rules.GetType(), "OperationalRoofDoodad").GetValue(rules) == null, "Detailed cable art must never render over exterior hull");
                foreach (string slot in new[] { "Floor", "Walls", "Roof", "OperationalDoodad" }) {
                    var group = Field(rules.GetType(), slot).GetValue(rules);
                    if (group == null) continue;
                    var sprites = (Array)Field(group.GetType(), "DamageLevels").GetValue(group)!;
                    Require(sprites.Length == 3, "Every native cable physical graphics group must retain all three damage levels: " + name + "/" + slot);
                    for (int damage = 0; damage < sprites.Length; damage++) AddSprite(name, slot, Field(group.GetType(), "Layer").GetValue(group)!.ToString()!, gate, location, sprites.GetValue(damage)!, damage, Pair(Field(group.GetType(), "Offset").GetValue(group)!));
                }
            }
            var roof = primitives.Single(p => p.Component == "CableBackground" && p.Slot == "Roof" && p.DamageLevel == 0);
            Require(roof.File.Replace('\\', '/').EndsWith(style == "armored" ? "/armor/roof.png" : "/corridor/roof.png", StringComparison.OrdinalIgnoreCase), "Exterior must retain plain vanilla hull");
            if (style == "crossing") {
                var original = Rules("Part/Components/Graphics", "Cosmoteer.Ships.Parts.PartComponentRules", vanillaCross);
                var background = Read("CableBackground");
                var body = Read("CableBody");
                Require(Equals(Field(original.GetType(), "Location").GetValue(original), Field(background.GetType(), "Location").GetValue(background)), "Crossing floor/wall location differs from native");
                Require(Equals(Field(original.GetType(), "Location").GetValue(original), Field(body.GetType(), "Location").GetValue(body)), "Crossing cylinder location differs from native");
                foreach (string slot in new[] { "Floor", "Walls", "OperationalDoodad" }) {
                    var actual = Field((slot == "OperationalDoodad" ? body : background).GetType(), slot).GetValue(slot == "OperationalDoodad" ? body : background)!;
                    var expected = Field(original.GetType(), slot).GetValue(original)!;
                    var actualSprites = (Array)Field(actual.GetType(), "DamageLevels").GetValue(actual)!;
                    var expectedSprites = (Array)Field(expected.GetType(), "DamageLevels").GetValue(expected)!;
                    Require(actualSprites.Length == expectedSprites.Length, "Crossing damage frame count differs from native");
                    for (int damage = 0; damage < actualSprites.Length; damage++) SameSprite(actualSprites.GetValue(damage)!, expectedSprites.GetValue(damage)!, "crossing/" + slot + "/" + damage, slot == "OperationalDoodad", slot == "OperationalDoodad" ? "body" : null);
                }
                Require(Field(body.GetType(), "OperationalToggle").GetValue(body) == null, "Disconnected crossing must retain its native physical orientation");
                var glow = Read("CableGlow");
                Require(Field(glow.GetType(), "OperationalToggle").GetValue(glow)?.ToString() == "CrewlessLogisticsAvailable", "Crossing groove glow must require a full available source cell");
                var nativeDoodad = (Array)Field(Field(original.GetType(), "OperationalDoodad").GetValue(original)!.GetType(), "DamageLevels").GetValue(Field(original.GetType(), "OperationalDoodad").GetValue(original)!)!;
                var lampGroup = Field(glow.GetType(), "OperationalDoodad").GetValue(glow)!;
                var lampSprites = (Array)Field(lampGroup.GetType(), "DamageLevels").GetValue(lampGroup)!;
                Require(lampSprites.Length == nativeDoodad.Length, "Crossing lamp lost native damage frames");
                for (int damage = 0; damage < lampSprites.Length; damage++) SameSprite(lampSprites.GetValue(damage)!, nativeDoodad.GetValue(damage)!, "crossing/lamp/" + damage, true, "lamp");
                Require(primitives.Where(p => p.Component == "CableBody").All(p => p.Layer == "doodads_high"), "Crossing cylinder must use the vanilla doodads_high layer above native walls");
                Require(primitives.Where(p => p.Component == "CableGlow").All(p => p.Layer == "lights_add"), "Crossing lamp must use the vanilla additive light layer");
            } else {
                var native = Rules("FloorBlendSprites", "Cosmoteer.Ships.Parts.PartComponentRules", vanillaBlend);
                foreach (string name in new[] { "CableBody", "CableGlow" }) {
                    var rules = Read(name);
                    var layer = Field(rules.GetType(), "Layer").GetValue(rules)!.ToString()!;
                    var gate = Field(rules.GetType(), "OperationalToggle").GetValue(rules)?.ToString();
                    Require(layer == (name == "CableBody" ? "doodads_low" : "lights_add"), "Adaptive cable uses the wrong vanilla layer");
                    Require(gate == (name == "CableBody" ? null : "CrewlessLogisticsAvailable"), "Adaptive groove glow lost native availability gating");
                    var variants = new HashSet<string>();
                    for (uint state = 0; state < 16; state++) {
                        var selected = Call(rules, "GetUnambiguousSprite", state)!;
                        var original = Call(native, "GetUnambiguousSprite", state)!;
                        var sprites = (Array)Field(selected.GetType(), "DamageLevelSprites").GetValue(selected)!;
                        var originals = (Array)Field(original.GetType(), "DamageLevelSprites").GetValue(original)!;
                        Require(sprites.Length == 3 && originals.Length == 3, "Every adaptive state must retain all native damage levels");
                        for (int damage = 0; damage < 3; damage++) {
                            SameSprite(sprites.GetValue(damage)!, originals.GetValue(damage)!, name + "/" + state + "/" + damage, false, name == "CableBody" ? "body" : "lamp");
                            AddSprite(name, "OperationalDoodad", layer, (gate == null ? "" : gate + "|") + "Situation_" + state, Pair(Field(rules.GetType(), "Location").GetValue(rules)!), sprites.GetValue(damage)!, damage);
                            variants.Add(FileName(sprites.GetValue(damage)!));
                        }
                    }
                    Require(variants.Count == 18, "Adaptive art must retain six shapes times three native damage levels");
                    if (name == "CableGlow") {
                        // Execute the native shutdown method without constructing GPU resources.
                        // A dormant native managed quad is enough to prove clear-on-off behavior.
                        NewTopologyManager(); NewTopologyPart(0, 0, 0);
                        var component = Component(rules);
                        SetField(component, "_quad", RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Rendering.ManagedShipQuad")));
                        Call(component, "OnNonOperational");
                        Require(Field(component.GetType(), "_quad").GetValue(component) == null, "Native ToggledBlendSprites failed to clear the groove when its gate turns off");
                    }
                }
            }
            Require(primitives.All(p => !p.Component.StartsWith("CableTrace") && !p.Component.StartsWith("CableGlass")), "Rectangle traces must not survive the masked sprite redesign");
            styles.Add(style, primitives);
        }
        var target = Path.Combine(projectRoot, "artifacts", "cable-appearance-native.json");
        File.WriteAllText(target, JsonSerializer.Serialize(new { generatedAtUtc = DateTimeOffset.UtcNow, description = "Native floor/wall geometry with baked cable body/lamp sprites per native shape and damage frame, all 16 adaptive connection states; lamps on vanilla lights_add gated by CrewlessLogisticsAvailable.", styles }, IndentedJson));
        scenarioResults.Add(new { name = "native exact pipe art and masked groove glow", adaptiveStates = 16, nativeVariants = 6, damageLevels = 3, exactCrossingFloorWalls = true, rectangleTraces = false, exteriorCableArt = false, gpuDraw = false, artifact = target });
        Console.WriteLine("PASS: all16 native body/glow states and three damage levels; exact native crossing floor/walls/cylinder; availability shutdown clears native quad; plain roofs.");
    }
}
