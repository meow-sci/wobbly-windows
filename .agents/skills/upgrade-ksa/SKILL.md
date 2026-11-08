---
name: upgrade-ksa
description: Upgrade or review wobbly-windows for a new Kitten Space Agency (KSA) version. Compare KSA and BRUTAL snapshots against this repo's Harmony hooks, native ImGui draw-data ownership, window internals, UI coverage mask, renderer, and StarMap lifecycle; apply compatibility fixes and run the repo's build and game-free checks.
---

# Upgrade wobbly-windows for a KSA release

Wobbly Windows deforms live ImGui geometry on the CPU and publishes replacement
draw lists into the main context's final draw data. KSA's coverage-mask pass and
visible UI renderer must consume the same replacement geometry. The highest-risk
changes concern native layouts, callback timing, and borrowed-memory lifetime;
a successful build cannot establish those contracts.

For an upgrade request, implement the necessary fixes and validate them. For a
review-only request, report findings without changing code. Keep the work scoped
to compatibility with the requested game version.

## Establish the baseline and inputs

Discover paths from the request, applicable workspace instructions,
`Local.Build.props`, `KSA_DLL_DIR`, and the sibling
`../ksa-game-assemblies/` checkout used by `Directory.Build.props`. Do not embed
machine-specific paths in this skill or committed build settings.

Identify:

- **Target snapshot**: the new build's `decomp/`, complete `dll/`, and
  `version.json` where available.
- **Baseline snapshot or revision**: the build last reviewed for this mod.
  Reconcile `Compatibility.ReferenceGameBuild`, the assemblies checkout SHA in
  `.github/workflows/release.yml`, `docs/releases.md`, and compatibility notes in
  `docs/implementation-research.md`. These record a reference/audit target, not
  proof that in-game acceptance occurred. Report disagreements.
- **Validation scope**: which host/platform can actually run the game. The mod
  delegates managed/native ImGui pairing to BRUTAL on Windows x64, Linux x86_64,
  and the unofficial macOS ARM64 port.

Snapshots commonly contain `decomp/`, `dll/`, and `version.json`; a single
assemblies repository may retain earlier builds in git history under
`current/`. Use history for the correct baseline rather than assuming a folder
named `previous` is the last reviewed version. Read build IDs and revisions from
metadata. Changelog `commits[]` can omit intermediate changes when
`fromRevision` does not match the baseline; use the source diff as evidence.

If an input cannot be found, continue independent checks: target DLLs permit a
build and game-free checks; two source snapshots permit behavioral review.
Ask only for the missing input needed to finish. State the resulting limits;
do not claim full compatibility from DLL-only or source-only checks. Treat
upstream snapshots as read-only.

## Efficient review and fix loop

1. Read `src/Mod.cs`, `src/Runtime/Compatibility.cs`, the project/props files,
   and the relevant parts of `docs/implementation-research.md`. The research
   explains intent but also contains proposals and historical paths; current
   `src/` defines the implementation.
2. Re-derive the coupling surface before trusting the catalog below:
   ```sh
   rg -n 'Patch\(|Program\.|GameSettings\.|ImGui\.|ImGuiContext|ImGuiWindow|ImDraw|ImVector|StarMap' src
   rg -n 'HostAssembly|Reference|PackageReference|KSAFolder|KSA_DLL_DIR' WobblyWindows.csproj Directory.Build.props
   ```
3. Compare changed-file names and build metadata first, then diff the specific
   upstream methods and layouts in the catalog. Resolve files with `rg --files`;
   assembly filenames and decompiled namespace directories can differ.
   For history-based snapshots, for example:
   ```sh
   git -C "$ksa_repo" diff --name-status "$baseline_sha" "$target_sha" -- current/decomp current/version.json
   ```
   Here the variables identify the discovered checkout and reviewed revisions;
   adapt the pathspec to its layout. For independent trees, use `diff -u` on
   the relevant files. Trace moved methods and new consumers/callers rather than
   treating a file rename as removal. Read unchanged implementations once when
   their dependencies or call order changed.
4. Build against the explicit target DLL directory to expose member/assembly
   drift. Fix compile errors first, then address source-diff findings affecting
   patches, native data, or rendering semantics. Avoid unrelated solver tuning
   and dependency upgrades.
5. Run the game-free and distribution checks below. Perform available in-game
   acceptance, record what remains untested, and synchronize the compatibility
   metadata when the review supports it.

A change limited to vehicles, orbital physics, or world shaders normally needs
no deep inspection here unless it alters a cataloged UI consumer. The mod has
no vehicle/part lookup, custom Vulkan pipeline, shader asset lookup, texture
registration, or second ImGui context of its own.

## Coupling catalog

Navigate by symbol, not historical line numbers. Check each row for relevant
changes, concentrating detailed review on changed contracts.

### Harmony entry points — `src/Mod.cs`

All four patches are applied in `AllModsLoaded`. `Patch` uses
`AccessTools.Method`; a missing or unpatchable target reaches the initialization
catch, disposes the runtime, and unpatches the mod. **There are no optional
patches in the current implementation.** A lost menu or input hook can therefore
disable wobble entirely.

| Target and patch | Contract to verify in the new game |
| --- | --- |
| `Program.PrepareImGui(double)` prefix → `PrepareFrame` | String-named target with an explicit argument type. It must run with the main ImGui context current, early enough to register hooks before that frame's `NewFrame`/`Render`. Trace context selection and all callers; attaching to an offscreen context is wrong. |
| `Program.DrawProgramMenusHook()` postfix → `Menu` | Must execute inside the active menu bar and ImGui frame. `DrawMenu` uses `Program.HoveredViewport` and `IGameViewportLifecycle.SetMenuBarInUse(true)`; check hovered-viewport and menu-capture semantics. |
| `ImGuiBackend.Shutdown()` prefix → `BeforeBackendShutdown` | Restore borrowed draw data and detach hooks before native context/backend destruction. Check shutdown callers, ordering, and context replacement/reinitialization. |
| `GameSettings.OnKeyAll` prefix → `GuardTextInput` | Resolved by name without argument-type filtering; check overload ambiguity and the boolean return contract for `ref bool __result`. Returning `true` as the result and skipping the original must still consume keys. Trace `Program` key dispatch, `Program.ConsoleWindow?.IsOpen`, and `ImGui.GetIO().WantTextInput` so UI text editing consumes hotkeys while the normal console path remains available. |

Patches using `nameof` can still suffer overload, return-type, call-order, or
Harmony changes after a green build. Runtime evidence is the
`[wobbly-windows]` log, especially `Initialization failed:`,
`Attached to the main ImGui context.`, and `Effect disabled:`.

### Context hooks and shared frame lifetime — `src/Runtime/WobbleRuntime.cs`

Inspect `ImGui.Internal.AddContextHook`/`RemoveContextHook`,
`ImGuiContextHook`, `ImGuiContextHookCallback`, and hook enum values. Verify
native callback calling convention/signature, registration/removal rules, and
that the managed delegate remains rooted until every hook is detached.

The frame contract is:

```text
PrepareImGui prefix captures the main context
→ NewFramePre restores the previous borrowed header
→ game and StarMap UI submission
→ ImGui.Render finalizes draw data
→ RenderPost builds and publishes replacement lists once per frame
→ UiCoverageMaskSystem and visible ImGui renderer consume them
→ PostRender / platform-window consumers finish
→ next NewFramePre restores the original header before reuse
```

Diff `Program.PrepareImGui`, `OnFrame`, `RenderGame`, `PostRender`,
`ImGuiBackend`, and any newly introduced draw-data consumers. Confirm that no
consumer caches or uploads original geometry before `RenderPost`, that no late
consumer outlives restoration/storage reuse, and that rendering stays serialized
as assumed. `FrameCount` suppresses double processing; hooks reject foreign
contexts. Re-check context switching, shutdown-hook timing, `Detach`, `Fail`,
and `Dispose` together.

### Native draw-data contracts — `src/Rendering/` and `Compatibility.cs`

- **`NativeBuffer<T>`** constructs an `ImVector<T>` header as two `int` values
  (size/capacity), then a pointer. `Compatibility.ValidateBindings` checks its
  size and managed field interpretation for vertices, `ushort` indices,
  commands, and list pointers on a 64-bit runtime. These checks do not certify
  the host native library's internal layouts.
- **`OwnedDrawList`** allocates a zeroed `ImDrawList` and fills only
  `VtxBuffer`, `IdxBuffer`, `CmdBuffer`, and `Flags`. Audit every downstream
  consumer's field accesses if the backend changes. New reads of internal
  write pointers, callback storage, shared data, or splitter state can make
  this synthetic list unsafe. Never invoke native draw-list methods on it.
- **`DrawPublication`** borrows `ImDrawData*` and its original `CmdLists`
  header/count/totals, then substitutes mod-owned buffers. Inspect changes to
  `ImDrawData`, `ImDrawListPtr`, `ImVector<T>`, and `GetDrawData` ownership.
  Preserve restore-before-free on next frame, failure, detach, and shutdown.
  Never clear/free/resize a borrowed vector through its original header.
- **`GeometryProcessor`** assumes triangle commands, `ushort` source indices,
  additive `IdxOffset`/`VtxOffset`, vertex position/UV and low-byte-RGBA packed
  color, and logical-coordinate `ClipRect`/`DisplayPos`/`DisplaySize`.
  It preclips before warping, copies command texture/alpha semantics, and emits
  command-local 16-bit index chunks with vertex offsets. Inspect `ImDrawVert`,
  `ImDrawCmd`, texture references, color packing, and renderer index type.
  `RendererHasVtxOffset` is required; `FramebufferScale` controls tessellation
  quality and must remain consistent with renderer scissor scaling.

BRUTAL owns native resolution and the managed/native pairing. Version strings
are diagnostic, and the reference build is not a runtime whitelist. Do not
restore historical hash checks, hard-coded native-library loading, or a native
`DebugCheckVersionAndDataLayout` probe that may assert and cannot certify
internal window/context structures.

### Visible renderer and UI coverage mask

Inspect `ImGuiBackendVulkanImpl` upload/draw paths and
`UiCoverageMaskSystem.RecordMaskGeneration` plus its draw-command replay.
Both must accept the synthetic lists and use matching positions, offsets,
textures, alpha, clipping, and framebuffer scale. Check texture trust/opacity
rules, callback handling, explicit opaque rectangles, and changes to caching
or render order. A correct visible window with a rectangular hole in the world
behind it is a coverage-mask failure.

`GaugeCanvas` uses custom callbacks and separate coverage registration; such
window families are excluded. New explicit coverage registrations on otherwise
ordinary windows require equivalent review. HDR/presentation changes matter
when they change a UI consumer; the mod does not own the GPU pipeline.

### Window ownership, eligibility, and motion inputs

In `WobbleRuntime.Render`/`Eligible`, diff the BRUTAL internal
`ImGuiContext` and `ImGuiWindow` bindings and relevant native behavior:

- `Windows`, `FrameCount`, `MovingWindow`, `IO.MousePos`/`DeltaTime`;
  `Active`, `LastFrameActive`, `ID`, `Name`, `Pos`, `Size`,
  `RootWindow`, `ParentWindow`, `DrawList`, and `ViewportId`.
- `Hidden`, `Collapsed`, `DockIsActive`, `DockNodeAsHost`, and excluded
  `ImGuiWindowFlags` (fixed/noninteractive, child, popup/modal, tooltip,
  child menu, docking host). Children share their root's warp. Popups can block
  ancestors; shared draw-list owners, foreign viewports, and any command with
  `UserCallback != 0` block the family.
- IDs associate persistent state; pointers are refreshed each frame and state
  resets after pointer changes or inactivity. Main-viewport boundaries gate
  eligibility. Unknown/global lists retain their order and original data.

New themed drawing may expose geometry rejection without any signature change.
Check `ConsoleStyle.BeginWindowCore`-style rounded bezels, thin antialias
triangles, and child bodies. Preserve the shared triangulated-grid mapping;
the thin-themed-geometry and triangulation-coverage regressions in
`tests/Checks.cs` protect against intermittent fallback/stutter.

The intended input contract keeps native mouse hitboxes unchanged during wobble.
Use UI `DeltaTime`, including while paused; preserve exact identity at rest.
An upgrade does not authorize inverse hit testing or rewriting global mouse input.

### StarMap, references, and packaging

`src/Mod.cs` uses `[StarMapMod]`, `[StarMapImmediateLoad]` with
`KSA.Mod.DirectoryPath`, `[StarMapAllModsLoaded]`, `[StarMapAfterGui]`
with `double dt`, and `[StarMapUnload]`. Check lifecycle ordering, UI-frame
availability, and teardown if the loader also changes. There is one entry
assembly (`WobblyWindows` in `mod.toml`) and no exported inter-mod ABI.

Read the live `HostAssembly` list in `WobblyWindows.csproj` for the exact
KSA/BRUTAL/Planet DLL set. Assembly splits or moves need reference updates even
when namespaces remain unchanged. StarMap.API and Lib.Harmony are compile-only
host contracts, as are the game references in normal builds. Preserve that
ownership and the locked NuGet restore; do not bundle host DLLs/native libraries
to solve loading problems. `RunChecks=true` intentionally copies managed host
references into a separate checks output directory.

## Build and validation

From the repo root, use .NET 10 and the complete target DLL directory. An explicit
MSBuild property overrides `Local.Build.props`, which can otherwise mask a
changed `KSA_DLL_DIR`. Always pass the same target to build and checks:

```sh
ksa_dll_dir='/absolute/path/to/target-snapshot/dll'
dotnet build WobblyWindows.slnx -c Release --no-incremental -p:KSAFolder="$ksa_dll_dir" -p:RestoreLockedMode=true --nologo -v quiet
dotnet run --project WobblyWindows.csproj -c Debug -p:RunChecks=true -p:KSAFolder="$ksa_dll_dir" -p:RestoreLockedMode=true
python3 tools/validate-dist.py artifacts/stage/wobbly-windows
mod_version="$(python3 -c 'import xml.etree.ElementTree as E; print(E.parse("WobblyWindows.csproj").findtext("./PropertyGroup/Version"))')"
python3 tools/check-release-archive.py artifacts/stage/wobbly-windows "artifacts/dist/wobbly-windows-$mod_version.zip"
```

This repo's checks are a conditional executable in `tests/Checks.cs`; use
`dotnet run -p:RunChecks=true`, not `dotnet test`. They cover binding headers,
publication restoration, motion, clipping/attributes, thin themed triangles,
shared warp coverage, 16-bit segmentation, malformed data, budgets, folds, and
warm allocations. They launch neither native ImGui nor KSA and cannot prove
patch application, native internal ABI, live hook timing, or GPU mask behavior.
If the checks cannot load target assemblies on the current architecture,
report that limitation separately from compilation.

Release builds generate staging, ZIP, and checksum automatically. The exact
payload is `WobblyWindows.dll`, `WobblyWindows.deps.json`, `mod.toml`,
`LICENSE`, and `README.md` beneath `wobbly-windows/`.
Run `python3 tools/check-release-policy.py` if changing release metadata,
workflow pins, dependencies, or packaging.

### In-game acceptance

Use the new game with a matching host loader/native binding pair. Record the
build, platform, and observed results:

- Confirm successful initialization and main-context attachment. Open
  **Wobbly Windows → Settings → Diagnostics** and try **Identity output check**:
  copied geometry should match the original UI in both visible and mask passes.
- Drag and release ordinary floating windows, especially themed Flight Plan,
  Resources, or Ground Track windows. Check continuous live contents, rounded
  bezels, scrolling children/tables, overlap/transparency, and exact settling.
  Watch for intermittent fallback in diagnostics and world holes behind edges.
- Exercise enable/off, per-window exclusions, resize/collapse/close/reopen,
  docking, popups/tooltips, screen edges, UI scale/DPI changes, and pause.
  Unsupported families should stay intact. Foreign/offscreen contexts stay
  unaffected.
- Check menu capture, typing into UI editors, the normal game console, and
  normal controls after settling. Temporary visual/hitbox displacement while
  wobbling is intentional.
- Exercise shutdown/unload and context/backend recreation where available;
  ensure no stale-pointer errors, crashes, or duplicate hooks. Record processing
  time/output vertices on representative windows when rendering changes.

## Finish the upgrade

When adopting the reviewed target, align
`Compatibility.ReferenceGameBuild`, the missing-DLL diagnostic in
`WobblyWindows.csproj`, the immutable assemblies checkout SHA and build comment
in `.github/workflows/release.yml`, and the pin explanation in
`docs/releases.md`. Add a dated compatibility note to
`docs/implementation-research.md` with source revisions and actual validation
coverage; keep historical research evidence labeled as history. Do not infer
runtime acceptance from an updated pin or diagnostic. Mod release versions are
separate from game versions; CI derives them from `release-major.txt` and its
run counter.

Report baseline → target builds/revisions, affected upstream contracts and repo
symbols, fixes (or why none are needed), checks run/results, and pending
acceptance by platform. Use a compact table for multiple findings and classify
the outcome as **no code changes**, **mechanical fixes**, or **behavioral work**,
with validation limits explicit. Native memory/lifetime and mask divergence
are critical; failed patches disable initialization; stale references/pins can
make local and CI results disagree.

When coupling changes, update this catalog in the same work item so the next
upgrade starts from the current implementation.
