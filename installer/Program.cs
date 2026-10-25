// CivicSurvival Mod Installer - .NET 9 Console App (cross-platform, BepInEx-aware,
// Skyve-aware via the game-side PDX PublishConfiguration.xml mirror).
//
// Usage:
//   civicsurvival-installer install    <mod-dll-or-built-publishdir> [--game-path <path>] [--launch]
//   civicsurvival-installer update     <new-mod-dll-or-built-publishdir> [--game-path <path>]
//   civicsurvival-installer remove     [--game-path <path>] [--keep-config]
//   civicsurvival-installer status     [--game-path <path>]
//   civicsurvival-installer launch     [--game-path <path>] [--steam|--direct]
//   civicsurvival-installer check                                  (pre-flight only)
//   civicsurvival-installer self-update [path-to-new-installer]
//
// Cross-platform: win-x64, linux-x64, osx-arm64 native bins via `dotnet publish -r ...`.
//
// Game-side mirror contract (PDX Mods compatible):
//   <Game>/BepInEx/plugins/CivicSurvival/CivicSurvival.dll
//   <Game>/BepInEx/plugins/CivicSurvival/CivicSurvival.skyve.json  (Skyve manifest)
//   <Game>/BepInEx/plugins/CivicSurvival/CivicSurvival.json        (installer manifest)
//   <Game>/BepInEx/plugins/CivicSurvival/PublishConfiguration.xml  (PDX game-side mirror)
//   <Game>/BepInEx/plugins/CivicSurvival/.civicsurvival-installed   (install stamp)
//
// Why game-side PublishConfiguration.xml?
// PDX Mods launcher reads PublishConfiguration.xml at <Game>/BepInEx/plugins/<Mod>/
// to discover mod metadata (ModName, ModVersion, ChangeLog). The src tree ships
// CivicSurvival/Properties/PublishConfiguration.xml; the installer mirrors it
// into the game plugins dir so the launcher (Skyve / Paradox Mods) can pick it up
// without us shipping a redundant copy.

using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CivicSurvivalInstaller;

public static partial class Program
{
    private const string ModName = "CivicSurvival";
    private const string PluginFolderName = "CivicSurvival";
    private const string InstallStampFile = ".civicsurvival-installed";
    private const string LatestReleaseApi = "https://api.github.com/repos/KooshaPari/CivicSurvival-public/releases/latest";
    // Single source of truth for version strings baked into this binary.
    // Bumped via `dotnet run -- bump --version X.Y.Z` (todo) or hand-edit before publish.
    private const string ModVersion = "0.3.25";
    private const string ModNameFull = "CivicSurvival";
    // BepInEx layout on Windows: <Game>/BepInEx/plugins/<PluginFolder>/<dll>
    // With monomod (built-in), BepInEx loads everything in plugins/. We still
    // sandbox into our own subfolder so install/remove stays atomic and idempotent.

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 64; // EX_USAGE
            }

            var gamePath = ResolveGamePath(GetOption(args, "--game-path"));
            var command = args[0].ToLowerInvariant();
            var positional = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();

            int code;
            switch (command)
            {
                case "install":
                    code = await InstallAsync(positional, gamePath, args);
                    break;
                case "update":
                    code = await UpdateAsync(positional, gamePath);
                    break;
                case "remove":
                case "uninstall":
                    code = await RemoveAsync(gamePath, HasOption(args, "--keep-config"));
                    break;
                case "status":
                    code = await Task.Run(() => Status(gamePath));
                    break;
                case "check":
                    code = await Task.Run(() => Check(gamePath));
                    break;
                case "launch":
                    code = await LaunchAsync(gamePath, args);
                    break;
                case "self-update":
                    code = SelfUpdate(positional);
                    break;
                case "version":
                case "--version":
                case "-v":
                    code = Version();
                    break;
                case "help":
                case "--help":
                case "-h":
                    code = Help();
                    break;
                default:
                    code = Unknown(command);
                    break;
            }
            return code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // ---------------------------------------------------------------------
    // Subcommands
    // ---------------------------------------------------------------------

    private static async Task<int> InstallAsync(string[] positional, string gamePath, string[] args)
    {
        if (positional.Length == 0)
        {
            Console.Error.WriteLine("install: missing source (path to .dll or built publish dir)");
            return 64;
        }
        var pluginDir = Path.Combine(PluginsRoot(gamePath), PluginFolderName);
        EnsureDirectory(pluginDir);

        // If we're passed a built publish dir, prefer the .dll from there; otherwise
        // treat the path as a direct dll.
        var source = positional[0];
        var modDll = ResolveModDll(source);

        // Prefer a sibling PublishConfiguration.xml from the source dir; otherwise
        // ship our own. The src repo's Properties/PublishConfiguration.xml is the
        // canonical content for the in-game mirror.
        var pdxXml = FindSiblingPublishXml(source) ?? MakePublishConfigurationXml();
        var skyve = MakeSkyveManifest(modDll);

        var targetDll = Path.Combine(pluginDir, $"{ModName}.dll");
        var targetPdx = Path.Combine(pluginDir, "PublishConfiguration.xml");
        var targetSkyve = Path.Combine(pluginDir, $"{ModName}.skyve.json");

        File.Copy(modDll, targetDll, overwrite: true);
        await File.WriteAllTextAsync(targetPdx, pdxXml);
        await File.WriteAllTextAsync(targetSkyve, skyve);
        await WriteManifest(pluginDir, modDll, isUpdate: false);

        StampInstalled(pluginDir);
        Console.WriteLine($"installed: {targetDll}");
        Console.WriteLine($"pdx-xml  : {targetPdx}");
        Console.WriteLine($"skyve    : {targetSkyve}");

        if (HasOption(args, "--launch"))
        {
            return await LaunchAsync(gamePath, args);
        }
        return 0;
    }

    private static async Task<int> UpdateAsync(string[] positional, string gamePath)
    {
        if (positional.Length == 0)
        {
            Console.Error.WriteLine("update: missing source (path to new .dll or built publish dir)");
            return 64;
        }
        var pluginDir = Path.Combine(PluginsRoot(gamePath), PluginFolderName);
        if (!IsInstalled(pluginDir))
        {
            Console.Error.WriteLine("update: mod not installed; run install first");
            return 1;
        }
        var source = positional[0];
        var modDll = ResolveModDll(source);
        var pdxXml = FindSiblingPublishXml(source) ?? MakePublishConfigurationXml();
        var skyve = MakeSkyveManifest(modDll);

        var targetDll = Path.Combine(pluginDir, $"{ModName}.dll");
        var targetPdx = Path.Combine(pluginDir, "PublishConfiguration.xml");
        var targetSkyve = Path.Combine(pluginDir, $"{ModName}.skyve.json");

        File.Copy(modDll, targetDll, overwrite: true);
        await File.WriteAllTextAsync(targetPdx, pdxXml);
        await File.WriteAllTextAsync(targetSkyve, skyve);
        await WriteManifest(pluginDir, modDll, isUpdate: true);
        Console.WriteLine($"updated: {targetDll}");
        return 0;
    }

    private static int Status(string gamePath)
    {
        var pluginDir = Path.Combine(PluginsRoot(gamePath), PluginFolderName);
        if (!Directory.Exists(pluginDir))
        {
            Console.WriteLine("not installed.");
            return 0;
        }
        var stamp = TryReadStamp(pluginDir);
        var modsJson = Path.Combine(pluginDir, $"{ModName}.json");
        Console.WriteLine($"installed: yes  ({pluginDir})");
        if (File.Exists(modsJson))
        {
            var json = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(modsJson));
            if (json is not null)
            {
                Console.WriteLine($"source-dll: {json.SourceDll}");
                Console.WriteLine($"version   : {json.InstalledVersion}");
                Console.WriteLine($"installed : {json.InstalledAtUtc:u}");
                Console.WriteLine($"updated   : {json.LastUpdatedAtUtc:u}");
            }
        }
        if (stamp is not null) Console.WriteLine($"stamp     : {stamp}");
        return 0;
    }

    private static async Task<int> LaunchAsync(string gamePath, string[] args)
    {
        var exe = Path.Combine(gamePath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Cities2.exe"
            : "Cities2.x86_64");
        if (HasOption(args, "--direct") || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!File.Exists(exe))
            {
                Console.Error.WriteLine($"launch: game executable not found at {exe}");
                return 1;
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = gamePath,
                UseShellExecute = true,
            };
            Console.WriteLine($"launching: {exe}");
            Process.Start(startInfo);
            return 0;
        }

        // Steam URL handler: `start steam://run/<appid>` -- Steam owns the launch
        // (auth, DLC check, VR, etc.). This is the canonical PDX Mods-compatible path.
        const string cs2AppId = "949230";
        var uri = $"steam://run/{cs2AppId}";
        Console.WriteLine($"launching via Steam: {uri}");
        var psi = new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true,
        };
        Process.Start(psi);
        await Task.CompletedTask;
        return 0;
    }

    private static int Check(string gamePath)
    {
        var pluginDir = Path.Combine(PluginsRoot(gamePath), PluginFolderName);
        var exe = Path.Combine(gamePath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Cities2.exe" : "Cities2.x86_64");
        var bepinex = Path.Combine(gamePath, "BepInEx", "core");
        var diags = new List<(string label, bool ok, string detail)>
        {
            ("game install", Directory.Exists(gamePath), gamePath),
            ("game executable", File.Exists(exe), exe),
            ("BepInEx core", Directory.Exists(bepinex), bepinex),
            ("mod installed", IsInstalled(pluginDir), Path.Combine(pluginDir, InstallStampFile)),
            ("PublishConfiguration.xml", File.Exists(Path.Combine(pluginDir, "PublishConfiguration.xml")), "<game>/BepInEx/plugins/CivicSurvival/PublishConfiguration.xml"),
            ("Skyve manifest", File.Exists(Path.Combine(pluginDir, $"{ModName}.skyve.json")), "<game>/BepInEx/plugins/CivicSurvival/CivicSurvival.skyve.json"),
        };
        var ok = true;
        foreach (var (label, pass, detail) in diags)
        {
            Console.WriteLine($"  [{(pass ? "OK " : "FAIL")}] {label,-22} {detail}");
            if (!pass) ok = false;
        }
        return ok ? 0 : 1;
    }

    private static async Task<int> RemoveAsync(string gamePath, bool keepConfig)
    {
        var pluginDir = Path.Combine(PluginsRoot(gamePath), PluginFolderName);
        if (!Directory.Exists(pluginDir))
        {
            Console.WriteLine("not installed.");
            return 0;
        }
        if (keepConfig)
        {
            // Preserve BepInEx config files under plugins/CivicSurvival/Configuration/
            // if the user opted to keep their config; only remove the DLL + manifests.
            var configDir = Path.Combine(pluginDir, "Configuration");
            string? preserved = null;
            if (Directory.Exists(configDir))
            {
                preserved = Path.Combine(Path.GetTempPath(), $"civicsurvival-cfg-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
                Directory.Move(configDir, preserved);
            }
            foreach (var f in Directory.EnumerateFiles(pluginDir))
            {
                var n = Path.GetFileName(f);
                if (n.Equals("CivicSurvival.dll", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".skyve.json", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && n.StartsWith(ModName, StringComparison.OrdinalIgnoreCase)
                    || n.Equals("PublishConfiguration.xml", StringComparison.OrdinalIgnoreCase)
                    || n.Equals(InstallStampFile, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(f);
                }
            }
            if (preserved != null && Directory.Exists(configDir))
            {
                Directory.Move(preserved, configDir);
            }
            await Task.CompletedTask;
        }
        else
        {
            Directory.Delete(pluginDir, recursive: true);
            await Task.CompletedTask;
        }
        Console.WriteLine($"removed: {pluginDir}");
        return 0;
    }

    private static int Version()
    {
        var v = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        Console.WriteLine($"civicsurvival-installer {v}");
        return 0;
    }

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

}
