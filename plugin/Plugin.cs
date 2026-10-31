using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace ResourceMap;

[BepInPlugin(Guid, Name, Version)]
[BepInProcess("VRisingServer.exe")]
[BepInDependency(VcfGuid, BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BasePlugin
{
    public const string Guid = "community.vrising.resourcemap";
    public const string Name = "ResourceMap";
    public const string Version = "1.0.2";
    const string VcfGuid = "gg.deca.VampireCommandFramework";
    // Named after the mod rather than BepInEx's default (the plugin GUID), so it's easy to find among other configs.
    const string ConfigFileName = "Prospector.cfg";

    internal static Plugin Instance;
    internal static ManualLogSource Logger;
    // Where ResourceMap keeps its config and dumps: normally this server's BepInEx folder (see ResolveDataRoot).
    internal static string DataRoot;
    internal static ConfigFile Settings;
    internal static ConfigEntry<int> DumpDelaySeconds;
    internal static ConfigEntry<int> RepeatMinutes;
    internal static ConfigEntry<bool> IncludeDisabled;
    internal static ConfigEntry<string> OutputFolder;
    internal static ConfigEntry<bool> FindEnabled;
    internal static ConfigEntry<bool> FindAdminOnly;
    internal static ConfigEntry<int> FindMaxRange;
    internal static ConfigEntry<int> FindCooldownSeconds;
    internal static ConfigEntry<string> WelcomeMessage;
    internal static ConfigEntry<int> KeepDumps;
    internal static ConfigEntry<string> DiscordWebhookUrl;
    internal static ConfigEntry<bool> PostAutomaticDumps;
    internal static bool CommandsAvailable;

    public override void Load()
    {
        Instance = this;
        Logger = Log;
        DataRoot = ResolveDataRoot();
        string configPath = Path.Combine(DataRoot, "config", ConfigFileName);
        MigrateOldConfig(configPath);
        Settings = new ConfigFile(configPath, false, MetadataHelper.GetMetadata(this));
        InstallBundledConfig();

        DumpDelaySeconds = Settings.Bind("Dump", "DelaySeconds", 60,
            "Seconds to wait after the world has loaded before the first dump, so spawned nodes have time to appear.");
        RepeatMinutes = Settings.Bind("Dump", "RepeatMinutes", 0,
            "Dump again every N minutes (0 = only once per server start). Admins can also dump any time with .rmap dump.");
        IncludeDisabled = Settings.Bind("Dump", "IncludeDisabled", true,
            "Also record nodes the game has switched off for now (for example in an inactive area). They are flagged in the output.");
        OutputFolder = Settings.Bind("Output", "Folder", "",
            "Where dump files are written. Leave empty to use BepInEx/ResourceMap. A network share works, e.g. //nas/ResourceMap.");
        KeepDumps = Settings.Bind("Output", "KeepDumps", 10,
            "How many timestamped dumps to keep in the folder; older ones are deleted after each dump. 0 = keep all. nodes-latest.json is always kept.");
        DiscordWebhookUrl = Settings.Bind("Discord", "WebhookUrl", "",
            "Discord webhook link. When set, each dump's compressed copy (nodes-latest.json.gz) is posted to that channel. Keep it private: anyone with the link can post there. Leave empty to turn it off.");
        PostAutomaticDumps = Settings.Bind("Discord", "PostAutomaticDumps", true,
            "Also post dumps made at server start and by RepeatMinutes. When false, only .rmap dump posts to Discord.");
        WelcomeMessage = Settings.Bind("Commands", "WelcomeMessage", "Type .rmap help to see the resource map commands.",
            "Private message each player gets shortly after joining. Leave empty to turn it off. Only sent when the chat commands are available.");
        FindEnabled = Settings.Bind("Commands", "FindEnabled", true,
            "Allow the .rmap find chat command (needs VampireCommandFramework).");
        FindAdminOnly = Settings.Bind("Commands", "FindAdminOnly", true,
            "Only admins can use .rmap find. Set to false to let every player use it.");
        FindMaxRange = Settings.Bind("Commands", "FindMaxRange", 300,
            "Furthest distance (in meters) .rmap find searches around the player. 0 = no limit.");
        FindCooldownSeconds = Settings.Bind("Commands", "FindCooldownSeconds", 10,
            "Seconds a player must wait between searches. Admins are exempt.");

        if (IL2CPPChainloader.Instance.Plugins.ContainsKey(VcfGuid))
        {
            RegisterCommands();
            CommandsAvailable = true;
            Log.LogInfo("Chat commands registered: .rmap help, .rmap find, .rmap mark, .rmap dump, .rmap reload");
        }
        else Log.LogInfo("VampireCommandFramework isn't installed, so chat commands are off.");

        AddComponent<DumpRunner>();
        Log.LogInfo($"{Name} {Version} loaded. First dump {DumpDelaySeconds.Value}s after the world is ready.");
    }

    // The release ships a ready-made config next to the DLL. On a first install it becomes the real config (so owners
    // can edit it before the first start); after that it's never copied again, so updates keep the owner's settings.
    void InstallBundledConfig()
    {
        try
        {
            string target = Settings.ConfigFilePath;
            // Shipped as ".cfg.template" so mod managers' config editors don't list it next to the real config.
            string bundled = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", ConfigFileName + ".template");
            // A file without any "key = value" line is just an empty shell BepInEx may have created; treat it as missing.
            bool hasSettings = File.Exists(target) && File.ReadAllText(target).Contains('=');
            if (hasSettings || !File.Exists(bundled)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(bundled, target, true);
            Settings.Reload();
            Log.LogInfo($"First start: copied the bundled config to {target}");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Couldn't copy the bundled config; using defaults instead. {e.Message}");
        }
    }

    // Versions before 1.0.1 used BepInEx's default name (<GUID>.cfg). Rename it once, so every setting carries over.
    void MigrateOldConfig(string configPath)
    {
        try
        {
            string old = Path.Combine(Path.GetDirectoryName(configPath)!, Guid + ".cfg");
            if (File.Exists(configPath) || !File.Exists(old)) return;
            File.Move(old, configPath);
            Log.LogInfo($"Renamed the config from {Path.GetFileName(old)} to {ConfigFileName}; your settings are kept.");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Couldn't rename the old config to {ConfigFileName}. {e.Message}");
        }
    }

    // Single player and host & play with ServerLaunchFix: the game's background server runs from the mod manager
    // profile with its own "BepInEx_Server" folder next to the profile's normal "BepInEx" folder. Keep the config and
    // dumps in the normal folder, where the mod manager's config editor and "Browse profile folder" lead players.
    static string ResolveDataRoot()
    {
        string root = Paths.BepInExRootPath;
        string parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string normal = parent == null ? null : Path.Combine(parent, "BepInEx");
        bool underServerLaunchFix = string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "BepInEx_Server", StringComparison.OrdinalIgnoreCase)
                                    && Directory.Exists(normal);
        return underServerLaunchFix ? normal : root;
    }

    // Kept in its own method so VCF's types are only loaded when VCF is actually installed.
    static void RegisterCommands() => Commands.Register();
}
