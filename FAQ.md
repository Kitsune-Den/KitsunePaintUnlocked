# PaintUnlocked FAQ

## "I installed PaintUnlocked on an existing world and my custom paints are gone"

This is expected behaviour, not a bug. Here's what's going on.

When you install a paint pack like PyroPaints or CK Textures *without* PaintUnlocked, the game registers those custom paints in the free IDs below 256 (vanilla V3.2 has 156 paints spread across IDs 0-183, so custom paints fill the gaps and the slots above). When PaintUnlocked is enabled, custom paints register at ID 512 and up instead, because the GPU atlas needs that gap to handle the wider paint range.

The migration system updates how chunk data is stored on disk, but it can't follow paints to their new IDs — a block that used to say "paint ID 200" still says 200, but in the new world that slot doesn't point to your custom paint anymore. So those blocks render as unpainted.

**Vanilla paints are unaffected**: their IDs stay stable.

**Fix:** Repaint the affected blocks. Your existing custom paint packs all still work, they're just at different IDs now, so picking them again from the paint menu and applying them gets you back where you were.

This only happens once, when first installing PaintUnlocked. After that, the IDs are stable for that world.

---

## "painting.xml Index was outside the bounds of the array"

You're using the wrong OcbCustomTextures.

PaintUnlocked ships with a special fork of OcbCustomTextures that handles the larger paint ID range. If you downloaded OCB separately from NexusMods, it doesn't have the changes needed and will crash when it tries to register custom paints above ID 255.

**Fix:** Delete your OcbCustomTextures folder entirely. Extract both folders from the PaintUnlocked zip - `0_PaintUnlocked` AND `OcbCustomTextures`. They're a matched pair. Both must come from the same zip.

**Already did that and it still happens?** Then another mod is carrying its own copy of OCB. Some paint packs, texture mods and overhauls bundle stock OcbCustomTextures inside their own folder, and nothing on their mod page says so. A giveaway: if you remove PaintUnlocked and its OCB and still have more than vanilla's 156 paints in the menu, whatever is adding the extras brought its own OCB.

To find it, open your `Mods` folder and search it (Windows search box, top right) for `CustomTextures.dll`. There must be exactly **one** result, inside the `OcbCustomTextures` folder from the PaintUnlocked zip. Delete every other OCB copy; the paints from those mods will still load, through the fork. Also check for a second folder that is OCB under a different name (for example `0_OcbCustomTextures`). The game loads mod folders alphabetically and skips a later one with the same mod name, so a stock copy that sorts first silently replaces the fork.

Newer PaintUnlocked versions check for all of this when the game starts. Look in your log for:

```
[PaintUnlocked] NON-FORKED OcbCustomTextures DETECTED
[PaintUnlocked] You're using a non-forked OcbCustomTextures. ...
[PaintUnlocked] Stock OCB in use: <the folder to delete>
```

or `MORE THAN ONE OcbCustomTextures INSTALLED`. The lines after it name every copy it found. When it detects stock OCB, PaintUnlocked stays out of OCB's way so the game still loads instead of crashing. Custom paints then stay capped at 255 until the extra copy is gone.

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

The world load waits while it rewrites every chunk on disk from 48-bit to 64-bit texture storage. It costs a few milliseconds per chunk: a small testbed save is done in a second, an explored world with ~40,000 chunks takes around three minutes on a fast PC, and a fully explored 8k/10k map takes longer. The loading screen shows `PaintUnlocked: upgrading this save` with a chunk counter the whole time, so it's working, not frozen. Once it's done, a marker file (`paintunlocked.migrated`) is written to the save folder, and subsequent loads skip the scan entirely.

Before touching anything it copies the save's `Region` folder to `Region.pre-paintunlocked` (skipped, with a log warning, if the disk doesn't have room). Delete that folder once you're happy with the world. Closing the game during the upgrade is safe: every converted chunk is recorded in `paintunlocked.migrating`, and the next load continues from there.

**One caveat:** if you were using custom paint packs *before* installing PaintUnlocked, those paints will appear unpainted after migration — see the entry above. Vanilla paints survive the conversion intact.

---

## "Stuck on 'Configuring splatmaps' when loading my save"

Versions up to 1.4.4 ran the save upgrade above without updating the loading screen. The game sat on "Configuring splatmaps" (and Windows could call it Not Responding) until every chunk was done, which on a big save took minutes. Update PaintUnlocked: the upgrade now shows its progress.

If you closed the game during one of those long loads, 1.4.4 and older lost track of the chunks it had already converted, and the next load threw them away (`EXCEPTION: In load chunk ... Attempted to read past the end of the stream` in the log, terrain regenerated). The current version recognises those converted chunks and keeps them, so load the save with the update **before** playing it again on the old version. Any chunk already thrown away left an `error_backup_*.bak` file in the save's `Region` folder; those can't be put back automatically.

---

## "Paint all sides leaves one part unpainted"

That part is the block's 7th face, "Middle". Besides its six sides, a block shape can have geometry that isn't on the outside of the block: the frame pieces inside the window shapes, the inside of the hollow counters, `logsHalfCorner`. About 130 shapes have one. The game stores paint for six faces only, so in vanilla the Middle part always shows the block's default texture, and "paint all sides" skips it.

Since 1.5.1 PaintUnlocked remembers Middle as "the same paint as one of the sides" in four spare bits it has left over:

- **Paint all sides** now paints the Middle part too.
- **Clicking a Middle part** with the brush works if one of that block's sides already has the paint. Otherwise nothing changes (same as vanilla), and the log says why.
- The Middle part **follows its side**: repaint that side and the Middle part changes with it.

Blocks painted before 1.5.1 keep their unpainted Middle until you paint them again with paint all sides.

---

## "Does the server AND client need it?"

Yes. Both.

The server needs PaintUnlocked to handle the wider chunk storage and network encoding. The client needs it to decode paint IDs above 255 and display them correctly. If either side is missing the mod, you'll get crashes or wrong textures.

Both `0_PaintUnlocked` and `OcbCustomTextures` (from the zip) on both server and every client.

---

## "How many custom textures can I have?"

Two limits, and the second one is usually the one you hit first.

**Paints: up to 512 custom, on top of vanilla's own.** Custom paints get IDs 512 through 1023 (1023 is the highest ID a painted block face can store), and vanilla V3.2 keeps its 156 paints below that. For comparison, vanilla was hardcapped at 255.

**Texture slots: 2048 in total, vanilla included.** Every texture lives in one big texture array on your graphics card, and 2048 is a hard limit of the graphics API that no mod can raise. Vanilla uses 407 of them, which leaves 1641 for packs. A 1×1 paint takes one slot, but a 2×2 paint takes four, so big multi-tile packs fill this up fast. For example, CK Textures N Paints and CK Textures N Paints Vol II together use 1628 slots, which leaves room for only 13 more.

---

## "Too many custom textures: they need N texture slots, but your graphics card supports at most 2048"

Your paint and texture packs together need more texture slots than the 2048 a graphics card can hold (see above). Since 1.5.2, OcbCustomTextures loads everything that fits and skips the rest, so the last N textures are missing from the paint menu. Everything else works.

Before 1.5.2 this showed up as `Texture2DArray has out of range depth (got N max supported 2048)` followed by `Loading and parsing 'painting.xml' failed`, and every custom paint broke.

**Fix:** Remove a texture or paint pack (move its folder out of `Mods`) until you're under the limit. Packs load alphabetically by folder name, so the textures that get skipped are the ones from the pack whose folder name sorts last.

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
