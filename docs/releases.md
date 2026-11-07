# Release maintenance

The release workflow follows purr2d2's rolling release pattern. Pushes and manual
branch dispatches publish: `main` produces stable releases and every other branch
produces betas. Pull requests and tag refs do not publish. Every workflow run
consumes a build number, so gaps are normal.

`release-major.txt` is the single manually maintained release major, initially
`1`. Major `1` and workflow run `123` produce `1.123.0` on main or
`1.123.0-beta` elsewhere, with matching project, manifest, ZIP and checksum
names and a `v`-prefixed tag. Changing the major does not reset the build counter.
Keep `.github/workflows/release.yml`: its run number is the build counter across
branches. Local development versions in `WobblyWindows.csproj` and `mod.toml`
stay in sync independently; CI stamps them without committing those changes.

The repository requires `KSA_GAME_ASSEMBLIES_PAT`, a fine-grained token with
read-only Contents access to `meow-sci/ksa-game-assemblies`. The workflow pins
`4b3e9e2281c6eec25c9ab77ddf2961ec53ec3dc8`
(KSA 2026.10.25.5707), matching the mod's audited compatibility target. Update
that pin only with a compatibility review. Full builds run for branch pushes,
manual dispatches and same-repository PRs. Fork PRs run offline release-policy
checks only. Build jobs have read-only repository permissions; publishing and
cleanup have Contents write permission.

The .NET Release build owns staging, ZIP creation and hashing. CI validates the
exact five-file payload, matching versions and compile-only host dependencies,
runs the game-free checks, then compares the release ZIP byte-for-byte with
stage and verifies its checksum before transferring assets to the publishing
job. No game assemblies are distributed. These checks do not launch KSA or
replace the in-game acceptance described in the README.

Reruns preserve an existing release and its assets. After successful publishing,
cleanup fetches every release page and keeps the newest 10 stable releases or
5 betas by publication time (ID breaks ties). Beta branches share one pool;
cleanup is serialized per channel. All positive major versions participate.
Only published, non-draft `vMAJOR.BUILD.0` / `vMAJOR.BUILD.0-beta` tags with the
matching prerelease flag qualify. Older qualifying releases and their tags are
deleted; named or other-version releases are left alone.

Local verification (Python 3.11+ and .NET 10; no GitHub writes):

```sh
python3 tools/check-release-policy.py
dotnet build WobblyWindows.slnx -c Release -p:RestoreLockedMode=true --nologo -v quiet
python3 tools/validate-dist.py artifacts/stage/wobbly-windows
python3 tools/check-release-archive.py artifacts/stage/wobbly-windows artifacts/dist/wobbly-windows-0.1.0.zip
dotnet run --project WobblyWindows.csproj -c Debug -p:RunChecks=true -p:RestoreLockedMode=true
```

Use the current development version in the archive path if it changes. Release
policy checks exercise metadata, atomic version stamping, package validation,
mocked publishing (including failures and reruns), and paginated retention.

When updating an installed mod, back up `settings.json`, remove the old
`wobbly-windows/` folder, extract the new folder, restore the saved settings,
and restart KSA.
