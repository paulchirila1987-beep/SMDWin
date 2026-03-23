using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// Loads and serves localisation strings from JSON language pack files.
    /// Built-in packs (en, ro) are embedded; additional packs can be imported.
    /// </summary>
    public static class LanguageService
    {
        // The loaded dictionary: section → key → value
        private static Dictionary<string, Dictionary<string, string>> _data = new();
        private static string _currentCode = "en";

        // ── Language pack metadata ─────────────────────────────────────────────
        public static string CurrentCode   => _currentCode;
        public static string CurrentName   => Get("Meta", "Name", _currentCode);

        // Folder next to the .exe where users drop extra language packs
        private static string LangFolder =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Languages");

        // ── Discover available packs ───────────────────────────────────────────
        public static List<(string Code, string Name, string Path)> GetAvailablePacks()
        {
            var result = new List<(string, string, string)>();

            // Built-in packs embedded as content files
            foreach (var code in new[] { "en", "ro" })
            {
                var path = Path.Combine(LangFolder, $"{code}.json");
                if (File.Exists(path))
                {
                    string name = code == "en" ? "English" : "Română";
                    try
                    {
                        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
                        if (raw != null && raw.TryGetValue("Meta", out var meta))
                        {
                            var metaDict = JsonSerializer.Deserialize<Dictionary<string, string>>(meta.GetRawText());
                            if (metaDict != null && metaDict.TryGetValue("Name", out var n)) name = n;
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                    result.Add((code, name, path));
                }
            }

            // Extra imported packs
            if (Directory.Exists(LangFolder))
            {
                foreach (var file in Directory.GetFiles(LangFolder, "*.json"))
                {
                    string code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (code == "en" || code == "ro") continue; // already added
                    try
                    {
                        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file));
                        string name = code, displayCode = code;
                        if (raw != null && raw.TryGetValue("Meta", out var meta))
                        {
                            var metaDict = JsonSerializer.Deserialize<Dictionary<string, string>>(meta.GetRawText());
                            if (metaDict != null)
                            {
                                if (metaDict.TryGetValue("Name", out var n)) name = n;
                                if (metaDict.TryGetValue("Code", out var c)) displayCode = c;
                            }
                        }
                        result.Add((displayCode, name, file));
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Unhandled exception"); }
                }
            }

            return result;
        }

        // ── Load a language pack ───────────────────────────────────────────────
        public static bool Load(string code)
        {
            var packs = GetAvailablePacks();
            var pack = packs.Find(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

            string? json = null;

            if (!string.IsNullOrEmpty(pack.Path) && File.Exists(pack.Path))
            {
                try { json = File.ReadAllText(pack.Path); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }

            if (json == null)
            {
                // Fallback to English
                var en = packs.Find(p => p.Code == "en");
                if (!string.IsNullOrEmpty(en.Path) && File.Exists(en.Path))
                    try { json = File.ReadAllText(en.Path); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }

            if (json == null) return false;

            try
            {
                var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (root == null) return false;

                _data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (section, elem) in root)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(elem.GetRawText());
                    if (dict != null)
                        _data[section] = dict;
                }

                _currentCode = code.ToLowerInvariant();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── Import an external language pack into the Languages folder ─────────
        public static (bool Success, string Message) ImportPack(string sourcePath)
        {
            try
            {
                var json = File.ReadAllText(sourcePath);
                var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (root == null) return (false, "Invalid JSON format.");

                string code = Path.GetFileNameWithoutExtension(sourcePath).ToLowerInvariant();
                if (root.TryGetValue("Meta", out var meta))
                {
                    var metaDict = JsonSerializer.Deserialize<Dictionary<string, string>>(meta.GetRawText());
                    if (metaDict != null && metaDict.TryGetValue("Code", out var c))
                        code = c.ToLowerInvariant();
                }

                Directory.CreateDirectory(LangFolder);
                var dest = Path.Combine(LangFolder, $"{code}.json");
                File.Copy(sourcePath, dest, overwrite: true);
                return (true, $"Language pack '{code}' imported successfully.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ── Get a string ───────────────────────────────────────────────────────
        public static string Get(string section, string key, string? fallback = null)
        {
            if (_data.TryGetValue(section, out var dict) && dict.TryGetValue(key, out var val))
                return val;
            return fallback ?? $"[{section}.{key}]";
        }

        // Convenience accessors
        public static string S(string section, string key) => Get(section, key);
        // BUG-005/013 FIX: overload with fallback so model computed properties compile safely
        public static string S(string section, string key, string fallback) => Get(section, key, fallback);
    }
}
