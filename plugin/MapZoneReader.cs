using System;
using System.Collections.Generic;
using System.Text.Json;
using Il2CppInterop.Runtime;
using ProjectM.Terrain;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ResourceMap;

// Reads how the game projects world positions onto its own map textures (the in-game map, e.g.
// ZoneMap_Wilderness_VRisingWorld). Written into the dump so the viewer can place nodes on that image exactly.
public static class MapZoneReader
{
    public class MapZone
    {
        public string MapType;
        public int2? StartChunk;
        public int2? ChunkSize;
        public float4x4 Projection;
    }

    public static List<MapZone> Read(EntityManager em)
    {
        var zones = new List<MapZone>();
        try
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .AddAll(ComponentType.ReadOnly(Il2CppType.Of<WorldZoneTextureProjectionMatrix>()))
                .WithOptions(EntityQueryOptions.IncludeDisabled);
            var query = em.CreateEntityQuery(ref builder);
            builder.Dispose();

            var zoneIdType = ComponentType.ReadOnly(Il2CppType.Of<WorldZoneId>());
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (var entity in entities)
                {
                    var zone = new MapZone { Projection = em.GetComponentData<WorldZoneTextureProjectionMatrix>(entity).ProjectionMatrix };
                    if (em.HasComponent(entity, zoneIdType))
                    {
                        var id = em.GetComponentData<WorldZoneId>(entity);
                        zone.MapType = id.MapType.ToString();
                        zone.StartChunk = new int2(id.ZoneStartChunk.X, id.ZoneStartChunk.Y);
                        zone.ChunkSize = id.ZoneChunkSize;
                    }
                    zones.Add(zone);
                }
            }
            finally
            {
                entities.Dispose();
                query.Dispose();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't read the map projection; the viewer will need manual alignment for the game map. {e.Message}");
        }
        if (zones.Count == 0) Plugin.Logger.LogInfo("No map projection found on the server.");
        return zones;
    }

    // "mapZones": [{ mapType, startChunk: [x, y], chunkSize: [x, y], projection: [c0, c1, c2, c3] }]
    // projection is the column-major float4x4: texture coords = c0 * x + c1 * y + c2 * z + c3.
    public static void Write(Utf8JsonWriter json, List<MapZone> zones)
    {
        json.WriteStartArray("mapZones");
        foreach (var z in zones)
        {
            json.WriteStartObject();
            if (z.MapType != null) json.WriteString("mapType", z.MapType);
            if (z.StartChunk is { } s) { json.WriteStartArray("startChunk"); json.WriteNumberValue(s.x); json.WriteNumberValue(s.y); json.WriteEndArray(); }
            if (z.ChunkSize is { } c) { json.WriteStartArray("chunkSize"); json.WriteNumberValue(c.x); json.WriteNumberValue(c.y); json.WriteEndArray(); }
            json.WriteStartArray("projection");
            foreach (var col in new[] { z.Projection.c0, z.Projection.c1, z.Projection.c2, z.Projection.c3 })
            {
                json.WriteStartArray();
                json.WriteNumberValue(col.x); json.WriteNumberValue(col.y); json.WriteNumberValue(col.z); json.WriteNumberValue(col.w);
                json.WriteEndArray();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
