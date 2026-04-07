using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// Server-authoritative paint ID sync.
///
/// Problem: Paint IDs are assigned locally by both server and client using a sequential
/// counter. If dictionary iteration order shifts across restarts, IDs diverge and clients
/// see wrong textures until they fully restart their game.
///
/// Solution: After InitOpaqueConfig completes, the server builds a mapping of texture
/// config name -> paint ID. When a client connects (RequestToEnterGame), the server sends
/// this mapping via a console command (pu_sync). The client reshuffles BlockTextureData.list
/// to match the server's IDs before any chunk data arrives.
/// </summary>
public static class PaintIdSyncManager
{
    private const int CustomIdFloor = 512;

    // Server's authoritative mapping: texture config ID (string) -> paint ID (ushort)
    private static Dictionary<string, ushort> _serverMapping;
    private static string _serverMappingBase64;
    private static bool _mappingReady = false;

    // Reflection into OpaqueTextures
    private static readonly FieldInfo _fOpaqueConfigs =
        typeof(OpaqueTextures).GetField("OpaqueConfigs",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

    // Reflection into NetPackageRequestToEnterGame to get the sender
    private static readonly FieldInfo _fSenderId =
        typeof(NetPackage).GetField("Sender",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    // ----------------------------------------------------------------
    // Server side: build and store the mapping after InitOpaqueConfig
    // ----------------------------------------------------------------

    /// <summary>
    /// Called as a postfix on OpaqueTextures.InitOpaqueConfig.
    /// Scans BlockTextureData.list for all custom entries (ID >= 512)
    /// and builds a name -> ID mapping.
    /// </summary>
    public static void OnInitOpaqueConfigDone()
    {
        try
        {
            _serverMapping = BuildMapping();
            _serverMappingBase64 = SerializeMapping(_serverMapping);
            _mappingReady = true;
            Log.Out($"[PaintUnlocked] Paint ID mapping built: {_serverMapping.Count} custom textures, {_serverMappingBase64.Length} bytes encoded");
        }
        catch (Exception ex)
        {
            Log.Error($"[PaintUnlocked] Failed to build paint ID mapping: {ex.Message}");
            _mappingReady = false;
        }
    }

    /// <summary>
    /// Scans BlockTextureData.list for entries with ID >= CustomIdFloor.
    /// Returns a dictionary of Name -> ID.
    /// </summary>
    private static Dictionary<string, ushort> BuildMapping()
    {
        var mapping = new Dictionary<string, ushort>();
        var list = BlockTextureData.list;
        if (list == null) return mapping;

        for (int i = CustomIdFloor; i < list.Length; i++)
        {
            var entry = list[i];
            if (entry == null) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;
            mapping[entry.Name] = (ushort)entry.ID;
        }

        return mapping;
    }

    // ----------------------------------------------------------------
    // Serialization: compact binary -> base64
    // Format: [ushort count] [per entry: ushort nameLen, UTF8 bytes, ushort paintId]
    // ----------------------------------------------------------------

    private static string SerializeMapping(Dictionary<string, ushort> mapping)
    {
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms, Encoding.UTF8))
        {
            bw.Write((ushort)mapping.Count);
            foreach (var kv in mapping)
            {
                var nameBytes = Encoding.UTF8.GetBytes(kv.Key);
                bw.Write((ushort)nameBytes.Length);
                bw.Write(nameBytes);
                bw.Write(kv.Value);
            }
            return Convert.ToBase64String(ms.ToArray());
        }
    }

    private static Dictionary<string, ushort> DeserializeMapping(string base64)
    {
        var mapping = new Dictionary<string, ushort>();
        var bytes = Convert.FromBase64String(base64);
        using (var ms = new MemoryStream(bytes))
        using (var br = new BinaryReader(ms, Encoding.UTF8))
        {
            int count = br.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                int nameLen = br.ReadUInt16();
                var nameBytes = br.ReadBytes(nameLen);
                string name = Encoding.UTF8.GetString(nameBytes);
                ushort paintId = br.ReadUInt16();
                mapping[name] = paintId;
            }
        }
        return mapping;
    }

    // ----------------------------------------------------------------
    // Server side: send mapping to connecting client
    // ----------------------------------------------------------------

    /// <summary>
    /// Prefix on NetPackageRequestToEnterGame.ProcessPackage (server-side).
    /// Sends the paint ID mapping to the connecting client before chunk data flows.
    /// </summary>
    public static void OnRequestToEnterGamePrefix(NetPackage __instance)
    {
        if (!_mappingReady || string.IsNullOrEmpty(_serverMappingBase64))
        {
            Log.Warning("[PaintUnlocked] Paint ID mapping not ready when client connected");
            return;
        }

        try
        {
            // Get the sender's ClientInfo from the package
            var sender = __instance.Sender;
            if (sender == null)
            {
                Log.Warning("[PaintUnlocked] RequestToEnterGame: no sender info");
                return;
            }

            // Find the ClientInfo for this sender
            var clientInfo = ConnectionManager.Instance?.Clients?.ForEntityId(sender.entityId);
            if (clientInfo == null)
            {
                Log.Warning($"[PaintUnlocked] RequestToEnterGame: no ClientInfo for entity {sender.entityId}");
                return;
            }

            SendMappingToClient(clientInfo);
        }
        catch (Exception ex)
        {
            Log.Error($"[PaintUnlocked] Failed to send paint mapping: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void SendMappingToClient(ClientInfo client)
    {
        // Send as a console command: pu_sync <base64data>
        // NetPackageConsoleCmdClient executes a console command on the client
        var cmd = $"pu_sync {_serverMappingBase64}";
        client.SendPackage(NetPackageManager.GetPackage<NetPackageConsoleCmdClient>().Setup(cmd, false));
        Log.Out($"[PaintUnlocked] Sent paint ID mapping to {client.playerName} ({_serverMapping.Count} textures)");
    }

    // ----------------------------------------------------------------
    // Client side: apply the server's mapping
    // ----------------------------------------------------------------

    /// <summary>
    /// Called by ConsoleCmdPaintSync when the client receives pu_sync.
    /// Reshuffles BlockTextureData.list to match the server's ID assignment.
    /// </summary>
    public static void ApplyServerMapping(string base64)
    {
        try
        {
            var serverMap = DeserializeMapping(base64);
            Log.Out($"[PaintUnlocked] Received server paint mapping: {serverMap.Count} textures");

            RemapBlockTextureData(serverMap);

            Log.Out($"[PaintUnlocked] Paint ID remap complete");
        }
        catch (Exception ex)
        {
            Log.Error($"[PaintUnlocked] Failed to apply server mapping: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Two-pass remap of BlockTextureData.list:
    /// Pass 1: Extract all custom entries (ID >= 512) into a name-keyed dictionary, null their slots
    /// Pass 2: Place each at the server's assigned ID
    /// </summary>
    private static void RemapBlockTextureData(Dictionary<string, ushort> serverMap)
    {
        var list = BlockTextureData.list;
        if (list == null)
        {
            Log.Error("[PaintUnlocked] BlockTextureData.list is null during remap");
            return;
        }

        // Find max server ID to ensure list is big enough
        ushort maxServerId = CustomIdFloor;
        foreach (var id in serverMap.Values)
        {
            if (id > maxServerId) maxServerId = id;
        }

        // Ensure list can hold all server IDs
        if (list.Length <= maxServerId)
        {
            var newLen = Math.Max(1024, ((maxServerId + 1) / 256 + 1) * 256);
            Array.Resize(ref BlockTextureData.list, newLen);
            list = BlockTextureData.list;
            Log.Out($"[PaintUnlocked] Resized list to {newLen} for server mapping");
        }

        // Pass 1: Extract all custom entries
        var extracted = new Dictionary<string, BlockTextureData>();
        for (int i = CustomIdFloor; i < list.Length; i++)
        {
            var entry = list[i];
            if (entry == null) continue;
            if (!string.IsNullOrEmpty(entry.Name) && !extracted.ContainsKey(entry.Name))
            {
                extracted[entry.Name] = entry;
            }
            list[i] = null; // clear the slot
        }

        Log.Out($"[PaintUnlocked] Extracted {extracted.Count} local custom textures for remapping");

        int remapped = 0;
        int placeholders = 0;

        // Pass 2: Place at server-assigned IDs
        foreach (var kv in serverMap)
        {
            string name = kv.Key;
            ushort serverId = kv.Value;

            if (extracted.TryGetValue(name, out var entry))
            {
                // We have this texture locally — move it to server's ID
                entry.ID = serverId;
                list[serverId] = entry;
                extracted.Remove(name);
                remapped++;
            }
            else
            {
                // Server has a texture we don't — create placeholder
                // Existing null-safety (ClampValue64Result, UpdateBackgroundTexturePatch)
                // handles rendering gracefully
                var placeholder = new BlockTextureData
                {
                    Name = name,
                    ID = serverId,
                    TextureID = 0, // falls back to default texture
                    LocalizedName = name,
                    Group = "",
                    Hidden = false,
                    SortIndex = 255,
                    PaintCost = 1
                };
                list[serverId] = placeholder;
                placeholders++;
            }
        }

        // Remaining extracted entries: client has textures server doesn't
        // Place them in unused slots above the server's max
        int nextSlot = maxServerId + 1;
        foreach (var kv in extracted)
        {
            if (nextSlot >= list.Length)
            {
                Array.Resize(ref BlockTextureData.list, nextSlot + 256);
                list = BlockTextureData.list;
            }
            kv.Value.ID = nextSlot;
            list[nextSlot] = kv.Value;
            nextSlot++;
        }

        // Reset the allocation counter so any further GetFreePaintID calls
        // continue from after the last assigned slot
        OcbPaintLimitPatch.ResetCounterTo(nextSlot);

        Log.Out($"[PaintUnlocked] Remap result: {remapped} matched, {placeholders} placeholders, {extracted.Count} client-only");
    }
}
