using System.Runtime.InteropServices;

namespace CivicSurvivalInstaller;

public static partial class Program
{
    private static int SelfUpdate(string[] positional)
    {
        if (positional.Length == 0)
        {
            Console.Error.WriteLine("self-update: missing path to new installer exe");
            return 64;
        }
        var newExe = positional[0];
        if (!File.Exists(newExe))
        {
            Console.Error.WriteLine($"self-update: not found: {newExe}");
            return 1;
        }
        var thisExe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot resolve own path");
        var backup = thisExe + ".prev";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(thisExe, backup);
        File.Copy(newExe, thisExe, overwrite: true);
        Console.WriteLine($"self-update: {thisExe} <- {newExe}");
        Console.WriteLine($"backup    : {backup}");
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        PrintUsage();
        return 64;
    }

    private static string ResolveGamePath(string? overridePath)
    {
        if (!string.IsNullOrEmpty(overridePath) && Directory.Exists(overridePath)) return overridePath!;
        var envGame = Environment.GetEnvironmentVariable("CITIES_SKYLINES_II_PATH");
        if (!string.IsNullOrEmpty(envGame) && Directory.Exists(envGame)) return envGame;
        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            candidates.AddRange(new[] {
                @"C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II",
                @"C:\SteamLibrary\steamapps\common\Cities Skylines II",
                @"D:\SteamLibrary\steamapps\common\Cities Skylines II",
                @"G:\SteamLibrary\steamapps\common\Cities Skylines II" });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/root";
            candidates.Add(Path.Combine(home, ".steam", "steam", "steamapps", "common", "Cities Skylines II"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Cities Skylines II"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/Users";
            candidates.Add(Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "Cities Skylines II"));
        }
        foreach (var candidate in candidates.Distinct())
            if (Directory.Exists(candidate)) return candidate;
        throw new InvalidOperationException("could not locate Cities: Skylines II install path. Set CITIES_SKYLINES_II_PATH or pass --game-path");
    }

    private static string PluginsRoot(string gamePath) => Path.Combine(gamePath, "BepInEx", "plugins");

    private static string ResolveModDll(string source)
    {
        if (File.Exists(source) && source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return source;
        if (Directory.Exists(source))
        {
            var dll = Directory.EnumerateFiles(source, "*.dll", SearchOption.AllDirectories).FirstOrDefault(path =>
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith("ref_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) return false;
                return !name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("UnityEngine.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("UnityEngine.CoreModule.dll", StringComparison.OrdinalIgnoreCase);
            });
            if (dll is not null) return dll;
            throw new InvalidOperationException($"no suitable .dll found in {source}");
        }
        throw new InvalidOperationException($"source not found: {source}");
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("CivicSurvival Mod Installer (compiled .NET 9 exe).");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  civicsurvival-installer install    <dll-or-publishdir> [--game-path <path>]");
        Console.WriteLine("  civicsurvival-installer update     <dll-or-publishdir> [--game-path <path>]");
        Console.WriteLine("  civicsurvival-installer remove     [--game-path <path>]");
        Console.WriteLine("  civicsurvival-installer status     [--game-path <path>]");
        Console.WriteLine("  civicsurvival-installer launch     [--game-path <path>]");
        Console.WriteLine("  civicsurvival-installer version");
        Console.WriteLine();
        Console.WriteLine("Env: CITIES_SKYLINES_II_PATH can override auto-discovery.");
        Console.WriteLine("Auto-discovery: Steam install paths on Windows/Linux/macOS.");
    }
}
