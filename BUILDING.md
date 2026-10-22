# Building

**Read this first: this repository is for reading the code, not for building it in
one click.**

The source is published for transparency and auditability. A full build of a Cities:
Skylines II mod requires a configured CS2 modding environment, and on top of that
**this public snapshot deliberately omits the project's private source generators**,
so the snapshot does **not** compile end-to-end for third parties. That is by design,
not a bug — see "Why the public snapshot does not fully compile" below.

If you only want to verify what the mod does, just read the code. If you want to
understand the build contract anyway, the requirements below are accurate to the
project's `CivicSurvival/CivicSurvival.csproj`.

## Quick reference (three task runners)

Three equivalent task runners are shipped so contributors can use whichever is
installed. **All three ship identical recipes** — pick the one that exists on your
PATH:

| Runner                | Install                                                                           | Invoke      |
| --------------------- | --------------------------------------------------------------------------------- | ----------- |
| **GNU make**          | bundled on Linux/macOS; `choco install make` on Windows                           | `make help` |
| **just** (casey/just) | `cargo install just` / `scoop install just` / `brew install just`                 | `just`      |
| **Task** (go-task)    | `go install github.com/go-task/task/v3/cmd/task@latest` / `scoop install go-task` | `task`      |

The recipes mirror each other (lint / format / test / scorecard / ci / build /
install / launch / update / remove). For a list of every recipe:

```sh
make help        # or: just --list   /   task --list
```

## Dev workflow — full local loop

The fastest path from a clean checkout to "mod loaded into CS2":

```sh
# 1. Lint + format + test the Python discipline suites (no toolchain needed)
make ci          # or: just ci    /   task ci

# 2. Build the mod DLL (needs CS2 + toolchain)
make build       # release build
make build-dev   # debug build (faster)

# 3. Build the native installer (single .exe, 184 KB, .NET 9)
make build-installer
# → installer/dist/civicsurvival-installer.exe

# 4. Verify CS2 install + BepInEx presence
make status-cs2

# 5. Install the mod into CS2 (writes PublishConfiguration.xml + Skyve manifest)
make install

# 6. Launch CS2
make launch
```

Equivalent recipes (full matrix):

| Recipe                    | Make                       | Just                       | Task                       | Purpose                      |
| ------------------------- | -------------------------- | -------------------------- | -------------------------- | ---------------------------- |
| Lint Python               | `make lint`                | `just lint`                | `task lint`                | ruff check                   |
| Format Python             | `make format`              | `just format`              | `task format`              | ruff format (auto-fix)       |
| Verify format             | `make format-check`        | `just format-check`        | `task format-check`        | ruff format --check          |
| Run tests                 | `make test`                | `just test`                | `task test`                | pytest (excludes env-broken) |
| Scorecard                 | `make scorecard`           | `just scorecard`           | `task scorecard`           | 88-pillar scorecard          |
| Scorecard + regress guard | `make scorecard-check`     | `just scorecard-check`     | `task scorecard-check`     | with --fail-on-drop          |
| Build mod                 | `make build`               | `just build`               | `task build`               | C# Release build             |
| Dev build                 | `make build-dev`           | `just build-dev`           | `task build-dev`           | C# Debug, no restore         |
| Build installer           | `make build-installer`     | `just build-installer`     | `task build-installer`     | .NET 9 win-x64               |
| Build all RIDs            | `make build-installer-all` | `just build-installer-all` | `task build-installer-all` | win+linux+mac                |
| Install mod               | `make install`             | `just install`             | `task install`             | install into CS2             |
| Update mod                | `make update`              | `just update`              | `task update`              | preserve config              |
| Remove mod                | `make remove`              | `just remove`              | `task remove`              | full uninstall               |
| Check CS2                 | `make status-cs2`          | `just status-cs2`          | `task status-cs2`          | discover + verify            |
| Check mod                 | `make status-mod`          | `just status-mod`          | `task status-mod`          | show installed               |
| Launch CS2                | `make launch`              | `just launch`              | `task launch`              | Steam URL handler            |
| Full CI parity            | `make ci`                  | `just ci`                  | `task ci`                  | lint+fmt+test+scorecard      |
| Loc regression            | `make loc-audit`           | `just loc-audit`           | `task loc-audit`           | localization suite           |
| Current version           | `make release-current`     | `just release-current`     | `task release-current`     | from csproj/manifest         |
| Verify release            | `make release-verify`      | `just release-verify`      | `task release-verify`      | all surfaces agree           |

## Pre-build hooks (lefthook)

The repo ships a `.lefthook.yml` that runs the same gates the CI runs, on the
developer's box first:

```sh
brew install lefthook          # or scoop / apt
lefthook install               # one-time per clone
```

Pre-commit and pre-push hooks then run actionlint, prettier, ruff, pytest, gitleaks.
A discipline test (`tests/test_lefthook_config_discipline.py`) ensures the config
stays in sync.

## CI (GitHub Actions + Mergify)

`make ci` is the local mirror of the GitHub Actions pipeline. The release workflow
(`.github/workflows/release.yml`) is the production path: it builds the mod + the
multi-RID installers, bundles them into a `CivicSurvival.tar.gz`, attaches the bundle
to a GitHub Release, and updates the release tag.

Self-hosted runner hardening (SHA-pinned actions, ephemeral label, persist-credentials
false) is enforced by `tests/test_runner_hardening_discipline.py`.

## Requirements (for actual C# build)

1. **Cities: Skylines II installed.** Provides `Game.dll` plus the Unity / Colossal /
   UnityEngine managed assemblies the project references. The build resolves these
   from the game's `…\Cities2_Data\Managed\` folder. If your game lives somewhere the
   toolchain does not auto-detect, point the build at it via the `GameManagedPath`
   MSBuild property (`-p:GameManagedPath=...`) or the `CSII_MANAGEDPATH` environment
   variable — no need to edit the `.csproj`.

   The `Makefile` defaults `GAME_MANAGED_PATH` to the standard Steam path:
   `C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed`.

2. **CS2 Modding Toolkit**, with the `CSII_TOOLPATH` environment variable set. The
   project imports `Mod.props` / `Mod.targets` from this path; without it the project
   will not load in MSBuild.

   The `Makefile` defaults `CSII_TOOLPATH` to:
   `%USERPROFILE%/AppData/LocalLow/Colossal Order/Cities Skylines II/.cache/Modding`.

   On a fresh CS2 install this folder is populated by the toolkit's first-run setup.

3. **Unity mod-project** + Unity Entities version, for the source-generators that
   emit `*NullObject.g.cs`. The CS2 Analyzers project runs Roslyn source-generators
   at compile time that reference Unity types (`Unity.Entities.Entity`,
   `Unity.Mathematics.float3`, `Colossal.Core.*`, etc.). Without a Unity mod-project
   whose `Library/PackageCache/` is populated, the build fails with `CS0246` for those
   types.

   Required env vars (in addition to the ones above):
   - `CSII_UNITYMODPROJECTPATH` — absolute path to a Unity project with `Packages/manifest.json`
     containing `com.unity.entities`. The canonical template that ships with CS2 lives at:
     `C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Content/Game/.ModdingToolchain/UnityModsProject.zip` — extract it once and set
     `CSII_UNITYMODPROJECTPATH=<extract-path>`.
   - `CSII_ENTITIESVERSION` — exact `com.unity.entities@<version>` (e.g. `1.3.10`) to verify
     the Unity project's `Library/PackageCache` matches what `Mod.targets` expects.
   - `CSII_MODPOSTPROCESSORPATH` — directory containing `ModPostProcessor.exe` (the post-build
     tool that injects Unity ECS source-generator code into the compiled DLL). Auto-resolved
     to: `Cities2_Data/Content/Game/.ModdingToolchain/ModPostProcessor`.

   Open the Unity project once in Unity Editor (`Unity.exe -projectPath ... -quit`) to
   populate `Library/PackageCache/`. Subsequent builds will work until PackageCache is
   cleared.

4. **Python and Node.js** — the prebuild step runs code generators and contract
   checks (`scripts/generate.py`, `scripts/contract_check.py`,
   `Tools/generate-binding-manifest.js`, and others). These run automatically before
   compilation and will fail the build if generated artifacts are stale.

5. **UI toolchain** — Node.js with the UI's `npm` dependencies; the UI is built with
   webpack and type-checked / linted as part of the build.

## Burst is optional

The mod has a single Burst switch, `EnableCivicBurst` (in `CivicSurvival.csproj`).

- With Burst **off** (`EnableCivicBurst=false`), jobs run as managed IL and
  **Unity.Logging is not required at all** — its reference and source generator are
  gated on `EnableCivicBurst=true`, and Burst logging is behind `#if ENABLE_BURST`.
  This is the simpler configuration.
- With Burst **on** (`EnableCivicBurst=true`), the build additionally needs the
  Unity.Logging binaries (`Unity.Logging.dll`, `LoggingCommon.dll`,
  `MainLoggingGenerator.dll`). **These are not included in this repository.** They are
  Player-compiled from `com.unity.logging@1.2.1` using a local Unity Editor. Obtaining
  them is an extra step only relevant if you specifically want the Burst-compiled
  performance path.

## Why the public snapshot does not fully compile

The project relies on a private set of **Roslyn source generators** (part of the
`CivicSurvival.Analyzers` project) that are **not published** in this snapshot.
Several of these generators emit code the client needs at compile time, so without
them the client will not compile completely.

The public snapshot therefore has the `ProjectReference` to `CivicSurvival.Analyzers`
(and its analyzer-only `AdditionalFiles`) removed from the public `.csproj` as a
cosmetic measure — so the project does not reference a project that isn't here. The
reference to `CivicSurvival.Contracts` is kept (those wire contracts are published and
the client needs them).

This is intentional: the goal of this repository is **readable, auditable code**, not
a reproducible third-party build. The author's own store releases are built from the
private repository, which has the generators.
