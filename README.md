# PaintUnlocked

**Removes the 255 paint texture hard limit in 7 Days to Die.**

The vanilla game serializes paint texture indices as a single byte in `NetPackageSetBlockTexture`, silently truncating any index above 255. `BlockTextureData.TextureID` is a `ushort` internally — the engine supports up to 65535 textures. The bottleneck was purely the network packet.

This mod patches `Setup`, `write`, and `read` on `NetPackageSetBlockTexture` to use a magic sentinel byte + ushort for indices above 254, while remaining fully backward compatible with vanilla for indices 0-254.

## Requirements

- 7 Days to Die V2.0+
- [OCBCustomTextures](https://www.nexusmods.com/7daystodie/mods/2788) (handles the actual texture atlas injection)
- EAC **disabled** on server and all clients

## Installation

Drop the `PaintUnlocked` folder into your server's `Mods/` directory.

**Install on BOTH the server and all connecting clients.** Unpatched clients can still connect and use paint slots 0-254, but slots 255+ will not work for them.

## How it works

`NetPackageSetBlockTexture` is the vanilla network packet that fires when a player applies paint to a block. It stored the texture index as a `byte` field (`idx`), meaning any value above 255 was silently cast away before hitting the wire.

This mod intercepts `Setup()` to capture the full index before truncation, then replaces `write()` and `read()` with an extended protocol:

- Indices **0-254**: single byte (vanilla format, zero overhead)
- Indices **255+**: `0xFF` magic sentinel + `ushort` (3 bytes total)

Discovered via reflection on `Assembly-CSharp.dll`. The engine itself was never the limit — just this one packet.

## Compatibility

- Works standalone
- Works alongside [KitsuneCommand](https://github.com/AdaInTheLab/KitsuneCommand) (which includes this patch as of v2.3.0)
- Compatible with KitsunePaint and any OCBCustomTextures-based paint pack

## Building from source

Copy game binaries from your 7D2D install's `7DaysToDie_Data/Managed/` into `7dtd-binaries/`:
- `Assembly-CSharp.dll`
- `Assembly-CSharp-firstpass.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- `0Harmony.dll`

Then:
```
dotnet build PaintUnlocked.csproj -c Release
```

## License

MIT
