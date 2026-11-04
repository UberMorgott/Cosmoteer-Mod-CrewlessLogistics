using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;

sealed partial class NativeProbe
{
    void ConnectConsumerProxy(object proxy, object? storage)
    {
        var handler = Field(proxy.GetType(), "_proxy").GetValue(proxy)!;
        if (storage != null) SetField(handler, "_proxiedPart", part);
        handler.GetType().GetProperty("ProxiedComponent", All)!.SetValue(handler, storage);
    }

    object ConsumerCoverageInput(object rules, object store)
    {
        // Seed membership only; native package query/category rules and native IO
        // perform matching, available-capacity tracking, and whole-cell delivery.
        var io = Component(rules);
        const string prefix = "Cosmoteer.Ships.Networks.Queries.";
        var arguments = new[] { Game("Cosmoteer.Ships.Parts.Part"), Game("Cosmoteer.Ships.Parts.PartComponent") };
        var cacheType = Game(prefix + "SubnetworkQueryCache`2").MakeGenericType(arguments);
        var cache = Activator.CreateInstance(cacheType, All, null, new object?[] { null }, null)!;
        var query = Activator.CreateInstance(Game(prefix + "PartNetworkResourceQuery"), All, null, new[] { Field(rules.GetType(), "ResourceType").GetValue(rules), Field(rules.GetType(), "NetworkFilter").GetValue(rules) }, null)!;
        var resultType = Game(prefix + "SubnetworkResourceSinkQueryResult`2").MakeGenericType(arguments);
        var result = Activator.CreateInstance(resultType, All, null, new[] { cache, query }, null)!;
        Require((bool)Call(result, "MatchesCriteria", store)!, "Actual reactor consumer query rejects actual package receiver");
        var data = (IList)Field(resultType, "_data").GetValue(result)!;
        var indices = (IDictionary)Field(resultType, "_dataIndices").GetValue(result)!;
        indices.Add(store, data.Count);
        data.Add(Call(result, "OnAddition", store));
        var queriesField = Field(cacheType, "_resourceSinkQueries");
        var queries = (IDictionary)Activator.CreateInstance(queriesField.FieldType)!;
        queries.Add(query, result);
        queriesField.SetValue(cache, queries);
        SetField(io, "_subnetworkSinks", result);
        return io;
    }

    void ValidateRocketLocalDraw(object root, string prefix, string name, object battery)
    {
        object Read(string component) => Rules(prefix + component, "Cosmoteer.Ships.Parts.PartComponentRules", root);
        // Reproduce native aggregate links without neighboring ship/buff discovery.
        foreach (string side in name == "thruster_rocket_battery" ? new[] { "LeftExtendableBatteryStorage", "RightExtendableBatteryStorage" } : new[] { "LeftBatteryStorage", "RightBatteryStorage" }) Component(Read(side));
        var extended = Component(Read("ExtendableBatteryStorage"));
        InitializeLinks(extended);
        object draw = extended;
        if (name != "thruster_rocket_battery") {
            var localProxy = Component(Read("ToggledExtendableBatteryStorage"));
            ConnectConsumerProxy(localProxy, extended);
            Component(Read("PrevCombinedBatteryStorage"));
            draw = Component(Read("CombinedBatteryStorage"));
            InitializeLinks(draw);
        }
        if (name == "thruster_rocket_nozzle") Require((string)ReadRuleObject(root, prefix + "MainThruster/FuelStorage", typeof(string)) == "CombinedBatteryStorage", "Rocket nozzle fuel does not reference the native aggregate");
        int before = Count(battery, "Resources");
        Add(draw, -1000);
        Require(Count(battery, "Resources") == before - 1000, "Native rocket draw does not consume mapped local battery: " + name);
    }
}
