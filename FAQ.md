# PaintUnlocked FAQ

## "I installed PaintUnlocked on an existing world and my custom paints are gone"

This is expected behaviour, not a bug. Here's what's going on.

When you install a paint pack like PyroPaints or CK Textures *without* PaintUnlocked, the game registers those custom paints at IDs 154-255 (the slots above vanilla's ~154 paints). When PaintUnlocked is enabled, custom paints register at ID 512 and up instead, because the GPU atlas needs that gap to handle the wider paint range.

The migration system updates how chunk data is stored on disk, but it can't follow paints to their new IDs — a block that used to say "paint ID 200" still says 200, but in the new world that slot doesn't point to your custom paint anymore. So those blocks render as unpainted.

**Vanilla paints are unaffected** — those IDs (0-153) stay stable.

**Fix:** Repaint the affected blocks. Your existing custom paint packs all still work, they're just at different IDs now, so picking them again from the paint menu and applying them gets you back where you were.

This only happens once, when first installing PaintUnlocked. After that, the IDs are stable for that world.

---

## "painting.xml Index was outside the bounds of the array"

You're using the wrong OcbCustomTextures.

PaintUnlocked ships with a special fork of OcbCustomTextures that handles the larger paint ID range. If you downloaded OCB separately from NexusMods, it doesn't have the changes needed and will crash when it tries to register custom paints above ID 255.

**Fix:** Delete your OcbCustomTextures folder entirely. Extract both folders from the PaintUnlocked zip - `0_PaintUnlocked` AND `OcbCustomTextures`. They're a matched pair. Both must come from the same zip.

---

## "Paint icons are black/blank"

Two possible causes:

1. **OcbCustomTextures isn't installed at all.** The paint IDs get registered but there's nothing loading the actual texture images into the GPU. You need OCB.

2. **Your paint pack's .unity3d bundles are missing or broken.** The painting.xml references Atlas_001.unity3d (etc.) but those files either don't exist in the Resources folder or failed to build. If you made the pack with KitsunePaint, re-download it from [paint.kitsuneden.net](https://paint.kitsuneden.net) - the bundle builder was updated recently.

---

## "Paint menu is empty after reloading my save"

If you're on v1.0.4 or earlier, update to v1.0.5. There was a bug where the paint ID counter didn't reset properly when you quit to menu and reloaded on a listen server (P2P hosted game). The IDs would drift and your custom paints would vanish from the menu.

Also double-check you're using the OcbCustomTextures from the PaintUnlocked zip. The original from NexusMods doesn't pre-size the paint registry correctly.

---

## "Do I need a new world?"

**No.** As of v1.1.0, PaintUnlocked migrates existing worlds automatically.

The first time you load a pre-PaintUnlocked world with the mod installed, you'll see something like this in the log:

```
[PaintUnlocked] Migration sentinel NOT found — treating world as legacy 8-bit
[PaintUnlocked] EagerMigrator: starting scan of N chunks
[PaintUnlocked] EagerMigrator: scan complete in Xms. N migrated, 0 failed
[PaintUnlocked] Migration complete. Sentinel written
```

The world load pauses while it scans every chunk on disk and rewrites the texture storage from 48-bit to 64-bit format. For a small testbed save (~300 chunks) this takes about a second; for big multi-region worlds it can take several seconds to a minute. Once it's done, a marker file (`paintunlocked.migrated`) is written to the save folder, and subsequent loads skip the scan entirely.

**One caveat:** if you were using custom paint packs *before* installing PaintUnlocked, those paints will appear unpainted after migration — see the entry above. Vanilla paints survive the conversion intact.

---

## "Does the server AND client need it?"

Yes. Both.

The server needs PaintUnlocked to handle the wider chunk storage and network encoding. The client needs it to decode paint IDs above 255 and display them correctly. If either side is missing the mod, you'll get crashes or wrong textures.

Both `0_PaintUnlocked` and `OcbCustomTextures` (from the zip) on both server and every client.

---

## "How many custom textures can I have?"

Up to 1023 total paint textures. Vanilla uses about 154, so that leaves roughly 869 slots for custom paint packs. In practice you probably won't hit that unless you're running a LOT of packs.

For comparison, vanilla was hardcapped at 255.

---

## "Does it work with [paint pack name]?"

If the paint pack uses OcbCustomTextures (most of them do), yes. We've tested with PyroPaints, CK Textures N Paints, and KitsunePaints. Custom POI packs like Fluffy Panda also work correctly.

The only requirement is that both PaintUnlocked and the included OcbCustomTextures fork are installed. The paint packs themselves don't need any changes.

---

## "I'm getting NullReferenceException in updateBackgroundTexture"

That's expected and harmless. It's our mod catching a null reference when the game tries to render a toolbar thumbnail for a custom paint ID that doesn't have a vanilla texture entry. The error is swallowed (caught and ignored) so it doesn't affect gameplay. You might see it in the log but it won't cause any problems.

---

## "Can I make my own paint packs?"

Yep. Check out [KitsunePaint](https://paint.kitsuneden.net) - it's a web tool that lets you upload textures and download a ready-to-install modlet with pre-built .unity3d bundles. No Unity Editor needed.

Each paint gets its own Atlas_XXX.unity3d file containing the diffuse, normal, and specular maps. Normal and specular are generated automatically if you don't provide them.
