using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace ResourceMap;

// Reads every resource entity (trees, rocks, ore veins, plants, ...) from the server world and writes them to JSON.
public static class NodeDumper
{
    // Some component types are buffers in one game version and plain components in another. If reading one fails,
    // stop trying for the rest of the session instead of failing every node.
    static bool _yieldsBroken;
    static bool _spawnRegionsBroken;

    internal static World ServerWorld()
    {
        foreach (var world in World.s_AllWorlds)
            if (world.Name == "Server") return world;
        return null;
    }

    public static bool IsWorldReady()
    {
        var world = ServerWorld();
        if (world == null || !world.IsCreated) return false;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        return prefabs != null && prefabs.GameDataInitialized;
    }

    static DumpJob _current;

    public static bool Busy => _current != null;

    // Starts a dump, or joins the one already running. Returns false if one was already running. Every requester
    // (each admin who asked) gets a private chat message when it's done.
    public static bool TryStart(User? requester)
    {
        bool started = _current == null;
        _current ??= new DumpJob();
        if (requester is { } user) _current.AddRequester(user);
        return started;
    }

    // Called every server frame. Advances the running dump, if any.
    public static void Tick()
    {
        if (_current != null && _current.Step()) _current = null;
    }

    // A dump in progress. Nodes can only be read on the main thread, so they're read a few milliseconds per frame to
    // keep the server responsive; the file is then written on a background thread, since that doesn't touch the game.
    sealed class DumpJob
    {
        const double MillisecondsPerFrame = 5;

        readonly List<User> _requesters = new();
        readonly EntityManager _em;
        readonly PrefabCollectionSystem _prefabs;
        readonly Dictionary<int, string> _names = new();
        readonly HashSet<Entity> _regionMembers;
        readonly ChainReader _chains;
        readonly double? _serverTime;
        readonly ComponentType _autoChainType = ComponentType.ReadOnly(Il2CppType.Of<AutoChainInstanceData>());
        readonly ComponentType _disabledType = ComponentType.ReadOnly(Il2CppType.Of<Disabled>());
        readonly List<NodeRecord> _nodes = new();
        readonly List<MapZoneReader.MapZone> _mapZones;
        readonly string _gameVersion = UnityEngine.Application.version;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        EntityQuery _query;
        NativeArray<Entity> _entities;
        int _next;
        Task<string> _write;

        public void AddRequester(User user)
        {
            if (!_requesters.Exists(u => u.PlatformId == user.PlatformId)) _requesters.Add(user);
        }

        public DumpJob()
        {
            var world = ServerWorld() ?? throw new InvalidOperationException("Server world not found.");
            _em = world.EntityManager;
            _prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            _regionMembers = CollectSpawnRegionMembers(_em);
            _chains = new ChainReader(_em, _prefabs, NameOf);
            _serverTime = ServerTimeOf(_em);
            _mapZones = MapZoneReader.Read(_em);

            var builder = new EntityQueryBuilder(Allocator.Temp)
                .AddAll(ComponentType.ReadOnly(Il2CppType.Of<EntityCategory>()))
                .AddAll(ComponentType.ReadOnly(Il2CppType.Of<PrefabGUID>()));
            if (Plugin.IncludeDisabled.Value) builder = builder.WithOptions(EntityQueryOptions.IncludeDisabled);
            _query = _em.CreateEntityQuery(ref builder);
            builder.Dispose();
            _entities = _query.ToEntityArray(Allocator.Persistent);
        }

        string NameOf(PrefabGUID guid)
        {
            if (_names.TryGetValue(guid.GuidHash, out var n)) return n;
            if (!_prefabs._PrefabLookupMap.TryGetName(guid, out n)) n = guid.GuidHash.ToString();
            _names[guid.GuidHash] = n;
            return n;
        }

        // Returns true once the dump has finished (or failed).
        public bool Step()
        {
            try
            {
                if (_write == null)
                {
                    ReadSlice();
                    if (_next < _entities.Length) return false;
                    ReleaseEntities();
                    var nodes = _nodes; var chains = _chains.Broken ? null : _chains; var zones = _mapZones; var version = _gameVersion;
                    bool manual = _requesters.Count > 0;
                    _write = Task.Run(() => Write(nodes, chains, zones, version, manual));
                    return false;
                }
                if (!_write.IsCompleted) return false;

                string message = _write.IsFaulted
                    ? "Dump failed. The server log has the details."
                    : $"{_write.Result} ({_clock.Elapsed.TotalSeconds:0.0}s)";
                if (_write.IsFaulted) Plugin.Logger.LogError($"Resource dump failed: {_write.Exception?.GetBaseException()}");
                else Plugin.Logger.LogInfo(message);
                Notify(message);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Resource dump failed: {e}");
                ReleaseEntities();
                Notify("Dump failed. The server log has the details.");
                return true;
            }
        }

        void ReadSlice()
        {
            var slice = Stopwatch.StartNew();
            while (_next < _entities.Length && slice.Elapsed.TotalMilliseconds < MillisecondsPerFrame)
            {
                var entity = _entities[_next++];
                // Entities can come and go between frames while the dump is spread out.
                if (!_em.Exists(entity)) continue;
                if (_em.GetComponentData<EntityCategory>(entity).MainCategory != MainEntityCategory.Resource) continue;

                var guid = _em.GetComponentData<PrefabGUID>(entity);
                var chain = _chains.Read(entity);
                _nodes.Add(new NodeRecord
                {
                    Prefab = NameOf(guid),
                    Guid = guid.GuidHash,
                    Position = PositionOf(_em, entity),
                    Disabled = _em.HasComponent(entity, _disabledType),
                    Source = SourceOf(entity, chain, _regionMembers),
                    Chain = chain,
                    Yields = YieldsOf(_em, entity, NameOf),
                    ChangeIn = _serverTime is { } now ? NextChangeOf(_em, entity, _autoChainType) - now : null,
                });
            }
        }

        void ReleaseEntities()
        {
            if (_entities.IsCreated) _entities.Dispose();
            _query.Dispose();
        }

        void Notify(string message)
        {
            foreach (var user in _requesters)
            {
                FixedString512Bytes text = message.Length > 500 ? message.Substring(0, 500) : message;
                ServerChatUtils.SendSystemMessageToClient(_em, user, ref text);
            }
        }
    }

    // When the node's spot next changes on its own (server time). The timer sits on the node, or on its spawn chain.
    // Stages that don't change on a timer (e.g. a full-grown tree) leave it at 0, which means "no due time".
    static double? NextChangeOf(EntityManager em, Entity node, ComponentType autoChainType)
    {
        double next;
        if (em.HasComponent(node, autoChainType)) next = em.GetComponentData<AutoChainInstanceData>(node).NextTransitionAttempt;
        else
        {
            if (!em.HasComponent(node, ComponentType.ReadOnly(Il2CppType.Of<SpawnChainChild>()))) return null;
            var chain = em.GetComponentData<SpawnChainChild>(node).SpawnChain;
            if (!em.Exists(chain) || !em.HasComponent(chain, autoChainType)) return null;
            next = em.GetComponentData<AutoChainInstanceData>(chain).NextTransitionAttempt;
        }
        return next > 0 ? next : null;
    }

    // The server clock that spawn chain timers count against, or null if it can't be read.
    static double? ServerTimeOf(EntityManager em)
    {
        try
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).AddAll(ComponentType.ReadOnly(Il2CppType.Of<ServerTime>()));
            var query = em.CreateEntityQuery(ref builder);
            builder.Dispose();
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                return entities.Length > 0 ? em.GetComponentData<ServerTime>(entities[0]).Time : null;
            }
            finally
            {
                entities.Dispose();
                query.Dispose();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't read the server clock; the dump won't say when nodes are due to change. {e.Message}");
            return null;
        }
    }

    internal static float3 PositionOf(EntityManager em, Entity entity)
    {
        if (em.HasComponent(entity, ComponentType.ReadOnly(Il2CppType.Of<LocalToWorld>())))
            return em.GetComponentData<LocalToWorld>(entity).Position;
        if (em.HasComponent(entity, ComponentType.ReadOnly(Il2CppType.Of<Translation>())))
            return em.GetComponentData<Translation>(entity).Value;
        return float3.zero;
    }

    // How the game placed this node. "fixed" nodes are baked into the map. Spawn chain nodes cycle through set stages
    // in one spot; randomized ones roll a different chain (possibly a different resource) each time; spawn region ones
    // can appear at a different spot.
    static string SourceOf(Entity entity, ChainReader.ChainInfo? chain, HashSet<Entity> regionMembers)
    {
        if (chain is { SpawnerDef: >= 0 }) return "randomizedSpawnChain";
        if (regionMembers.Contains(entity)) return "spawnRegion";
        if (chain != null) return "spawnChain";
        return "fixed";
    }

    static HashSet<Entity> CollectSpawnRegionMembers(EntityManager em)
    {
        var members = new HashSet<Entity>();
        if (_spawnRegionsBroken) return members;

        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<SpawnRegion>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();

        var regions = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var region in regions)
            {
                if (!em.HasBuffer<SpawnRegionSpawnSlotEntry>(region)) continue;
                foreach (var slot in em.GetBuffer<SpawnRegionSpawnSlotEntry>(region, true))
                    if (slot.HasSpawned && slot.Entity != Entity.Null) members.Add(slot.Entity);
            }
        }
        catch (Exception e)
        {
            _spawnRegionsBroken = true;
            Plugin.Logger.LogWarning($"Couldn't read spawn regions; nodes from them will show as \"fixed\". {e.Message}");
        }
        finally
        {
            regions.Dispose();
            query.Dispose();
        }
        return members;
    }

    internal static List<YieldRecord> YieldsOf(EntityManager em, Entity entity, Func<PrefabGUID, string> nameOf)
    {
        if (_yieldsBroken) return null;
        try
        {
            if (!em.HasBuffer<YieldResourcesOnDamageTaken>(entity)) return null;
            var yields = new List<YieldRecord>();
            foreach (var y in em.GetBuffer<YieldResourcesOnDamageTaken>(entity, true))
                yields.Add(new YieldRecord { Item = nameOf(y.ItemType), Amount = y.Amount });
            return yields;
        }
        catch (Exception e)
        {
            _yieldsBroken = true;
            Plugin.Logger.LogWarning($"Couldn't read node yields; dumps will not list items. {e.Message}");
            return null;
        }
    }

    static string OutputFolder()
    {
        string folder = Plugin.OutputFolder.Value.Trim();
        if (folder.Length == 0) return Path.Combine(Paths.BepInExRootPath, "ResourceMap");
        // BepInEx reads "\\" in a config value as one backslash, so a network path typed normally
        // (two leading backslashes) arrives with only one, which Windows would treat as a folder on the current drive.
        if (folder.StartsWith("\\") && !folder.StartsWith("\\\\")) folder = "\\" + folder;
        return folder;
    }

    // Keeps only the newest KeepDumps timestamped dumps. Their names sort by time, so this also works across restarts.
    static void PruneOldDumps(string folder)
    {
        int keep = Plugin.KeepDumps.Value;
        if (keep <= 0) return;
        var dumps = Directory.GetFiles(folder, "nodes-????????-??????.json");
        Array.Sort(dumps, StringComparer.Ordinal);
        for (int i = 0; i < dumps.Length - keep; i++)
        {
            try { File.Delete(dumps[i]); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Couldn't delete old dump {dumps[i]}: {e.Message}"); }
        }
    }

    static string Write(List<NodeRecord> nodes, ChainReader chains, List<MapZoneReader.MapZone> mapZones, string gameVersion, bool manual)
    {
        string folder = OutputFolder();
        Directory.CreateDirectory(folder);

        var now = DateTime.Now;
        string stamped = Path.Combine(folder, $"nodes-{now:yyyyMMdd-HHmmss}.json");
        // Write under a temporary name and rename when done, so anyone reading the folder (the viewer, a NAS sync)
        // never picks up a half-written dump.
        string temp = stamped + ".tmp";

        using (var stream = File.Create(temp))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            json.WriteStartObject();
            json.WriteString("plugin", $"{Plugin.Name} {Plugin.Version}");
            json.WriteString("gameVersion", gameVersion);
            json.WriteString("dumpedAt", now.ToString("o"));
            json.WriteNumber("count", nodes.Count);
            json.WriteStartArray("nodes");
            foreach (var n in nodes)
            {
                json.WriteStartObject();
                json.WriteString("prefab", n.Prefab);
                json.WriteNumber("guid", n.Guid);
                json.WriteNumber("x", MathF.Round(n.Position.x, 2));
                json.WriteNumber("y", MathF.Round(n.Position.y, 2));
                json.WriteNumber("z", MathF.Round(n.Position.z, 2));
                json.WriteString("source", n.Source);
                if (chains != null && n.Chain is { } c)
                {
                    json.WriteNumber("spot", c.SpotId);
                    if (c.ChainDef >= 0) json.WriteNumber("chain", c.ChainDef);
                    json.WriteNumber("stage", c.Element);
                    if (c.SpawnerDef >= 0) json.WriteNumber("spawner", c.SpawnerDef);
                    if (c.SpawnerId >= 0) json.WriteNumber("spawnerSpot", c.SpawnerId);
                }
                if (n.Disabled) json.WriteBoolean("disabled", true);
                // Seconds until the spot's next automatic change; negative = overdue. Areas with no players nearby
                // are paused, so overdue spots catch up (and may re-roll) as soon as someone comes close.
                if (n.ChangeIn is { } changeIn) json.WriteNumber("changeIn", Math.Round(changeIn));
                if (n.Yields is { Count: > 0 })
                {
                    json.WriteStartArray("yields");
                    foreach (var y in n.Yields)
                    {
                        json.WriteStartObject();
                        json.WriteString("item", y.Item);
                        json.WriteNumber("amount", y.Amount);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            if (chains != null) WriteChains(json, chains);
            if (mapZones.Count > 0) MapZoneReader.Write(json, mapZones);
            json.WriteEndObject();
        }

        File.Move(temp, stamped);
        string latestTemp = Path.Combine(folder, "nodes-latest.json.tmp");
        File.Copy(stamped, latestTemp, true);
        File.Move(latestTemp, Path.Combine(folder, "nodes-latest.json"), true);

        PruneOldDumps(folder);

        // A compressed copy (about 1/15 the size) for sharing; the viewer opens it directly.
        string gz = Path.Combine(folder, "nodes-latest.json.gz");
        string gzTemp = gz + ".tmp";
        using (var input = File.OpenRead(stamped))
        using (var output = File.Create(gzTemp))
        using (var zip = new GZipStream(output, CompressionLevel.Optimal))
            input.CopyTo(zip);
        File.Move(gzTemp, gz, true);

        string summary = $"Wrote {nodes.Count} resource nodes to {stamped}";
        if (manual || Plugin.PostAutomaticDumps.Value)
        {
            string caption = $"Resource dump: {nodes.Count:N0} nodes · game {gameVersion} · {now:yyyy-MM-dd HH:mm}";
            string posted = DiscordPoster.Post(gz, Path.GetFileName(stamped) + ".gz", caption);
            if (posted != null) summary += " " + posted;
        }
        return summary;
    }

    // "chains": what each spawn chain cycles through. "spawners": the weighted chains a randomized spawner picks from.
    static void WriteChains(Utf8JsonWriter json, ChainReader chains)
    {
        json.WriteStartArray("chains");
        foreach (var c in chains.Chains)
        {
            json.WriteStartObject();
            if (c.Name != null) json.WriteString("name", c.Name);
            json.WriteNumber("main", c.Main);
            json.WriteStartArray("stages");
            foreach (var e in c.Elements)
            {
                json.WriteStartObject();
                json.WriteString("name", e.Name);
                if (e.Prefab != null) json.WriteString("prefab", e.Prefab);
                if (e.AfterSeconds > 0) json.WriteNumber("afterSeconds", MathF.Round(e.AfterSeconds, 1));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("spawners");
        foreach (var s in chains.Spawners)
        {
            json.WriteStartObject();
            json.WriteString("name", s.Name);
            json.WriteStartArray("options");
            foreach (var o in s.Options)
            {
                json.WriteStartObject();
                json.WriteString("chainName", o.Chain);
                if (o.ChainDef >= 0) json.WriteNumber("chain", o.ChainDef);
                json.WriteNumber("chance", MathF.Round(o.Chance, 4));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    class NodeRecord
    {
        public string Prefab;
        public int Guid;
        public float3 Position;
        public bool Disabled;
        public string Source;
        public ChainReader.ChainInfo? Chain;
        public double? ChangeIn;
        public List<YieldRecord> Yields;
    }

    internal class YieldRecord
    {
        public string Item;
        public int Amount;
    }
}
