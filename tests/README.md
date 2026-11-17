# Regression suite

```powershell
# from work/companion
dotnet test tests
```

Requires the .NET 9 SDK. The suite never starts the game and never touches Steam; it reads the shipped
binaries as data. **61 cases in 10 classes.**

## What it checks

| Test class | Cases | What it pins down | Needs |
|------------|-------|-------------------|-------|
| `BreakpointRegressionTests` | 12 | The shipped `sts2_lan_connect` **0.6.4** DLL has **zero** unresolvable member references on 0.113 — and the six breaks 0.6.3 had (named, one case each) are asserted to be gone, so an upstream regression re-opens the fix that used to cover them. Also proves the generic-instantiation entries are probe artefacts, not hidden breaks. | game + official 0.6.4 DLL |
| `GameApiShapeTests` | 9 | What 0.113 actually changed, read from `sts2.dll` metadata: the six old shapes are gone and the new ones are present. Game-side facts only — no remaining fix binds to them. | game |
| `BuiltPatchTests` | 2 | The patch DLL itself resolves completely against 0.113 and declares `[ModInitializer("Init")]`. | built patch (+ game for the scan) |
| `GameOverLayoutTests` | 12 | The pure geometry of `GameOverLayout.cs` (linked-compiled, i.e. the shipping file): injected rects stay inside both viewports, never overlap, the two buttons keep a 16 px gap, the caption follows the button, and the client layout has no button. This is the only class that runs everywhere. | nothing |
| `ReleasePackageTests` | 14 | The Workshop workspace is uploadable: `image.png` is a square PNG under 1 MiB, `workshop.json` is valid and depends on item `3749766330`, `content/` holds only this project's files, the packaged DLL is byte-identical to the current build, **the release zip in `dist/` is byte-identical to `content/`**, and the documentation set is complete. | nothing (some cases need `dist/`, `image.png`, the built patch or the upstream licence — see below) |
| `TailRosterContractTests` | 4 | The tail-roster epoch reset (`TailRoster`) contract against the shipped **0.6.4** binary: the five reflection anchors the reset depends on. | game + official 0.6.4 DLL |
| `RosterEpochRegressionTests` | 4 | Why the roster must be rebaselined: an authority that accepted the old epoch rejects the new one, `Reset` makes the next epoch acceptable again, repeating the bytes still passes, and the host peer id survives the reset. Drives the real host types with the real serializers. | game + official 0.6.4 DLL |
| `FixRegistryTests` | 1 | The **inventory** of repairs: `CompatFixes.Build()` yields exactly the expected ids in the expected order (so deleting a line cannot stay green). | nothing (source-level) |
| `GameDependentCoverageTests` | 1 | The anti-false-green gate: fails when an input that makes one of the 32 game-dependent cases skip is missing, and fails when the cases that ran are not exactly the expected list — naming the ones that are missing or unexpected, so a green run always means they executed. | nothing (probes the inputs) |
| `PoolAddressLogicTests` | 2 | The two pure helpers behind the pool's room key and its "same version" filter: address equality ignores case and a trailing slash only, and a version key ignores a leading `v` and surrounding space only. | game + built patch |

## Missing inputs: skipped *and* reported

`GameDependentCoverageTests` is the reason a missing input can no longer be silent. 32 of the
cases need the game DLL, an official LAN Connect DLL or the built patch, and six more in `ReleasePackageTests`
need the packaging workspace or a release zip; all of them call `Assert.Skip` when their input is absent, and a
skip still exits 0, so a green run on a machine without them used to prove nothing. The gate:

* checks every input that can trigger one of those skips and **fails**, listing the missing inputs, when any of
  them is absent;
* compares the game-dependent cases that actually reached their assertions in this run (`TestRunLedger`, one
  entry per case including each theory row) with `TestRunLedger.ExpectedGameDependentTests`, and names the cases
  that are missing or unexpected. A bare count could not say which case went missing, or tell a new case from a
  skipped one. The fact may run before the cases it counts, so it waits for them (bounded at 60 s) instead of
  asserting the runner's order.

Set `STS2_ALLOW_MISSING_GAME_INPUTS=1` to downgrade a missing input to a skip (for a developer
without the game). The skip message still lists every missing input, so the exception is never silent.

| Variable | Default | Meaning |
|----------|---------|---------|
| `STS2_GAME_DATA_DIR` | auto-detected Steam libraries | folder containing `sts2.dll` |
| `STS2_LAN_CONNECT_DLL` | the 0.6.4 bundle under `../lobby/upstream-064/src/*/releases/...` | official DLL (0.6.3 is out of support since 2.0.0, so this slot resolves to 0.6.4 too) |
| `STS2_LAN_CONNECT_064_DLL` | `../lobby/upstream-064/src/*/releases/sts2_lan_connect/sts2_lan_connect.dll` | official 0.6.4 DLL (the build this mod requires) |
| `STS2_COMPANION_ROOT` | two levels above this file | companion project root |
| `STS2_PACKAGING_DIR` | `<companion>/packaging` | Workshop workspace to validate |
| `STS2_ALLOW_MISSING_GAME_INPUTS` | unset | `1` turns a missing game/build input into a skip instead of a failure |

A file that *is* present but wrong (a different official build, a stale DLL in `content/`, a stale zip in
`dist/`) fails loudly: those are the mistakes the suite exists to catch.

## Source-level assertions, and what they cannot prove

`FixRegistryTests` reads the patch's **source**; it does not build or run it. It can prove that a repair
was not deleted, renamed or reordered. `TailRosterContractTests` goes one step further and reflects over
the shipped 0.6.4 binary, but it only reads metadata — no member is invoked. Neither can prove the patch
behaves correctly in a game.

Runtime evidence lives in `tools/smoke-test`, which loads the shipped
`packaging/content/sts2_lobby_enhance.dll` (not the source), calls `Entry.RunSilently()`
reflectively, and asserts both the applied id list and **one real Harmony seam per repair**. The two
layers are complementary: the tests run on a machine without the game, the tool cannot be fooled by a
stale build.

## How the scan works

`Scan/BreakpointScanner.cs` loads the assembly and asks the CLR itself whether each metadata token
resolves (`Module.ResolveMember`), exactly as the JIT would when it first compiles a method that uses
it. Three tables are scanned — `MemberReferences`, `FieldDefinitions`, `MethodDefinitions` — because a
broken *signature* in any of them is fatal for the assembly, not just a broken call site.

Two details make the result trustworthy:

- **Generic instantiations.** `Module.ResolveMember` cannot resolve `List<Player>::Add` without being
  handed the generic arguments, so those references always look broken. Instead of filtering them out,
  the scanner decodes the declaring `TypeSpecification`, resolves the declaring type, and checks that
  the member name really exists on it. Anything it cannot explain lands in `Unexplained` and fails the
  test.
- **No filtering by exception type.** Classification is by *what* failed, not by which exception the
  runtime happened to throw, so a runtime upgrade cannot silently change the answer.

## Adding a break

If a future game build breaks the patch:

1. Re-run the scan and read `GenuineBreaks`.
2. Add the new break to `HistoricalBreaks` in `BreakpointRegressionTests` **and** to the tables in
   `../docs/README.md` and `../docs/CHANGELOG.md`.
3. Re-derive the fix. The suite will fail until the documentation and the code agree again.

Adding or removing a repair additionally requires editing `FixRegistryTests.ExpectedFixIds` and
`tools/smoke-test/Program.cs`'s `expectedFixIds` — three copies on purpose.
