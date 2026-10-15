using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

/// <summary>
/// Game members whose signatures differ between 7DTD V2.6 and V3.x, bound by reflection
/// so one PaintUnlocked.dll (compiled against the V3.1 references) also runs on V2.6.
/// A direct call would compile to the V3 signature and throw MissingMethodException
/// the first time it ran on V2.6.
///
///   GameManager.SetBlockTextureClient  first arg: V2.6 Vector3i, V3.x BlockValueRef
///   World.GetChunkSync(long)            declared on World in V2.6, WorldBase in V3.x
///   Localization.Get                    V2.6 (key, bool) / V3.x (key, bool, string)
/// </summary>
public static class GameCompat
{
    private static readonly MethodInfo _setBlockTextureClient;
    private static readonly MethodInfo _toBlockValueRef;
    private static readonly Func<World, long, IChunk> _getChunkSync;
    private static readonly MethodInfo _localizationGet;
    private static readonly object[] _localizationDefaults;

    static GameCompat()
    {
        _setBlockTextureClient = AccessTools.Method(typeof(GameManager), "SetBlockTextureClient");
        var posType = _setBlockTextureClient?.GetParameters()[0].ParameterType;
        if (posType != null && posType != typeof(Vector3i))
            _toBlockValueRef = posType.GetMethod("op_Implicit", new[] { typeof(Vector3i) });

        var getChunk = AccessTools.Method(typeof(World), "GetChunkSync", new[] { typeof(long) });
        if (getChunk != null)
            _getChunkSync = (Func<World, long, IChunk>)Delegate.CreateDelegate(typeof(Func<World, long, IChunk>), getChunk);

        _localizationGet = typeof(Localization).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Get")
            .Select(m => new { m, p = m.GetParameters() })
            .Where(x => x.p.Length >= 1 && x.p[0].ParameterType == typeof(string)
                        && (x.p.Length == 1 || x.p[1].ParameterType == typeof(bool))
                        && x.p.Skip(1).All(p => p.HasDefaultValue))
            .Select(x => x.m)
            .FirstOrDefault();
        if (_localizationGet != null)
            _localizationDefaults = _localizationGet.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();

        if (_setBlockTextureClient == null || (posType != typeof(Vector3i) && _toBlockValueRef == null))
            Log.Warning("[PaintUnlocked] GameManager.SetBlockTextureClient not bindable - remote paints above 255 will not be applied");
        if (_getChunkSync == null)
            Log.Warning("[PaintUnlocked] World.GetChunkSync(long) not found - save force-flush disabled");
    }

    public static void SetBlockTextureClient(Vector3i pos, BlockFace face, int idx, byte channel)
    {
        if (_setBlockTextureClient == null) return;
        object first = pos;
        if (_toBlockValueRef != null) first = _toBlockValueRef.Invoke(null, new object[] { pos });
        else if (_setBlockTextureClient.GetParameters()[0].ParameterType != typeof(Vector3i)) return;
        _setBlockTextureClient.Invoke(GameManager.Instance, new object[] { first, face, idx, channel });
    }

    public static IChunk GetChunkSync(World world, long key)
    {
        return _getChunkSync?.Invoke(world, key);
    }

    /// <summary>Localization.Get(key) with the game's own defaults; the key itself if unavailable.</summary>
    public static string Localize(string key)
    {
        if (_localizationGet == null) return key;
        var args = (object[])_localizationDefaults.Clone();
        args[0] = key;
        try { return (string)_localizationGet.Invoke(null, args); }
        catch (Exception) { return key; }
    }
}
