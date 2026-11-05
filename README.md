# Wobbly Windows

Spring-driven wobble for ordinary floating ImGui windows in Kitten Space Agency. Window contents stay live during dragging and settling. Native mouse hitboxes remain unchanged: let a window settle before targeting its controls.

## Compatibility

This initial release targets the inspected Windows x64 KSA **2026.10.25.5707** binaries (Dear ImGui 1.92.2), with StarMap. It checks the loaded KSA/BRUTAL module identities and native ImGui fingerprint before touching internal structures. An unknown build disables the effect with a diagnostic instead of guessing the layout. New game builds need an audited compatibility update.

Docked windows, popups, menus, tooltips, custom draw callbacks (including gauges), secondary/offscreen contexts, detached platform windows, and windows touching the viewport boundary are excluded. Child windows share their root's deformation. Ordinary purrtty windows can participate; its in-world panels remain unchanged.

Compatibility with other mods that replace or resize finalized ImGui draw data needs separate testing. The output views are mod-owned and read-only to render consumers; another modifier must not free or reallocate them through ImGui's allocator.

## Use

Install the `wobbly-windows` folder from the release ZIP in your StarMap mod location and enable its ID. Open the **Wobbly Windows** game menu for settings or a live sample window. The effect is enabled by default. Choose Subtle, Classic, or Playful; adjust strength, responsiveness, and settling; exclude individual windows or disable the effect for reduced motion. Preferences are saved to `settings.json` alongside the installed mod.

The mouse continues to interact with native rectangles during settling. Clicking a visually displaced control can miss it or hit a different control. There is no input remapping or artificial click blocking.

## Build

Use the .NET 10 SDK and the matching game reference DLLs. The sibling `../ksa-game-assemblies/current/dll` snapshot is detected automatically. Otherwise set `KSA_DLL_DIR`, pass `-p:KSAFolder=/absolute/path/to/dll`, or copy `Local.Build.props.example` to the ignored `Local.Build.props`.

```sh
dotnet build WobblyWindows.slnx -c Release
```

The single `WobblyWindows.csproj` owns compilation and the entire distribution. Every normal Release build, including incremental builds, recreates:

- `artifacts/stage/wobbly-windows/` — complete installable folder
- `artifacts/dist/wobbly-windows-0.1.0.zip` — archive with one `wobbly-windows/` root
- `artifacts/dist/wobbly-windows-0.1.0.zip.sha256` — archive checksum

Packaging uses MSBuild's copy, ZIP and hash tasks. It requires no external staging script or additional packaging command. Only the mod DLL, dependency manifest, mod manifest, license, and this README are shipped. Game/loader/Harmony/native ImGui assemblies are host-owned and excluded. Building never installs into or removes files from the game directory.

## Checks

The same project has an isolated executable check configuration; it does not produce a mod distribution:

```sh
dotnet run --project WobblyWindows.csproj -c Debug -p:RunChecks=true
```

The checks cover the solver, clipping and attribute interpolation, index segmentation, native output ownership, and draw-data substitution/restoration. They do not initialize native ImGui or launch KSA.

In-game acceptance still requires dragging and releasing windows with UI pixel culling on/off, overlapping roots and scrolling children, hiding/restoring the HUD, resizing, pause/time warp, live purrtty output, and disabling/unloading. The diagnostics panel includes an identity-output mode for checking substitution independently of wobble.

## Implementation

The mod attaches `RenderPost` and `NewFramePre` callbacks only to the main game ImGui context. It preclips original triangles, subdivides them on a common grid, and deforms live geometry with a bounded 4×4 spring lattice. Replacement draw data remains alive for both the coverage mask and visible renderer, then the original borrowed headers are restored before the next frame. Original window draw buffers and mouse state are never edited.

Structure and MSBuild packaging follow the purr2d2 reference. ImGenie and Compiz informed the research; the implementation is original C#. Detailed source research is in `docs/implementation-research.md`.
