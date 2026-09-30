# Verifying against a new 7 Days to Die build

Three scripts, run in this order, against an isolated dedicated-server install of the version
under test. None of them touch the repo's `7dtd-binaries\` or any live server; everything they
produce goes in `_work\` (gitignored).

## 0. Get the build

The dedicated server (app 294420) allows anonymous steamcmd login and has one public branch per
stable version. `-beta` is sticky in steamcmd, so always pass it explicitly and check the manifest
afterwards:

```
steamcmd +login anonymous +app_info_update 1 +app_info_print 294420 +quit    # read the "branches" block
steamcmd +force_install_dir C:\path\7d2d-vX.Y-ref +login anonymous +app_update 294420 -beta vX.Y.0 validate +quit
findstr /i "buildid BetaKey" C:\path\7d2d-vX.Y-ref\steamapps\appmanifest_294420.acf
```

## 1. Static audit (Mono.Cecil)

```
.\audit-game-version.ps1 -Managed C:\path\7d2d-vX.Y-ref\7DaysToDieServer_Data\Managed
```

Checks, for both PaintUnlocked and the OcbPaintUnlocked fork: every Harmony target method and
reflected field exists (walking base types), every parameter Harmony binds *by name* still has
that name, the transpilers' expected IL is present (`ldc.i4.8`/`0xFF` counts, `conv.u1` before
`stfld Meta`, the unguarded `list[idx].Hidden`), and the iterator state machines the fork
patches via `EnumeratorMoveNext`. It then IL-diffs every type the mods touch against the
baseline assembly (default: `7dtd-binaries\Assembly-CSharp.dll`) so you can see what TFP changed
even where the checks still pass. Pass the previous version's `Assembly-CSharp.dll` as `-Baseline`
to see only what changed since the last audit.

Finally it resolves every game member the mod DLLs reference against the new build. This is the
check that catches a field turning into a property (3.3 did that to `ItemValue.Meta`): the C#
still compiles, but an already-built DLL throws `MissingFieldException` when the method JITs.
It scans the newest shipped `PaintUnlocked-x.y.z\` bundle by default (what users have installed);
use `-ModDlls` to scan a fresh build. One DLL has to serve every supported version, so a
candidate build should pass this against each of them. Exit code is the failure count.

## 2. Compile against the new refs

```
.\build-against-refs.ps1 -Install C:\path\7d2d-vX.Y-ref
```

Builds the fork, then PaintUnlocked against it and the new game assemblies. Catches API changes
the mods bind at compile time (which the audit does not cover). Expects a sibling
`OcbCustomTextures` checkout; override with `-OcbSource`.

## 3. Headless smoke test

```
.\smoke-test.ps1 -Install C:\path\7d2d-vX.Y-ref -Mods ..\..\PaintUnlocked-1.4.2\0_PaintUnlocked,..\..\PaintUnlocked-1.4.2\OcbCustomTextures,C:\path\PyroPaints -Tag run1
.\smoke-test.ps1 -Install C:\path\7d2d-vX.Y-ref -Mods ... -Tag run2     # same save: idmap reload path
```

Proves the patches actually apply at runtime. Deploys the given mods (use the *shipped* bundle to
test what users install, plus a paint pack so custom IDs land above 512), boots the server with
EAC off and telnet on, runs `pu_audit`, then a graceful `shutdown`. Read `_work\smoke-<tag>.log`:

- `Loaded assembly PaintUnlocked (in <this install>)` - not a stale `%APPDATA%\7DaysToDie\Mods` copy
- every `[PaintUnlocked] ... registered` / `patched N constants` line, no `WRN ... not found`
- `GetFreePaintID seeded at 512`, `OCB fork check passed`, `Paint ID mapping built: N custom textures`
- run 2: `Loaded persistent paint map`, `N persisted, 0 new`, `N matched, 0 placeholders, 0 client-only`
- no `EXC`/`Exception` lines outside the usual shader/EOS/Xbox noise a headless box always prints

`-Commands` replaces the default `pu_audit` with any list of console commands to run before
`shutdown`. No client connects to a headless server, so connection setup
(`NetConnectionSimple.InitStreams` and the send path) never runs here. `stream-selftest\` is a
throwaway verification mod (never shipped) that exercises it in-process: its `pu_selftest_streams`
command builds bare `NetConnectionSimple`s, runs the patched `InitStreams` on both paths, and on
3.3+ pushes a paint package and a 100 KiB package through `WriteToPackageStagingStream`. Build
it after step 2 (it references `_work\refs`), put `ModInfo.xml` beside the DLL, and add that
folder to `-Mods` with `-Commands pu_selftest_streams,pu_audit`; look for `[PUSelfTest] RESULT PASS`.
It still isn't a substitute for a real client joining before a release.
