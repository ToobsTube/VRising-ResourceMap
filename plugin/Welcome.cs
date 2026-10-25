using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;

namespace ResourceMap;

// Sends each player a private system message shortly after they join (e.g. pointing them at .rmap help).
// Joins are spotted by watching users' IsConnected flag, so no game code needs patching.
public static class Welcome
{
    // Wait a little after the player connects, so the message isn't buried under the game's own login messages.
    const float DelaySeconds = 10f;

    static readonly HashSet<ulong> Connected = new();
    static readonly Dictionary<ulong, (float sendAt, User user)> Pending = new();

    public static void Poll(float now)
    {
        var world = NodeDumper.ServerWorld();
        if (world == null) return;
        var em = world.EntityManager;

        var builder = new EntityQueryBuilder(Allocator.Temp).AddAll(ComponentType.ReadOnly(Il2CppType.Of<User>()));
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var entity in entities)
            {
                var user = em.GetComponentData<User>(entity);
                if (user.IsConnected)
                {
                    if (Connected.Add(user.PlatformId)) Pending[user.PlatformId] = (now + DelaySeconds, user);
                }
                else
                {
                    Connected.Remove(user.PlatformId);
                    Pending.Remove(user.PlatformId);
                }
            }
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }

        string text = Plugin.WelcomeMessage.Value.Trim();
        var due = new List<ulong>();
        foreach (var (id, p) in Pending)
            if (now >= p.sendAt) due.Add(id);
        foreach (var id in due)
        {
            var user = Pending[id].user;
            Pending.Remove(id);
            if (text.Length == 0 || !Plugin.CommandsAvailable) continue;
            FixedString512Bytes message = text;
            ServerChatUtils.SendSystemMessageToClient(em, user, ref message);
        }
    }
}
