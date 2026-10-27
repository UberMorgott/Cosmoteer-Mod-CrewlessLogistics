using System.Text.Json;
using System.Text.RegularExpressions;

sealed partial class NativeProbe
{
    void LocalizationProbe(object modFile, string modDirectory)
    {
        Require((string)ReadRuleObject(modFile, "StringsFolder", typeof(string)) == "strings", "Missing native language folder");
        var referencePattern = new Regex("(?:NameKey|IconNameKey|DescriptionKey|ButtonToolTipKey)\\s*=\\s*\"(CrewlessLogistics/[^\"]+)\"");
        var references = Directory.GetFiles(modDirectory, "*.rules", SearchOption.AllDirectories)
            .SelectMany(path => referencePattern.Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Require(references.Count == 32, "Unexpected set of user-visible Crewless Logistics strings; update language coverage");
        var values = new Dictionary<string, Dictionary<string, string>>();
        // Every game language (Data/strings/<lang>.rules) has its own table.
        string[] languages = { "en", "ru", "de", "es", "fr", "ja", "pt-br", "zh-cn" };
        foreach (string language in languages) {
            string path = Path.Combine(modDirectory, "strings", language + ".rules");
            var source = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
            var declared = Regex.Matches(File.ReadAllText(path), @"(?m)^\s*(\w+)\s*=")
                .Select(match => "CrewlessLogistics/" + match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            Require(declared.SetEquals(references), "Language keys missing or unused: " + language);
            var translated = new Dictionary<string, string>();
            foreach (string key in references) {
                // Resolve every UI key through the native ObjectText parser;
                // each language must work without falling back to English.
                string text = (string)ReadRuleObject(source, key, typeof(string));
                Require(!string.IsNullOrWhiteSpace(text) && text != key && !text.Contains('\uFFFD'), "Missing or damaged translation: " + language + "/" + key);
                Require(language == "ru" ? Regex.IsMatch(text, "[А-Яа-яЁё]") : !Regex.IsMatch(text, "[А-Яа-яЁё]"), "Wrong translation language: " + language + "/" + key);
                if (language == "ja") Require(Regex.IsMatch(text, "[\u3040-\u30FF]"), "Japanese text needs kana: " + key);
                if (language == "zh-cn") Require(Regex.IsMatch(text, "[\u4E00-\u9FFF]") && !Regex.IsMatch(text, "[\u3040-\u30FF]"), "Chinese text needs Han without kana: " + key);
                translated.Add(key, text);
            }
            values.Add(language, translated);
        }
        foreach (string language in languages.Skip(1))
            Require(references.All(key => values[language][key] != values["en"][key]), "Keys must have their own translation: " + language);
        foreach (string language in languages)
            Require(File.Exists(Path.Combine(gameRoot, "Data", "strings", language + ".rules")), "Not a game language: " + language);
        // Native ModInfo.Name/Description are plain strings, not language keys.
        // Keep the mod-list metadata bilingual rather than inventing a schema.
        string description = (string)ReadRuleObject(modFile, "Description", typeof(string));
        Require(description.Contains("RU:") && description.Contains("EN:") && description.Contains('\n'), "Mod-list description must include both languages");
        string report = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "localization-validation.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow, languages = values.Keys, uiKeys = references.OrderBy(key => key), translatedStrings = values.Sum(pair => pair.Value.Count), nativeParser = true, metadata = "Bilingual plain strings; native ModInfo has no localized name/description fields", limits = "Native rules parsing and exact UI key coverage; no live language switch or GUI rendering." }, IndentedJson));
        Console.WriteLine($"PASS: {values.Count}-language localization ({string.Join("/", values.Keys)}); {references.Count} UI keys per language, bilingual mod metadata; evidence={report}");
    }
}
