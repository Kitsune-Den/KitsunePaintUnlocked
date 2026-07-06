using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

public class PaintUnlockedMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var harmony = new Harmony("com.adainthelab.paintunlocked");

        // === Early safety: ensure BlockTextureData.list can hold 10-bit paint IDs ===
        // On save reload, the game may access paint IDs 512+ before CreateBlockTextures
        // fires. Pre-size the list now if it already exists.
        if (BlockTextureData.list != null && BlockTextureData.list.Length < 1024)
        {
            var oldLen = BlockTextureData.list.Length;
            System.Array.Resize(ref BlockTextureData.list, 1024);
            Log.Out($"[PaintUnlocked] InitMod: pre-sized BlockTextureData.list {oldLen} -> 1024");
        }

        // === Layer 5: Widen ChunkBlockChannel storage from 48-bit to 64-bit ===
        // Must be patched BEFORE chunks are created. bytesPerVal=6 → 8.
        var cbcCtor = typeof(ChunkBlockChannel).GetConstructor(new[] { typeof(long), typeof(int) });
        if (cbcCtor != null)
        {
            harmony.Patch(cbcCtor, prefix: new HarmonyMethod(AccessTools.Method(typeof(ChunkStorageWidthPatch), "CtorPrefix")));
        }
        else Log.Warning("[PaintUnlocked] ChunkBlockChannel constructor not found!");

        // === Widen NetConnectionSimple's channel-0 send/receive buffers ===
        // The 48→64-bit chunk storage widening above makes every chunk-texture
        // network payload ~33% bigger, which can overrun vanilla's fixed 32KB
        // "not full" connection buffer during the spawn/decoration burst and
        // desync the client. See NetStreamBufferSizePatch for the full writeup.
        var initStreams = AccessTools.Method(typeof(NetConnectionSimple), "InitStreams", new[] { typeof(bool) });
        if (initStreams != null)
        {
            harmony.Patch(initStreams, prefix: new HarmonyMethod(AccessTools.Method(typeof(NetStreamBufferSizePatch), "Prefix")));
            Log.Out("[PaintUnlocked] NetConnectionSimple.InitStreams: channel-0 buffer widening enabled");
        }
        else Log.Warning("[PaintUnlocked] NetConnectionSimple.InitStreams not found — channel-0 buffer widening disabled");

        // === Fix NetPackageSignDataResponse.GetLength() always returning 0 ===
        // Genuine vanilla bug (unrelated to paint): the soft capacity check in
        // NetConnectionSimple.WriteToStream trusts GetLength() to decide whether a
        // package fits in the remaining buffer before writing it; since this
        // package always reports 0, it can slip through no matter how full the
        // buffer already is, and the real write() then overflows the fixed-size
        // stream. PaintUnlocked's large NetPackageDecoUpdate payloads (500KB+ per
        // prefab) make the buffer far more likely to already be near full when a
        // sign-data batch needs to go out too. See SignDataResponseLengthPatch.
        var signDataLength = AccessTools.Method(typeof(NetPackageSignDataResponse), "GetLength");
        if (signDataLength != null)
        {
            harmony.Patch(signDataLength, postfix: new HarmonyMethod(AccessTools.Method(typeof(SignDataResponseLengthPatch), "Postfix")));
            Log.Out("[PaintUnlocked] NetPackageSignDataResponse.GetLength() fix enabled");
        }
        else Log.Warning("[PaintUnlocked] NetPackageSignDataResponse.GetLength not found — length fix disabled");

        // === Legacy world migration: repack 8-bit chunks to 10-bit on load ===

        // Patch ChunkBlockChannel.Read: temporarily revert bytesPerVal 8→6 for legacy
        // disk reads so Read() consumes the correct number of bytes from width-6 streams.
        // Try PooledBinaryReader first (7DTD's custom subclass), fall back to BinaryReader
        var cbcRead = AccessTools.Method(typeof(ChunkBlockChannel), "Read",
            new[] { typeof(PooledBinaryReader), typeof(uint), typeof(bool) })
            ?? AccessTools.Method(typeof(ChunkBlockChannel), "Read",
            new[] { typeof(System.IO.BinaryReader), typeof(uint), typeof(bool) })
            ?? AccessTools.Method(typeof(ChunkBlockChannel), "Read");
        if (cbcRead != null)
        {
            harmony.Patch(cbcRead,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ChunkBlockChannelReadPatch), "ReadPrefix")));
            Log.Out("[PaintUnlocked] ChunkBlockChannel.Read prefix registered for legacy bytesPerVal override");
        }
        else Log.Warning("[PaintUnlocked] ChunkBlockChannel.Read not found — legacy migration will not work correctly");

        // Save guard: prefix snapshots paint counter, postfix re-marks chunk
        // dirty if any paint op happened during async save (race fix).
        var chunkSave = AccessTools.Method(typeof(Chunk), "save", new[] { typeof(PooledBinaryWriter) });
        if (chunkSave != null)
        {
            harmony.Patch(chunkSave,
                prefix:  new HarmonyMethod(AccessTools.Method(typeof(ChunkSaveGuardPatch), "SavePrefix")),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkSaveGuardPatch), "SavePostfix")));
            Log.Out("[PaintUnlocked] Chunk.save guard patches registered (race fix)");
        }
        else Log.Warning("[PaintUnlocked] Chunk.save not found — save guard disabled");

        // Postfix on Chunk.read: after all channels are deserialized, repack any
        // that have bytesPerVal==6 (legacy-read) from 8-bit to 10-bit face storage.
        var chunkRead = AccessTools.Method(typeof(Chunk), "read", new[] { typeof(PooledBinaryReader), typeof(uint), typeof(bool) });
        if (chunkRead != null)
        {
            harmony.Patch(chunkRead,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkMigrationPatch), "ReadPostfix")));
            Log.Out("[PaintUnlocked] Chunk.read postfix registered for legacy world migration");
        }
        else Log.Warning("[PaintUnlocked] Chunk.read not found — legacy world migration disabled");

        // Hook world load/save for sentinel file management
        var worldLoad = AccessTools.Method(typeof(World), "LoadWorld");
        if (worldLoad != null)
        {
            harmony.Patch(worldLoad, prefix: new HarmonyMethod(AccessTools.Method(typeof(WorldLifecyclePatch), "LoadWorldPrefix")));
        }
        else Log.Warning("[PaintUnlocked] World.LoadWorld not found — migration sentinel check disabled");

        var worldSave = AccessTools.Method(typeof(World), "Save");
        if (worldSave != null)
        {
            harmony.Patch(worldSave, postfix: new HarmonyMethod(AccessTools.Method(typeof(WorldLifecyclePatch), "SavePostfix")));
            // Force-flush: mark all chunks dirty before save runs so any paint
            // operations that slipped past the dirty-flag tracking still persist.
            harmony.Patch(worldSave, prefix: new HarmonyMethod(AccessTools.Method(typeof(ForceFlushPatch), "WorldSavePrefix")));
            Log.Out("[PaintUnlocked] World.Save force-flush prefix registered");
        }
        else Log.Warning("[PaintUnlocked] World.Save not found — migration sentinel write disabled");

        var worldUnload = AccessTools.Method(typeof(World), "UnloadWorld");
        if (worldUnload != null)
        {
            harmony.Patch(worldUnload, prefix: new HarmonyMethod(AccessTools.Method(typeof(WorldLifecyclePatch), "UnloadWorldPrefix")));
        }

        // Eager migration: postfix on RegionFileManager constructor to scan all
        // chunks on disk and migrate them up front, before normal chunk streaming
        // begins. This avoids the lazy-migration corruption where mid-session
        // save+reload of a migrated chunk would be re-read at the wrong width.
        var rfmCtor = typeof(RegionFileManager).GetConstructor(
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) });
        if (rfmCtor != null)
        {
            harmony.Patch(rfmCtor,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(EagerMigrator), "OnRegionFileManagerConstructed")));
            Log.Out("[PaintUnlocked] RegionFileManager ctor postfix registered for eager migration");
        }
        else Log.Warning("[PaintUnlocked] RegionFileManager(string, string, int, bool) ctor not found — eager migration disabled");

        // === Layer 4: Widen chunk face storage from 8-bit to 10-bit ===
        var setBlockFaceTex = AccessTools.Method(typeof(Chunk), "SetBlockFaceTexture");
        harmony.Patch(setBlockFaceTex, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "PatchSet")));
        // Also force chunk dirty flag on every paint operation (vanilla save-race fix).
        harmony.Patch(setBlockFaceTex, postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkPaintDirtyPatch), "SetBlockFaceTexturePostfix")));
        // Diagnostic: log every paint change for tracking "random shift" mysteries.
        harmony.Patch(setBlockFaceTex,
            prefix:  new HarmonyMethod(AccessTools.Method(typeof(PaintChangeTrackerPatch), "TrackPrefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(PaintChangeTrackerPatch), "TrackPostfix")));
        Log.Out("[PaintUnlocked] PaintChange tracker registered (diagnostic logging)");

        var getBlockFaceTex = AccessTools.Method(typeof(Chunk), "GetBlockFaceTexture");
        harmony.Patch(getBlockFaceTex, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "PatchGet")));

        // Force chunk dirty on full-face-array paint operations (covers SetTextureFull
        // and similar entry points that paint multiple faces at once).
        var setTextureFull = AccessTools.Method(typeof(Chunk), "SetTextureFull");
        if (setTextureFull != null)
        {
            harmony.Patch(setTextureFull, postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkPaintDirtyPatch), "SetTextureFullPostfix")));
            Log.Out("[PaintUnlocked] SetTextureFull dirty-flag postfix registered");
        }

        var v64ToIdx = typeof(Chunk).GetMethod("Value64FullToIndex", BindingFlags.Public | BindingFlags.Static);
        if (v64ToIdx != null)
        {
            harmony.Patch(v64ToIdx, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "PatchValue64ToIndex")));
            harmony.Patch(v64ToIdx, postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "ClampValue64Result")));
        }
        else Log.Warning("[PaintUnlocked] Value64FullToIndex not found!");

        // === Prefab texture re-encoding (8-bit → 10-bit) ===
        // GetSetTextureFullArray is called during POI prefab placement with 8-bit packed Int64s.
        // The 10-bit transpilers handle per-face storage, but GetSetTextureFullArray writes the
        // raw Int64 directly — the packed bit positions must be re-encoded from 8-bit to 10-bit.
        // NOTE: SetTextureFull is NOT patched — it unpacks face-by-face through SetBlockFaceTexture.
        var getSetTexFullArr = AccessTools.Method(typeof(Chunk), "GetSetTextureFullArray");
        if (getSetTexFullArr != null)
        {
            harmony.Patch(getSetTexFullArr, prefix: new HarmonyMethod(AccessTools.Method(typeof(TextureFullRepackPatch), "GetSetPrefix")));
            harmony.Patch(getSetTexFullArr, postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkPaintDirtyPatch), "GetSetTextureFullArrayPostfix")));
            Log.Out("[PaintUnlocked] GetSetTextureFullArray: re-encoding prefix + dirty-flag postfix enabled");
        }

        // === Layer 2: Paint ID allocation floor ===
        var getFreePaintID = AccessTools.Method(typeof(OpaqueTextures), "GetFreePaintID");
        var getFreePaintIDPrefix = AccessTools.Method(typeof(OcbPaintLimitPatch), "GetFreePaintIDPrefix");
        harmony.Patch(getFreePaintID, prefix: new HarmonyMethod(getFreePaintIDPrefix));

        // === Layer 1: Network packet encoding for indices > 255 ===
        var netPkgType = typeof(NetPackageSetBlockTexture);

        var setupMethod = AccessTools.Method(netPkgType, "Setup");
        harmony.Patch(setupMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(PaintIndexWidenerPatch), "SetupPostfix")));

        var writeMethod = AccessTools.Method(netPkgType, "write");
        harmony.Patch(writeMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(PaintIndexWidenerPatch), "WritePrefix")));

        var readMethod = AccessTools.Method(netPkgType, "read");
        harmony.Patch(readMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(PaintIndexWidenerPatch), "ReadPrefix")));

        var processMethod = AccessTools.Method(netPkgType, "ProcessPackage");
        harmony.Patch(processMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(PaintIndexWidenerPatch), "ProcessPackagePrefix")));

        // === UI protection ===
        var updateBg = AccessTools.Method(typeof(XUiC_ItemStack), "updateBackgroundTexture");
        harmony.Patch(updateBg, finalizer: new HarmonyMethod(AccessTools.Method(typeof(UpdateBackgroundTexturePatch), "Finalizer")));

        // === Fix paint ID byte truncation in SetSelectedTextureForItem ===
        // The game does conv.u1 (byte cast) on textureData.ID before storing in Meta,
        // truncating paint IDs above 255. Remove the conv.u1 to preserve full ID.
        var setSelTex = AccessTools.Method(typeof(XUiC_MaterialStack), "SetSelectedTextureForItem");
        if (setSelTex != null)
        {
            harmony.Patch(setSelTex, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MetaTruncationPatch), "Transpiler")));
        }
        else Log.Warning("[PaintUnlocked] SetSelectedTextureForItem not found!");

        // === Server-authoritative paint ID sync ===
        // After InitOpaqueConfig: build the server's ID mapping
        var initOpaqueConfig = AccessTools.Method(typeof(OpaqueTextures), "InitOpaqueConfig");
        if (initOpaqueConfig != null)
        {
            harmony.Patch(initOpaqueConfig, postfix: new HarmonyMethod(AccessTools.Method(typeof(PaintIdSyncManager), "OnInitOpaqueConfigDone")));
            Log.Out("[PaintUnlocked] InitOpaqueConfig postfix registered for paint ID mapping");
        }
        else Log.Warning("[PaintUnlocked] InitOpaqueConfig not found — paint ID sync disabled");

        // On client connect: send the mapping before chunks flow
        var requestToEnter = AccessTools.Method(typeof(NetPackageRequestToEnterGame), "ProcessPackage");
        if (requestToEnter != null)
        {
            harmony.Patch(requestToEnter, prefix: new HarmonyMethod(AccessTools.Method(typeof(PaintIdSyncManager), "OnRequestToEnterGamePrefix")));
            Log.Out("[PaintUnlocked] RequestToEnterGame prefix registered for paint ID sync");
        }
        else Log.Warning("[PaintUnlocked] NetPackageRequestToEnterGame.ProcessPackage not found — paint ID sync disabled");

        Log.Out("[PaintUnlocked] Loaded - paint limit raised to 1023 (10-bit chunk storage, 64-bit ChunkBlockChannel).");
        Log.Out("[PaintUnlocked] Legacy world migration enabled — pre-PaintUnlocked worlds will be repacked on first load.");
    }
}

public static class PaintIndexWidenerPatch
{
    private const byte OverflowFlag = 0x80;

    private static readonly Dictionary<int, ushort> _idxMap = new Dictionary<int, ushort>();
    private static readonly object _idxLock = new object();

    private static readonly BindingFlags _fieldFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly FieldInfo _fIdx =
        typeof(NetPackageSetBlockTexture).GetField("idx", _fieldFlags);
    private static readonly FieldInfo _fBlockPos =
        typeof(NetPackageSetBlockTexture).GetField("blockPos", _fieldFlags);
    private static readonly FieldInfo _fBlockFace =
        typeof(NetPackageSetBlockTexture).GetField("blockFace", _fieldFlags);
    private static readonly FieldInfo _fPlayerId =
        typeof(NetPackageSetBlockTexture).GetField("playerIdThatChanged", _fieldFlags);
    private static readonly FieldInfo _fChannel =
        typeof(NetPackageSetBlockTexture).GetField("channel", _fieldFlags);

    private static bool _reflectionValid = false;

    static PaintIndexWidenerPatch()
    {
        _reflectionValid = _fIdx != null && _fBlockPos != null && _fBlockFace != null
                        && _fPlayerId != null && _fChannel != null;
        if (!_reflectionValid)
            Log.Warning("[PaintUnlocked] Reflection check FAILED - network patches disabled");
        else
            Log.Out("[PaintUnlocked] Reflection check passed - all fields found.");
    }

    private static void StoreIdx(NetPackageSetBlockTexture instance, ushort value)
    {
        var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
        lock (_idxLock) { _idxMap[key] = value; }
        _fIdx?.SetValue(instance, (byte)(value & 0xFF));
    }

    private static ushort LoadIdx(NetPackageSetBlockTexture instance)
    {
        var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
        lock (_idxLock) { if (_idxMap.TryGetValue(key, out var v)) return v; }
        return _fIdx != null ? (byte)_fIdx.GetValue(instance) : (byte)0;
    }

    public static void SetupPostfix(NetPackageSetBlockTexture __instance, int _idx)
    {
        if (!_reflectionValid) return;
        StoreIdx(__instance, (ushort)_idx);
    }

    /// <summary>
    /// Field mutation prefix: for overflow indices, modify channel/idx fields on instance
    /// BEFORE vanilla write() runs. Vanilla serializes our values through PooledBinaryWriter.
    /// Does NOT skip original — PooledBinaryWriter internal state must be maintained.
    /// </summary>
    public static void WritePrefix(NetPackageSetBlockTexture __instance)
    {
        if (!_reflectionValid) return;

        var idx = LoadIdx(__instance);
        if (idx <= 255) return;

        byte channelWire = (byte)(OverflowFlag | ((idx >> 8) & 0x7F));
        byte idxLow = (byte)(idx & 0xFF);

        _fChannel.SetValue(__instance, channelWire);
        _fIdx.SetValue(__instance, idxLow);

        Log.Out($"[PaintUnlocked] WritePrefix: encoded overflow fullIdx={idx} -> channel=0x{channelWire:X2} idx=0x{idxLow:X2}");
    }

    /// <summary>
    /// Decodes overflow encoding in ProcessPackage (reliable on both client and server).
    /// ReadPrefix is unreliable on dedicated server due to virtual method dispatch.
    ///
    /// Also handles server-side re-broadcast: vanilla ProcessPackage forwards the
    /// packet to all other clients when running on the server. Because we return
    /// false (skip original), we must replicate that re-broadcast or other clients
    /// never receive the paint at all.
    /// </summary>
    public static bool ProcessPackagePrefix(NetPackageSetBlockTexture __instance, World _world)
    {
        if (!_reflectionValid) return true;

        try
        {
            var channel = (byte)_fChannel.GetValue(__instance);
            var blockPos  = (Vector3i)_fBlockPos.GetValue(__instance);
            var blockFace = (BlockFace)_fBlockFace.GetValue(__instance);
            var playerId  = (int)_fPlayerId.GetValue(__instance);

            ushort fullIdx;

            if ((channel & OverflowFlag) == 0)
            {
                fullIdx = LoadIdx(__instance);
                if (fullIdx <= 255) return true; // vanilla path, let original handle it
            }
            else
            {
                var idxByte = (byte)_fIdx.GetValue(__instance);
                fullIdx = (ushort)(((channel & 0x7F) << 8) | idxByte);
                Log.Out($"[PaintUnlocked] ProcessPackagePrefix: overflow decode -> fullIdx={fullIdx} at {blockPos} face={blockFace}");
            }

            // Apply locally on this peer (server or client)
            ApplyTexture(_world, blockPos, blockFace, fullIdx, playerId);

            // Server re-broadcast to other clients (replicates vanilla behavior we skipped)
            if (SingletonMonoBehaviour<ConnectionManager>.Instance != null
                && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                var package = NetPackageManager.GetPackage<NetPackageSetBlockTexture>()
                    .Setup(blockPos, blockFace, fullIdx, playerId, 0);
                // Our SetupPostfix has captured fullIdx; WritePrefix will encode overflow
                // into the channel byte during serialization for clients that need it.
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                    package, _onlyClientsAttachedToAnEntity: false, -1, playerId);
            }

            return false;
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] ProcessPackagePrefix failed: {ex.Message}");
            return true;
        }
    }

    private static void ApplyTexture(World _world, Vector3i blockPos, BlockFace blockFace, ushort idx, int playerId)
    {
        // Use GameManager.SetBlockTextureClient instead of calling chunk methods
        // directly. The vanilla entry point fires DynamicMeshManager.ChunkChanged
        // which is what triggers the visual mesh rebuild on the receiving client.
        // Without it, the paint data updates in memory but the mesh doesn't redraw,
        // so other players don't see the paint until they reload chunks.
        //
        // _idx is int in the vanilla signature so it carries our overflow value
        // cleanly through to ChunkCache.SetBlockFaceTexture (which we've already
        // patched for 10-bit storage via Chunk.SetBlockFaceTexture transpiler).
        //
        // _channel must be 0 ~ SetBlockTextureClient rejects channels >= 1.
        // Our overflow encoding uses the channel byte for transport, but here
        // on the receive side we pass the real channel (always 0 for paint ops).
        if (GameManager.Instance != null)
        {
            Log.Out($"[PaintUnlocked] ApplyTexture: SetBlockTextureClient pos={blockPos} face={blockFace} idx={idx}");
            GameManager.Instance.SetBlockTextureClient(blockPos, blockFace, idx, 0);
        }
        else
        {
            Log.Warning($"[PaintUnlocked] ApplyTexture: GameManager.Instance is null, paint not applied");
        }
    }

    public static bool ReadPrefix(NetPackageSetBlockTexture __instance, PooledBinaryReader _br)
    {
        return true; // passthrough — ProcessPackagePrefix handles decoding
    }
}
