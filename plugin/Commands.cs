using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using VampireCommandFramework;

namespace ResourceMap;

// Chat commands, typed in game chat. Only registered when VampireCommandFramework is installed (see Plugin.Load);
// nothing outside this file touches VCF types, so the plugin still loads without it.
[CommandGroup("rmap")]
public class Commands
{
    static readonly Dictionary<int, string> Names = new();
    static readonly Dictionary<ulong, DateTime> LastFind = new();

    public static void Register() => CommandRegistry.RegisterAll();

    // Lists only what this player can actually use: our own config (find/mark can be admin-only), plus the same
    // permission checks VCF runs before every command, its admin check and any permission mod such as VRoles.
    [Command("help", description: "Show the ResourceMap commands you can use.")]
    public void Help(ChatCommandContext ctx)
    {
        bool canSearch = Plugin.FindEnabled.Value && (ctx.IsAdmin || !Plugin.FindAdminOnly.Value);
        var lines = new List<string>();
        string range = Plugin.FindMaxRange.Value > 0 ? $" (within {Plugin.FindMaxRange.Value}m)" : "";
        if (canSearch && Permitted(ctx, nameof(Find)))
            lines.Add($".rmap find <resource> - nearest resource around you{range}, e.g. .rmap find copper");
        if (canSearch && Permitted(ctx, nameof(Mark)))
            lines.Add(".rmap mark <resource> - same, and puts your map marker on it");
        if (Permitted(ctx, nameof(Dump)))
            lines.Add(".rmap dump - write a fresh resource dump now");
        if (Permitted(ctx, nameof(Reload)))
            lines.Add(".rmap reload - reload the ResourceMap config");
        ctx.Reply(lines.Count > 0
            ? "ResourceMap commands:\n" + string.Join("\n", lines)
            : "There are no ResourceMap commands you can use on this server.");
    }

    // Would VCF let this player run the command? Asks every registered middleware, exactly as VCF does before
    // executing it. If a middleware misbehaves, fall back to the command's own admin flag.
    static bool Permitted(ChatCommandContext ctx, string methodName)
    {
        var method = typeof(Commands).GetMethod(methodName);
        var command = method.GetCustomAttribute<CommandAttribute>();
        try
        {
            foreach (var middleware in CommandRegistry.Middlewares)
                if (!middleware.CanExecute(ctx, command, method)) return false;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Permission check for .rmap {command.Name} failed; using its admin setting. {e.Message}");
            return !command.AdminOnly || ctx.IsAdmin;
        }
    }

    [Command("dump", description: "Write a fresh resource dump now.", adminOnly: true)]
    public void Dump(ChatCommandContext ctx)
    {
        if (!NodeDumper.TryStart(ctx.User)) { ctx.Reply("A dump is already running. You'll get a message when it's done."); return; }
        ctx.Reply("Dump started. You'll get a message when it's written.");
    }

    [Command("reload", description: "Reload the ResourceMap config file.", adminOnly: true)]
    public void Reload(ChatCommandContext ctx)
    {
        Plugin.Settings.Reload();
        ctx.Reply("ResourceMap config reloaded.");
    }

    [Command("find", usage: "<resource> [range]", description: "Find the nearest resource around you, e.g. .rmap find copper")]
    public void Find(ChatCommandContext ctx, string resource, string word2 = "", string word3 = "", string word4 = "") =>
        Search(ctx, resource, word2, word3, word4, false);

    [Command("mark", usage: "<resource> [range]", description: "Put your map marker on the nearest resource, e.g. .rmap mark copper")]
    public void Mark(ChatCommandContext ctx, string resource, string word2 = "", string word3 = "", string word4 = "") =>
        Search(ctx, resource, word2, word3, word4, true);

    // The resource can be several words ("crude gem"), so the range is whichever word comes last, if it's a number.
    static void Search(ChatCommandContext ctx, string first, string word2, string word3, string word4, bool mark)
    {
        var words = new List<string> { first, word2, word3, word4 }.FindAll(w => !string.IsNullOrWhiteSpace(w));
        int range = 0;
        if (words.Count > 1 && int.TryParse(words[^1], out int r)) { range = r; words.RemoveAt(words.Count - 1); }
        string resource = string.Join(" ", words);

        if (!Plugin.FindEnabled.Value) { ctx.Reply("Resource finding is turned off on this server."); return; }
        if (Plugin.FindAdminOnly.Value && !ctx.IsAdmin) { ctx.Reply("Only admins can use .rmap find on this server."); return; }

        ulong who = ctx.User.PlatformId;
        if (!ctx.IsAdmin && LastFind.TryGetValue(who, out var last))
        {
            double wait = Plugin.FindCooldownSeconds.Value - (DateTime.UtcNow - last).TotalSeconds;
            if (wait > 0) { ctx.Reply($"Wait {Math.Ceiling(wait)}s before searching again."); return; }
        }
        LastFind[who] = DateTime.UtcNow;

        int maxRange = Plugin.FindMaxRange.Value;
        if (range <= 0 || (maxRange > 0 && range > maxRange)) range = maxRange > 0 ? maxRange : 500;

        var world = NodeDumper.ServerWorld();
        if (world == null) { ctx.Reply("The server world isn't ready yet."); return; }
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        float3 me = NodeDumper.PositionOf(em, ctx.Event.SenderCharacterEntity);
        var terms = resource.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();

        var matches = new List<(float dist, Entity entity)>();
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<EntityCategory>()))
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<PrefabGUID>()));
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var entity in entities)
            {
                if (em.GetComponentData<EntityCategory>(entity).MainCategory != MainEntityCategory.Resource) continue;
                float3 pos = NodeDumper.PositionOf(em, entity);
                float dist = math.distance(new float2(pos.x, pos.z), new float2(me.x, me.z));
                if (dist > range) continue;
                if (!Matches(em, prefabs, entity, terms)) continue;
                matches.Add((dist, entity));
            }
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }

        if (matches.Count == 0) { ctx.Reply($"No {resource} within {range}m."); return; }
        matches.Sort((a, b) => a.dist.CompareTo(b.dist));
        var (nearest, target) = matches[0];
        float3 at = NodeDumper.PositionOf(em, target);
        string more = matches.Count > 1 ? $" {matches.Count - 1} more within {range}m." : "";
        ctx.Reply($"Nearest {resource}: {Math.Round(nearest)}m {Direction(at.x - me.x, at.z - me.z)} (x {Math.Round(at.x)}, z {Math.Round(at.z)}).{more}");
        if (mark)
        {
            SetMapMarker(em, ctx.Event.SenderUserEntity, ctx.Event.SenderCharacterEntity, new float2(at.x, at.z));
            ctx.Reply("Map marker placed. Open the map to follow its guide path.");
        }
    }

    // Places the player's own map marker, exactly as if they had clicked the map: the game's SetMapMarkerSystem
    // picks up this event (and replaces any marker the player already had).
    static void SetMapMarker(EntityManager em, Entity user, Entity character, float2 position)
    {
        var e = em.CreateEntity(
            ComponentType.ReadWrite(Il2CppType.Of<FromCharacter>()),
            ComponentType.ReadWrite(Il2CppType.Of<SetMapMarkerEvent>()),
            ComponentType.ReadWrite(Il2CppType.Of<ReceiveNetworkEventTag>()));
        em.SetComponentData(e, new FromCharacter { User = user, Character = character });
        em.SetComponentData(e, new SetMapMarkerEvent { Position = position });
    }

    // A node matches when every search word is in its prefab name or the items it gives, in any order, so "crude gem"
    // finds TM_GemCrude. Only harvestable nodes count, so rubble and stumps left behind after mining don't show up.
    static bool Matches(EntityManager em, PrefabCollectionSystem prefabs, Entity entity, string[] terms)
    {
        var yields = NodeDumper.YieldsOf(em, entity, guid => NameOf(prefabs, guid));
        if (yields == null || yields.Count == 0) return false;
        string prefab = Normalize(NameOf(prefabs, em.GetComponentData<PrefabGUID>(entity)));
        if (terms.All(t => prefab.Contains(t))) return true;
        foreach (var y in yields)
        {
            string item = Normalize(y.Item);
            if (terms.All(t => item.Contains(t))) return true;
        }
        return false;
    }

    static string NameOf(PrefabCollectionSystem prefabs, PrefabGUID guid)
    {
        if (Names.TryGetValue(guid.GuidHash, out var n)) return n;
        if (!prefabs._PrefabLookupMap.TryGetName(guid, out n)) n = "";
        Names[guid.GuidHash] = n;
        return n;
    }

    // Lower case, no spaces or underscores, and British spellings folded in, so "Ghost crystal", "ghostcrystal" and
    // "sulphur" all match the game's names.
    static string Normalize(string s) =>
        s.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("sulph", "sulf");

    // Compass direction from you to the node; +z is north.
    static string Direction(float dx, float dz)
    {
        string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        double angle = (Math.Atan2(dx, dz) * 180 / Math.PI + 360) % 360;
        return names[(int)Math.Round(angle / 45) % 8];
    }
}
