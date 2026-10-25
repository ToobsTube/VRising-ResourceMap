using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;

namespace ResourceMap;

// Reads what a node's spot can turn into.
//
// Every resource node is a child of a spawn chain: a list of stages (e.g. "copper vein" -> "depleted" -> back) that the
// spot cycles through. Some chains are themselves picked by a randomized spawner, which rolls one of several weighted
// chains (e.g. copper 40% / iron 35% / stone 25%) each time it spawns. Both lists live in blob assets, which the
// interop can't wrap as typed structs, so they are read by offset. The offsets come from the interop metadata for the
// game version this was built against and are sanity-checked before use; if they don't fit, the reader turns itself
// off and the dump simply leaves this information out.
public unsafe class ChainReader
{
    // SpawnChainBlobAsset: BlobArray<ChainElement> ChainElements @0, int MainElementIndex @8
    // ChainElement (96 bytes): ElementName (FixedString32Bytes) @8, int ElementIndex @40, PrefabGUID PrefabType @44,
    //   bool UseAutoTransition @48, AutoChainTransitionConstants @52 (float TransitionAfterDuration @+40)
    const int ChainElementSize = 96;
    const int ElementNameOffset = 8;
    const int ElementIndexOffset = 40;
    const int ElementPrefabOffset = 44;
    const int UseAutoTransitionOffset = 48;
    const int AutoTransitionAfterOffset = 92;

    // RandomizedSpawnChainSettingsBlobAsset: BlobArray<Group> Groups @0, float TotalGroupWeight @8
    // Group (16 bytes): BlobArray<SpawnChain> Chains @0, float GroupWeight @8, float TotalChainWeight @12
    // SpawnChain (8 bytes): PrefabGUID Prefab @0, float Weight @4
    const int GroupSize = 16;
    const int RandomChainSize = 8;

    const int MaxListLength = 256;

    public readonly List<ChainDef> Chains = new();
    public readonly List<SpawnerDef> Spawners = new();
    public bool Broken { get; private set; }

    readonly EntityManager _em;
    readonly PrefabCollectionSystem _prefabs;
    readonly Func<PrefabGUID, string> _nameOf;
    readonly ComponentType _spawnChainChild = ComponentType.ReadOnly(Il2CppType.Of<SpawnChainChild>());
    readonly ComponentType _chainConstants = ComponentType.ReadOnly(Il2CppType.Of<SpawnChainData.SpawnChainConstants>());
    readonly ComponentType _randomizedChild = ComponentType.ReadOnly(Il2CppType.Of<RandomizedSpawnChainChild>());
    readonly ComponentType _randomized = ComponentType.ReadOnly(Il2CppType.Of<RandomizedSpawnChain>());
    readonly ComponentType _randomizedSettings = ComponentType.ReadOnly(Il2CppType.Of<RandomizedSpawnChainSettings>());
    readonly ComponentType _prefabGuid = ComponentType.ReadOnly(Il2CppType.Of<PrefabGUID>());

    readonly Dictionary<IntPtr, int> _chainByBlob = new();
    readonly Dictionary<int, int> _spawnerBySettings = new();
    readonly Dictionary<int, ChainInfo> _chainByEntity = new();

    public ChainReader(EntityManager em, PrefabCollectionSystem prefabs, Func<PrefabGUID, string> nameOf)
    {
        _em = em;
        _prefabs = prefabs;
        _nameOf = nameOf;
    }

    public struct ChainInfo
    {
        public int SpotId;       // the chain entity: nodes sharing it are the same spot
        public int ChainDef;     // index into Chains, -1 if unknown
        public int Element;      // which stage of the chain this node is
        public int SpawnerDef;   // index into Spawners, -1 if the chain isn't randomized
        public int SpawnerId;    // the randomizer entity, -1 if none
    }

    public ChainInfo? Read(Entity node)
    {
        if (Broken || !_em.HasComponent(node, _spawnChainChild)) return null;
        try
        {
            var child = _em.GetComponentData<SpawnChainChild>(node);
            var chain = child.SpawnChain;
            if (!_chainByEntity.TryGetValue(chain.Index, out var info))
            {
                info = new ChainInfo { SpotId = chain.Index, ChainDef = -1, SpawnerDef = -1, SpawnerId = -1 };
                if (_em.Exists(chain))
                {
                    info.ChainDef = ChainDefOf(chain);
                    if (_em.HasComponent(chain, _randomizedChild))
                    {
                        var parent = _em.GetComponentData<RandomizedSpawnChainChild>(chain).Parent;
                        if (_em.Exists(parent) && _em.HasComponent(parent, _randomized))
                        {
                            info.SpawnerId = parent.Index;
                            info.SpawnerDef = SpawnerDefOf(_em.GetComponentData<RandomizedSpawnChain>(parent).Settings);
                        }
                    }
                }
                _chainByEntity[chain.Index] = info;
            }
            info.Element = child.SpawnChainElementIndex;
            return info;
        }
        catch (Exception e)
        {
            TurnOff($"Couldn't read spawn chains: {e.Message}");
            return null;
        }
    }

    int ChainDefOf(Entity chainEntity)
    {
        byte* blob = BlobOf(chainEntity, _chainConstants);
        if (blob == null) return -1;
        if (_chainByBlob.TryGetValue((IntPtr)blob, out int id)) return id;

        var def = new ChainDef { Main = *(int*)(blob + 8) };
        if (_em.HasComponent(chainEntity, _prefabGuid)) def.Name = _nameOf(_em.GetComponentData<PrefabGUID>(chainEntity));

        byte* elements = BlobArray(blob, out int count);
        if (count <= 0 || count > MaxListLength || def.Main < 0 || def.Main >= count) { TurnOff($"Spawn chain list looks wrong (count {count}, main {def.Main})."); return -1; }
        for (int i = 0; i < count; i++)
        {
            byte* el = elements + i * ChainElementSize;
            if (*(int*)(el + ElementIndexOffset) != i) { TurnOff("Spawn chain element layout doesn't match this game version."); return -1; }
            var prefab = new PrefabGUID(*(int*)(el + ElementPrefabOffset));
            def.Elements.Add(new ChainElementDef
            {
                Name = FixedString32(el + ElementNameOffset),
                Prefab = prefab.GuidHash == 0 ? null : _nameOf(prefab),
                AfterSeconds = *(bool*)(el + UseAutoTransitionOffset) ? *(float*)(el + AutoTransitionAfterOffset) : 0f,
            });
        }

        id = Chains.Count;
        Chains.Add(def);
        _chainByBlob[(IntPtr)blob] = id;
        return id;
    }

    int SpawnerDefOf(PrefabGUID settingsGuid)
    {
        if (_spawnerBySettings.TryGetValue(settingsGuid.GuidHash, out int id)) return id;
        _spawnerBySettings[settingsGuid.GuidHash] = -1;

        if (!_prefabs._PrefabLookupMap.TryGetValueWithoutLogging(settingsGuid, out var settingsEntity)) return -1;
        byte* blob = BlobOf(settingsEntity, _randomizedSettings);
        if (blob == null) return -1;

        var def = new SpawnerDef { Name = _nameOf(settingsGuid) };
        float totalGroupWeight = *(float*)(blob + 8);
        byte* groups = BlobArray(blob, out int groupCount);
        if (groupCount < 0 || groupCount > MaxListLength) { TurnOff($"Randomized spawner list looks wrong (groups {groupCount})."); return -1; }
        for (int g = 0; g < groupCount; g++)
        {
            byte* group = groups + g * GroupSize;
            float groupShare = totalGroupWeight > 0 ? *(float*)(group + 8) / totalGroupWeight : 1f / groupCount;
            float totalChainWeight = *(float*)(group + 12);
            byte* chains = BlobArray(group, out int chainCount);
            if (chainCount < 0 || chainCount > MaxListLength) { TurnOff($"Randomized spawner list looks wrong (chains {chainCount})."); return -1; }
            for (int c = 0; c < chainCount; c++)
            {
                byte* option = chains + c * RandomChainSize;
                var chainGuid = new PrefabGUID(*(int*)option);
                float weight = *(float*)(option + 4);
                float chance = groupShare * (totalChainWeight > 0 ? weight / totalChainWeight : 1f / chainCount);
                if (float.IsNaN(chance) || chance < 0f || chance > 1.001f) { TurnOff("Randomized spawner weights don't make sense for this game version."); return -1; }

                int chainDef = -1;
                if (_prefabs._PrefabLookupMap.TryGetValueWithoutLogging(chainGuid, out var chainPrefab))
                    chainDef = ChainDefOf(chainPrefab);
                def.Options.Add(new SpawnerOptionDef { Chain = _nameOf(chainGuid), ChainDef = chainDef, Chance = chance });
            }
        }

        id = Spawners.Count;
        Spawners.Add(def);
        _spawnerBySettings[settingsGuid.GuidHash] = id;
        return id;
    }

    byte* BlobOf(Entity entity, ComponentType type)
    {
        if (!_em.HasComponent(entity, type)) return null;
        void* component = _em.GetComponentDataRawRO(entity, type.TypeIndex);
        // A BlobAssetReference is a single pointer to the blob data.
        return component == null ? null : *(byte**)component;
    }

    // A BlobArray is { int offset from this field to the first item; int length }.
    static byte* BlobArray(byte* field, out int length)
    {
        length = *(int*)(field + 4);
        return field + *(int*)field;
    }

    // FixedString32Bytes: ushort byte length, then up to 29 bytes of UTF-8.
    static string FixedString32(byte* p)
    {
        int len = *(ushort*)p;
        return len is > 0 and <= 29 ? Encoding.UTF8.GetString(p + 2, len) : "";
    }

    void TurnOff(string reason)
    {
        if (Broken) return;
        Broken = true;
        Plugin.Logger.LogWarning($"{reason} The dump will not include what spots can turn into.");
    }

    public class ChainDef
    {
        public string Name;
        public int Main;
        public List<ChainElementDef> Elements = new();
    }

    public class ChainElementDef
    {
        public string Name;
        public string Prefab;
        public float AfterSeconds;
    }

    public class SpawnerDef
    {
        public string Name;
        public List<SpawnerOptionDef> Options = new();
    }

    public class SpawnerOptionDef
    {
        public string Chain;
        public int ChainDef;
        public float Chance;
    }
}
