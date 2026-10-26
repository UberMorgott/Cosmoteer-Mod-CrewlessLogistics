using System.Collections;

sealed partial class NativeProbe
{
    void ValidateCableMaterialLayer(object root)
    {
        // Cable art uses vanilla layers (doodads_low, doodads_high, lights_add); only
        // the mixed crossover's thermal cylinder needs one extra vanilla-shader layer.
        const string layers = "<ships/terran/terran.rules>/Terran/RenderLayers";
        var buckets = new Dictionary<string, int>();
        var nodes = (IEnumerable)ReadRuleObject(root, layers, Core("Halfling.ObjectText.OTNode").MakeArrayType());
        int index = 0, thermal = -1;
        foreach (var node in nodes) {
            string path = layers + "/" + index + "/";
            string key = (string)ReadRuleObject(root, path + "Key", typeof(string));
            Require(!key.StartsWith("CrewlessLogisticsCable"), "Custom cable shader layer survived the baked sprite switch: " + key);
            buckets[key] = (int)ReadRuleObject(root, path + "Value/UniqueBucket", typeof(int));
            if (key == "CrewlessLogisticsThermalBridge") { Require(thermal < 0, "Duplicate thermal bridge layer"); thermal = index; }
            index++;
        }
        Require(thermal >= 0, "Thermal bridge layer is absent");
        string thermalPath = layers + "/" + thermal + "/Value/";
        Require((string)ReadRuleObject(root, thermalPath + "RenderStage", typeof(string)) == "High", "Thermal bridge must render in the high stage");
        Require((string)ReadRuleObject(root, thermalPath + "Material/Shader", typeof(string)) == "./Data/ships/common/parts.shader", "Thermal bridge must use the vanilla parts shader");
        Require((int)ReadRuleObject(root, thermalPath + "AtlasTextureParams/MipLevels", typeof(int)) == 7, "Thermal bridge lost native atlas mip settings");
        Require(buckets.Values.Count(bucket => bucket == buckets["CrewlessLogisticsThermalBridge"]) == 1, "Thermal bridge bucket collides with another layer");
        Require(buckets["floors"] < buckets["doodads_low"] && buckets["doodads_low"] < buckets["walls"], "Adaptive body must draw above floors and below walls");
        Require(buckets["walls"] < buckets["doodads_high"] && buckets["doodads_high"] < buckets["CrewlessLogisticsThermalBridge"] && buckets["CrewlessLogisticsThermalBridge"] < buckets["lights_add"],
            "Crossing body must cover walls, thermal cylinder cover the electric body, thermal light stay on top");
        Console.WriteLine("PASS: cable art on vanilla layers; only the thermal bridge layer is added, ordered walls < doodads_high < thermal < lights_add.");
    }
}
