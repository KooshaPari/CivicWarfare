using System.Text.Json;
using System.Text.Json.Serialization;

namespace CivicSurvivalInstaller;

public static partial class Program
{
    private record Manifest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = ModName.ToLowerInvariant();
        [JsonPropertyName("name")] public string Name { get; set; } = ModName;
        [JsonPropertyName("source_dll")] public string SourceDll { get; set; } = "";
        [JsonPropertyName("installed_version")] public string InstalledVersion { get; set; } = "";
        [JsonPropertyName("installed_at_utc")] public DateTime InstalledAtUtc { get; set; }
        [JsonPropertyName("last_updated_at_utc")] public DateTime LastUpdatedAtUtc { get; set; }
        [JsonPropertyName("installer")] public string Installer { get; set; } = "civicsurvival-installer";
    }

    private static async Task WriteManifest(string pluginDir, string modDll, bool isUpdate)
    {
        var path = Path.Combine(pluginDir, $"{ModName}.json");
        var manifest = File.Exists(path)
            ? JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path)) ?? new Manifest()
            : new Manifest { InstalledAtUtc = DateTime.UtcNow };
        manifest.SourceDll = Path.GetFileName(modDll);
        manifest.InstalledVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        manifest.LastUpdatedAtUtc = DateTime.UtcNow;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void StampInstalled(string pluginDir)
        => File.WriteAllText(Path.Combine(pluginDir, InstallStampFile), $"{ModName} installed at {DateTime.UtcNow:o}");

    private static bool IsInstalled(string pluginDir)
        => Directory.Exists(pluginDir) && File.Exists(Path.Combine(pluginDir, InstallStampFile));

    private static string? TryReadStamp(string pluginDir)
    {
        var path = Path.Combine(pluginDir, InstallStampFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static bool HasOption(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));

    private static string? FindSiblingPublishXml(string source)
    {
        try
        {
            var dir = File.Exists(source) ? Path.GetDirectoryName(source)! : source;
            if (string.IsNullOrEmpty(dir)) return null;
            foreach (var candidate in new[]
            {
                Path.Combine(dir, "Properties", "PublishConfiguration.xml"),
                Path.Combine(dir, "PublishConfiguration.xml"),
            })
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        catch
        {
            // Fall back to synthesized metadata when the source cannot be read.
        }
        return null;
    }

    private static string MakePublishConfigurationXml() => $"""
<?xml version='1.0' encoding='utf-8'?>
<Configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <ModName Value="CivicSurvival" />
  <ModVersion Value="{ModVersion}" />
  <ModAuthor Value="KooshaPari" />
  <ModDescription Value="Phase-staged social-survival gameplay mod for Cities: Skylines II." />
  <ChangeLog Value="0.3.25" />
</Configuration>
""";

    private static string MakeSkyveManifest(string modDll)
    {
        var manifest = new
        {
            modId = ModName,
            versionId = ModVersion,
            loadOrder = 0,
            name = "CivicSurvival",
            author = "KooshaPari",
            description = "Phase-staged social-survival gameplay mod for Cities: Skylines II.",
            dll = Path.GetFileName(modDll),
            dependencies = new[] { new { id = "BepInEx", version = "5.4.21.0" } },
            updatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }
}
