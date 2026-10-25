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
    public const string Version = "1.0.0";
    const string VcfGuid = "gg.deca.VampireCommandFramework";

    internal static Plugin Instance;
    internal static ManualLogSource Logger;
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
        InstallBundledConfig();

        DumpDelaySeconds = Config.Bind("Dump", "DelaySeconds", 60,
            "Seconds to wait after the world has loaded before the first dump, so spawned nodes have time to appear.");
        RepeatMinutes = Config.Bind("Dump", "RepeatMinutes", 0,
            "Dump again every N minutes (0 = only once per server start). Admins can also dump any time with .rmap dump.");
        IncludeDisabled = Config.Bind("Dump", "IncludeDisabled", true,
            "Also record nodes the game has switched off for now (for example in an inactive area). They are flagged in the output.");
        OutputFolder = Config.Bind("Output", "Folder", "",
            "Where dump files are written. Leave empty to use BepInEx/ResourceMap. A network share works, e.g. //nas/ResourceMap.");
        KeepDumps = Config.Bind("Output", "KeepDumps", 10,
            "How many timestamped dumps to keep in the folder; older ones are deleted after each dump. 0 = keep all. nodes-latest.json is always kept.");
        DiscordWebhookUrl = Config.Bind("Discord", "WebhookUrl", "",
            "Discord webhook link. When set, each dump's compressed copy (nodes-latest.json.gz) is posted to that channel. Keep it private: anyone with the link can post there. Leave empty to turn it off.");
        PostAutomaticDumps = Config.Bind("Discord", "PostAutomaticDumps", true,
            "Also post dumps made at server start and by RepeatMinutes. When false, only .rmap dump posts to Discord.");
        WelcomeMessage = Config.Bind("Commands", "WelcomeMessage", "Type .rmap help to see the resource map commands.",
            "Private message each player gets shortly after joining. Leave empty to turn it off. Only sent when the chat commands are available.");
        FindEnabled = Config.Bind("Commands", "FindEnabled", true,
            "Allow the .rmap find chat command (needs VampireCommandFramework).");
        FindAdminOnly = Config.Bind("Commands", "FindAdminOnly", true,
            "Only admins can use .rmap find. Set to false to let every player use it.");
        FindMaxRange = Config.Bind("Commands", "FindMaxRange", 300,
            "Furthest distance (in meters) .rmap find searches around the player. 0 = no limit.");
        FindCooldownSeconds = Config.Bind("Commands", "FindCooldownSeconds", 10,
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
            string target = Config.ConfigFilePath;
            string bundled = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", Guid + ".cfg");
            // A file without any "key = value" line is just an empty shell BepInEx may have created; treat it as missing.
            bool hasSettings = File.Exists(target) && File.ReadAllText(target).Contains('=');
            if (hasSettings || !File.Exists(bundled)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(bundled, target, true);
            Config.Reload();
            Log.LogInfo($"First start: copied the bundled config to {target}");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Couldn't copy the bundled config; using defaults instead. {e.Message}");
        }
    }

    // Kept in its own method so VCF's types are only loaded when VCF is actually installed.
    static void RegisterCommands() => Commands.Register();
}
