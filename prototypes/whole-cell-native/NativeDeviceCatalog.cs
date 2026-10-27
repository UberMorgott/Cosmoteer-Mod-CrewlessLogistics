using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed partial class NativeProbe
{
    // Installation-time metadata discovery only. This never loads mod DLLs or
    // constructs complete PartRules, sprites, sounds, a ship, or a live world.
    public void BuildDeviceCatalog(bool validateCandidate = false)
    {
        string originalDirectory = Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory = gameRoot;
            object Open(string path) => Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
            bool Has(object source, string path)
            {
                var method = serializer.GetType().GetMethods(All).Where(m => m.Name == "HasPath" && !m.IsGenericMethod).OrderBy(m => m.GetParameters().Length).First();
                var parameters = method.GetParameters(); var args = new object?[parameters.Length]; args[0] = source; args[1] = path;
                for (int i = 2; i < args.Length; i++) args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                return (bool)method.Invoke(serializer, args)!;
            }
            T? Optional<T>(object source, string path, T? fallback = default) => Has(source, path) ? (T)ReadRuleObject(source, path, typeof(T)) : fallback;
            object[] Nodes(object source, string path) => ((IEnumerable)ReadRuleObject(source, path, Core("Halfling.ObjectText.OTNode").MakeArrayType())).Cast<object>().ToArray();
            string[] Names(object source, string path)
            {
                if (!Has(source, path)) return Array.Empty<string>();
                var type = typeof(Dictionary<,>).MakeGenericType(typeof(string), Core("Halfling.ObjectText.OTNode"));
                return ((IDictionary)ReadRuleObject(source, path, type)).Keys.Cast<string>().ToArray();
            }
            static Exception Inner(Exception error) { while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException; return error; }
            string settingsPath = SettingsPath(); var settings = Open(settingsPath);
            var enabledPaths = (string[])ReadRuleObject(settings, "GameSettings/EnabledMods", typeof(string[]));
            var mods = new List<CatalogMod>();
            foreach (string enabled in enabledPaths) {
                string directory = Path.GetFullPath(Path.IsPathRooted(enabled) ? enabled : Path.Combine(Path.GetDirectoryName(settingsPath)!, enabled));
                if (!Directory.Exists(directory)) continue; // ModInfo.LoadMods only loads existing folders.
                if (ModManifest(directory) is not string selected) continue; var manifests = new[] { selected };

                var manifest = Open(manifests[0]); string id = (string)ReadRuleObject(manifest, "ID", typeof(string));
                if (IsSelfModId(id)) continue; // includes the pre-0.3.3 ID of an old installed package
                var actionType = typeof(List<>).MakeGenericType(Game("Cosmoteer.Mods.ModAction"));
                IList actions;
                try { actions = Has(manifest, "Actions") ? (IList)ReadRuleObject(manifest, "Actions", actionType) : new ArrayList(); } catch (Exception error) { throw new InvalidOperationException($"Cannot parse enabled mod native actions: {id} ({manifests[0]}). {Inner(error).Message}", error); }
                mods.Add(new CatalogMod(id, directory, manifests[0], Optional<string>(manifest, "Version"), actions, Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal).ToArray()));
            }
            mods.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            var root = Open(Path.Combine(gameRoot, "Data", "cosmoteer.rules"));
            var actionReport = new List<object>();
            foreach (var mod in mods) foreach (object action in mod.Actions) {
                    try { Call(action, "ApplyAction", root); } catch (Exception error) { throw new InvalidOperationException($"Cannot apply enabled mod native action: {mod.Id}, {action.GetType().Name}. {Inner(error).Message}", error); }
                    actionReport.Add(new { modId = mod.Id, actionType = action.GetType().Name });
                }
            const string registry = "<ships/terran/terran.rules>/Terran/Parts";
            var partNodes = Nodes(root, registry); var reports = new List<object>();
            for (int i = 0; i < partNodes.Length; i++) {
                string prefix = registry + "/" + i; var node = partNodes[i];
                string canonical = Property(node, "PathWithFile").ToString()!.Replace('\\', '/');
                string? sourcePath = Property(Property(node, "FileRoot"), "FilePath")?.ToString();
                var owner = mods.Where(m => sourcePath != null && Path.GetFullPath(sourcePath).StartsWith(m.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Directory.Length).FirstOrDefault();
                string id = (string)ReadRuleObject(root, prefix + "/ID", typeof(string));
                var components = new Dictionary<string, CatalogComponent>(StringComparer.Ordinal);
                void Scan(string group, string? enclosingToggle = null)
                {
                    foreach (string name in Names(root, group)) {
                        string path = group + "/" + name;
                        string? type = Optional<string>(root, path + "/Type");
                        if (type == null) continue;
                        if (components.ContainsKey(name)) {
                            string? resource = Optional<string>(root, path + "/ResourceType");
                            if (resource == "battery") throw new InvalidOperationException("Repeated battery component ID in conditional branches requires explicit mapping: " + name);
                            if (type == "ToggledComponents") Scan(path + "/Components", Optional<string>(root, path + "/Toggle"));
                            continue;
                        }
                        var entry = new CatalogComponent { Id = name, Type = type, Path = path, EnclosingToggle = enclosingToggle };
                        entry.ResourceType = Optional<string>(root, path + "/ResourceType");
                        foreach (string field in new[] { "Storage", "ResourceStorage", "FromStorage", "ToStorage", "ComponentID", "ProxyToggle", "SuppliesResourcesToggle", "OperationalToggle" }) if (Has(root, path + "/" + field)) entry.References[field] = ReadRuleObject(root, path + "/" + field, typeof(string));
                        if (Has(root, path + "/ResourceStorages")) entry.References["ResourceStorages"] = ReadRuleObject(root, path + "/ResourceStorages", typeof(string[]));
                        if (Has(root, path + "/ProxyableComponents")) {
                            var proxies = Nodes(root, path + "/ProxyableComponents");
                            entry.References["ProxyableComponents"] = Enumerable.Range(0, proxies.Length).Select(n => Optional<string>(root, path + "/ProxyableComponents/" + n + "/ComponentID")).ToArray();
                        }
                        if (Has(root, path + "/PartLocation")) entry.References["PartLocation"] = ReadRuleObject(root, path + "/PartLocation", typeof(int[]));
                        foreach (string field in new[] { "QuantityScale", "FromQuantity", "ToQuantity", "MinResourcesPickUp", "MaxResourcesPickUp" }) if (Has(root, path + "/" + field)) {
                                try { entry.Values[field] = ReadRuleObject(root, path + "/" + field, typeof(float)); } catch { entry.MetadataWarnings.Add(field + " is a native modifiable value, not a plain scalar."); }
                            }
                        // false = crew deliver only while OperationalToggle is on (hyperdrives: FtlDrive spool-up).
                        if (Has(root, path + "/AllowNonOperationalMinPriorityDelivery")) entry.Values["AllowNonOperationalMinPriorityDelivery"] = ReadRuleObject(root, path + "/AllowNonOperationalMinPriorityDelivery", typeof(bool));
                        if (Has(root, path + "/MaxResources")) {
                            try { entry.Values["MaxResources"] = ReadRuleObject(root, path + "/MaxResources", typeof(int)); } catch (Exception error) { entry.MetadataWarnings.Add("MaxResources is not a plain native integer: " + Inner(error).Message.Split('\n')[0]); }
                        }
                        components.Add(name, entry);
                        if (type == "ToggledComponents") Scan(path + "/Components", Optional<string>(root, path + "/Toggle"));
                    }
                }
                string? metadataError = null;
                try { Scan(prefix + "/Components"); } catch (Exception error) { metadataError = Inner(error).Message; }
                var receivers = components.Values.Where(c => c.Type == "ResourceConsumer" && c.ResourceType == "battery" && c.References.ContainsKey("Storage")).ToArray();
                var storage = components.Values.Where(c => c.ResourceType == "battery" && (c.Type.Contains("Storage") || c.Type == "InlineResourceConverter")).ToArray();
                var thermalPorts = components.Values.Where(c => c.Type == "NetworkPort" && Optional<string[]>(root, c.Path + "/Types", Array.Empty<string>())!.Contains("thermal")).Select(c => new {
                    id = c.Id,
                    location = (int[])ReadRuleObject(root, c.Path + "/Location", typeof(int[])),
                    direction = ReadRuleObject(root, c.Path + "/Direction", Field(Game("Cosmoteer.Ships.Networks.PartNetworkPortRules"), "Direction").FieldType).ToString(),
                    types = (string[])ReadRuleObject(root, c.Path + "/Types", typeof(string[]))
                }).ToArray();
                var category = Optional<string[]>(root, prefix + "/TypeCategories", Array.Empty<string>())!;
                string classification = "non-electrical"; string? unsupported = metadataError; string? receiving = null; string? backing = null;
                if (receivers.Length > 0) receiving = receivers[0].References["Storage"].ToString();
                if (category.Contains("reactor")) { classification = "reactor"; backing = components.Values.Where(c => c.Type == "ResourceConverter" && c.References.ContainsKey("ToStorage")).Select(c => c.References["ToStorage"].ToString()).FirstOrDefault(s => s != null && components.TryGetValue(s, out var v) && v.ResourceType == "battery"); } else if (category.Contains("provides_power") && receiving != null) { classification = "capacitor"; backing = receiving; } else if (receiving != null) { classification = "consumer"; backing = receiving; }
                var knownExceptions = new Dictionary<string, string> { ["cosmoteer.engine_room"] = "BatteryStorage", ["cosmoteer.thruster_rocket_nozzle"] = "LocalBatteryStorage", ["cosmoteer.thruster_rocket_extender"] = "LocalBatteryStorage", ["cosmoteer.thruster_rocket_battery"] = "LocalBatteryStorage" };
                if (knownExceptions.TryGetValue(id, out string? physical)) { backing = physical; receiving = physical; }
                // Rocket-style extendable stores alias several neighboring parts.
                // Expose the unique directly-owned physical reserve once, not the
                // aggregate's overlapping network capacity or scaled proxies.
                if (classification == "consumer" && backing != null && components.TryGetValue(backing, out var aggregate) && aggregate.Type == "MultiResourceStorage" && aggregate.References.TryGetValue("ResourceStorages", out var children)) {
                    var directPhysical = ((string[])children).Where(n => components.TryGetValue(n, out var c) && c.Type == "ResourceStorage" && c.ResourceType == "battery").ToArray();
                    var otherMembers = ((string[])children).Where(n => !directPhysical.Contains(n)).Select(n => components.GetValueOrDefault(n)).ToArray();
                    if (directPhysical.Length == 1 && otherMembers.Length > 0 && otherMembers.All(c => c?.Type == "ResourceStorageProxy" && c.References.ContainsKey("PartLocation"))) { backing = directPhysical[0]; receiving = backing; }
                }
                var physicalIds = new HashSet<string>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
                string? Resolve(string? name)
                {
                    if (name == null || !components.TryGetValue(name, out var component)) return "Referenced battery storage is absent: " + name;
                    if (!seen.Add(name)) return "Cyclic or overlapping storage alias: " + name;
                    if (component.ResourceType != "battery") return "Storage is not standard battery resource: " + name;
                    if (component.Type == "ResourceStorage") {
                        physicalIds.Add(name);
                        if (!component.Values.TryGetValue("MaxResources", out var capacity)) return "Physical battery capacity could not be resolved as an integer: " + name;
                        int units = Convert.ToInt32(capacity);
                        return units >= 1000 && units % 1000 == 0 ? null : "Battery capacity requires a separate whole-cell mapping (not a positive multiple of 1000): " + name;
                    }
                    if (component.Type == "ResourceStorageProxy") {
                        if (component.References.TryGetValue("PartLocation", out var location) && ((int[])location).Any(x => x != 0)) return "Receiving proxy targets another part and requires explicit local-storage mapping: " + name;
                        if (component.MetadataWarnings.Any(w => w.StartsWith("QuantityScale"))) return "Battery proxy scale could not be resolved: " + name;
                        if (component.Values.TryGetValue("QuantityScale", out var scale) && Convert.ToDouble(scale) != 1) return "Scaled battery proxy requires explicit energy-safe mapping: " + name;
                        if (component.References.TryGetValue("ComponentID", out var target)) return Resolve(target.ToString());
                        return "Dynamic multi-target battery proxy requires explicit mapping: " + name;
                    }
                    if (component.Type == "MultiResourceStorage" && component.References.TryGetValue("ResourceStorages", out var members)) {
                        foreach (string member in (string[])members) { var error = Resolve(member); if (error != null) return error; }
                        return null;
                    }
                    return "Unsupported battery storage type: " + component.Type + " (" + name + ")";
                }
                if (classification != "non-electrical" && unsupported == null) unsupported = Resolve(backing);
                if (receivers.Length > 1 && classification == "consumer") unsupported ??= "Multiple distinct crew battery receivers need explicit selection.";
                if (classification == "reactor" && backing == null) unsupported ??= "No native battery-producing converter target was found.";
                if (unsupported != null && classification != "non-electrical") classification = "unsupported";
                var prohibitRects = new List<object>();
                if (Has(root, prefix + "/ProhibitRects")) {
                    int rectCount = Nodes(root, prefix + "/ProhibitRects").Length;
                    for (int n = 0; n < rectCount; n++) prohibitRects.Add(new { category = (string)ReadRuleObject(root, prefix + "/ProhibitRects/" + n + "/0", typeof(string)), rect = (int[])ReadRuleObject(root, prefix + "/ProhibitRects/" + n + "/1", typeof(int[])) });
                }
                var prohibits = Optional<string[]>(root, prefix + "/Prohibits", new[] { "default" });
                reports.Add(new { id, registryIndex = i, registryPath = prefix, canonicalPartGroupPath = canonical, sourcePath, modId = owner?.Id ?? "vanilla", sourceModHasDll = owner?.DllFiles.Length > 0, size = (int[])ReadRuleObject(root, prefix + "/Size", typeof(int[])), physicalRect = Optional<int[]>(root, prefix + "/PhysicalRect"), prohibitRects, prohibits, prohibitSides = new { left = Optional<int>(root, prefix + "/ProhibitLeft"), right = Optional<int>(root, prefix + "/ProhibitRight"), above = Optional<int>(root, prefix + "/ProhibitAbove"), below = Optional<int>(root, prefix + "/ProhibitBelow"), categories = prohibits }, prohibitedBy = Optional<string[]>(root, prefix + "/ProhibitedBy", new[] { "default" }), typeCategories = category, classification, unsupportedReason = unsupported, receiverStorage = receiving, backingStorage = backing, physicalStorageIds = physicalIds.Order(StringComparer.Ordinal).ToArray(), batteryReceivers = receivers, batteryStorages = storage, thermalPorts, componentCount = components.Count, metadataError });
            }
            var runtimeModAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && mods.Any(m => a.Location.StartsWith(m.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).Select(a => a.GetName().Name).ToArray();
            if (runtimeModAssemblies.Length != 0) throw new InvalidOperationException("Catalog unexpectedly loaded enabled-mod DLL assemblies.");
            // Discovery must remain possible when the previous generated
            // compatibility is stale. Validate the candidate after generation.
            var enabledRootSmoke = validateCandidate ? ValidateEnabledRootRegistry() : null;
            string output = Path.Combine(projectRoot, "docs", "device-catalog.json");
            File.WriteAllText(output, JsonSerializer.Serialize(new { generatedAtUtc = DateTimeOffset.UtcNow, gameVersion = "0.30.5", settingsPath = PortablePath(settingsPath), excludedModIds = SelfModIds, mods = mods.Select(m => new { id = m.Id, directory = PortablePath(m.Directory), manifest = PortablePath(m.Manifest), version = m.Version, nativeActions = m.Actions.Count, dllFiles = m.DllFiles }), appliedActions = actionReport, runtimeModAssemblies, enabledRootSmoke, registeredPartCount = partNodes.Length, parts = reports, limits = "Metadata only; mod DLLs never loaded. Known native battery schemas are classified; custom resource/receiver semantics require explicit mappings. Regenerate after enabled mods or versions change." }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Console.WriteLine($"PASS: enabled-mod native device catalog; mods={mods.Count}, parts={partNodes.Length}, actions={actionReport.Count}; output={output}");
        } finally { Environment.CurrentDirectory = originalDirectory; }
    }
    // Validate generated compatibility against the real enabled native action
    // order, including Crewless Logistics when requested. Mod DLL code is never loaded.
    public object BuildEnabledRulesRoot(bool includeCrewlessLogistics)
    {
        string originalDirectory = Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory = gameRoot;
            object Open(string path) => Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
            string settingsPath = SettingsPath(); var settings = Open(settingsPath);
            var enabled = (string[])ReadRuleObject(settings, "GameSettings/EnabledMods", typeof(string[]));
            var mods = new List<(string Id, IList Actions)>();
            bool candidateIncluded = false;
            foreach (string configured in enabled) {
                string directory = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(Path.GetDirectoryName(settingsPath)!, configured));
                if (!Directory.Exists(directory)) continue;
                if (ModManifest(directory) is not string selected) continue; var manifests = new[] { selected };

                // Validate the workspace candidate, not a stale installed package.
                // Other enabled mods always load from their exact configured paths.
                string manifestPath = manifests[0]; var manifest = Open(manifestPath);
                string id = (string)ReadRuleObject(manifest, "ID", typeof(string));
                if (IsSelfModId(id)) {
                    id = SelfModIds[0];
                    if (!includeCrewlessLogistics) continue;
                    manifest = Open(Path.Combine(projectRoot, "mod", "mod.rules"));
                    candidateIncluded = true;
                }
                var has = serializer.GetType().GetMethods(All).Where(m => m.Name == "HasPath" && !m.IsGenericMethod).OrderBy(m => m.GetParameters().Length).First();
                var parameters = has.GetParameters(); var args = new object?[parameters.Length]; args[0] = manifest; args[1] = "Actions";
                for (int n = 2; n < args.Length; n++) args[n] = parameters[n].HasDefaultValue ? parameters[n].DefaultValue : null;
                var actionsType = typeof(List<>).MakeGenericType(Game("Cosmoteer.Mods.ModAction"));
                IList actions = (bool)has.Invoke(serializer, args)! ? (IList)ReadRuleObject(manifest, "Actions", actionsType) : new ArrayList();
                mods.Add((id, actions));
            }
            // Candidate verification must also work while the user has the
            // installed package disabled. Never edit their enabled-mod settings.
            if (includeCrewlessLogistics && !candidateIncluded) {
                var candidate = Open(Path.Combine(projectRoot, "mod", "mod.rules"));
                var actionsType = typeof(List<>).MakeGenericType(Game("Cosmoteer.Mods.ModAction"));
                mods.Add(("morgott.crewless_logistics", (IList)ReadRuleObject(candidate, "Actions", actionsType)));
            }
            mods.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            var root = Open(Path.Combine(gameRoot, "Data", "cosmoteer.rules"));
            foreach (var mod in mods) foreach (object action in mod.Actions) {
                    try { Call(action, "ApplyAction", root); } catch (Exception error) { throw new InvalidOperationException("Enabled-mod validation failed: " + mod.Id + "/" + action.GetType().Name, error); }
                }
            return root;
        } finally { Environment.CurrentDirectory = originalDirectory; }
    }
    public object ValidateEnabledRootRegistry()
    {
        string originalDirectory = Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory = gameRoot;
            var baseline = BuildEnabledRulesRoot(false); var candidate = BuildEnabledRulesRoot(true);
            string[] Ids(object source)
            {
                var nodes = (IEnumerable)ReadRuleObject(source, "<ships/terran/terran.rules>/Terran/Parts", Core("Halfling.ObjectText.OTNode").MakeArrayType());
                return nodes.Cast<object>().Select((_, i) => (string)ReadRuleObject(source, "<ships/terran/terran.rules>/Terran/Parts/" + i + "/ID", typeof(string))).ToArray();
            }
            var before = Ids(baseline); var after = Ids(candidate);
            var addedIds = new[] { "morgott.crewless_logistics.cable", "morgott.crewless_logistics.cable_crossing", "morgott.crewless_logistics.cable_heat_crossing", "morgott.crewless_logistics.belt", "morgott.crewless_logistics.belt_splitter", "morgott.crewless_logistics.belt_connector", "morgott.crewless_logistics.belt_heat_crossing", "morgott.crewless_logistics.belt_cable_crossing" };
            if (after.Length != before.Length + addedIds.Length || after.Distinct(StringComparer.Ordinal).Count() != after.Length || before.Any(id => !after.Contains(id))) throw new InvalidOperationException("Enabled-root smoke check detected missing/duplicated parts or duplicate Crewless Logistics application.");
            if (addedIds.Any(id => !after.Contains(id))) throw new InvalidOperationException("Enabled-root smoke check did not find every Crewless Logistics cable/crossover.");
            return new { verified = true, baselineParts = before.Length, candidateParts = after.Length, candidate = "workspace mod/mod.rules", actionOrdering = "ModInfo.LoadMods ordinal ModID ordering; each native action applied once to a fresh OT root", dllExecution = false };
        } finally { Environment.CurrentDirectory = originalDirectory; }
    }
    public string? ModsDirectory { get; init; }    // Same manifest choice as the game (mod.rules / mod_*.rules by
    // CompatibleGameVersions); null means the game skips the folder.
    string? ModManifest(string directory)
    {
        var app = Core("Halfling.App").GetProperty("ObjectTextSerializer", BindingFlags.Static | BindingFlags.Public)!;
        if (app.GetValue(null) == null) app.SetValue(null, serializer);
        var path = Game("Cosmoteer.Mods.ModInfo").GetMethod("GetModInfoPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { Activator.CreateInstance(Core("Halfling.IO.AbsolutePath"), directory) });
        return path?.ToString();
    }
    // Current mod ID first; the legacy ID is the pre-0.3.3 package name.
    static readonly string[] SelfModIds = { "morgott.crewless_logistics", "morgott.powergrid" };
    static bool IsSelfModId(string id) => Array.IndexOf(SelfModIds, id) >= 0;
    // The profile's Settings.rules sits next to its Mods folder. Without an
    // explicit Mods folder, a single profile is used even before first install.
    string SettingsPath()
    {
        if (ModsDirectory != null) return Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ModsDirectory)))!, "Settings.rules");
        var candidates = Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Cosmoteer"), "Settings.rules", SearchOption.AllDirectories);
        if (candidates.Length > 1) candidates = candidates.Where(p => (Directory.Exists(Path.Combine(Path.GetDirectoryName(p)!, "Mods", "CrewlessLogistics")) || Directory.Exists(Path.Combine(Path.GetDirectoryName(p)!, "Mods", "Powergrid")))).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException($"Cannot select one Cosmoteer profile ({candidates.Length} candidates); pass -ModsDirectory.");
        return candidates[0];
    }
    // Generated docs are public: replace the local profile root and Steam ID folder.
    static string PortablePath(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) path = "%USERPROFILE%" + path.Substring(profile.Length);
        return Regex.Replace(path, @"([\\/]Cosmoteer[\\/])\d{17}(?=[\\/]|$)", "$1<SteamID>");
    }
    sealed record CatalogMod(string Id, string Directory, string Manifest, string? Version, IList Actions, string[] DllFiles);
    sealed class CatalogComponent
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Path { get; set; } = "";
        public string? ResourceType { get; set; }
        public string? EnclosingToggle { get; set; }
        public Dictionary<string, object> References { get; } = new();
        public Dictionary<string, object> Values { get; } = new();
        public List<string> MetadataWarnings { get; } = new();
    }
}
