# CivicWarfare — Master Audit (2026-10-17)

## IDENTITY
Distributed Cities: Skylines II infrastructure-survival mod (closed parent distributes; public mirror `CivicSurvival-public` is the player-facing docs + client source for transparency).

## INTENT
Player can install CS2 mod, play the crisis-survival loop (grid, drones, diplomacy, refugees), save/reload across versions, and uninstall cleanly — backed by authentic evidence and real producer provenance.

## CURRENT STATE
Public repo at HEAD `6b43663` on branch `fix/civic-installer-file-size-20260904` with 21 dirty modified files (no clean checkpoint). 1,380-file Unity-mod source mirror (~251k LOC) with no buildable distribution in this repo; server side lives in a separate closed repo. README marks "Early Access Beta", "Saves are not version-stable yet". Three intentional mock/synthetic patterns ship in the released mirror.
Canonical `STATE.md` flags this snapshot as `LOCAL_AND_BRANCH_PROGRESS_UNKNOWN` with sampled upstream commit `f300fe9` (2026-09-12); local HEAD appears newer.

## CODE REALITY
- Public source mirror is real and substantial, but the buildable artifact ships from a separate closed repo — public repo is documentation + transparency surface, not the distribution root.
- Player-facing UI exposes a dimmed mock-up for an unregistered feature: `CivicSurvival/Core/Infrastructure/FeatureRegistry.cs:216` (explicit "intentionally NOT registered; the UI renders a dimmed mock-up").
- Telemetry auth and global news fall back to a synthetic "Mayor_XXXX" identity when the auth server is unreachable: `CivicSurvival/Core/Services/TelemetryAuth.cs:189` and `CivicSurvival/Domains/Network/Systems/GlobalNewsSystem.cs:257`.
- Leaderboard service provides mock data when no arena server is reachable: `CivicSurvival/Services/Arena/ArenaLeaderboardSystem.cs:302`.
- Local working tree has 21 modified files across ROADMAP, security, sessions, installer with no checkpoint — open owner must reconcile before shipping.
- `install/Program.cs` and `Program.Metadata.cs` are dirty in the working tree (visible in `git status --short`); these are the only public-buildable installer artifacts in this repo.

## STUB/MOCK/MVP-LAZINESS INVENTORY
- `CivicSurvival/Core/Infrastructure/FeatureRegistry.cs:216` — explicit dimmed mock-up UI for an unregistered feature
- `CivicSurvival/Core/Services/TelemetryAuth.cs:189` — synthetic "Mayor_XXXX" identity fallback
- `CivicSurvival/Domains/Network/Systems/GlobalNewsSystem.cs:257` — synthetic "Mayor_XXXX" display for never-set player
- `CivicSurvival/Services/Arena/ArenaLeaderboardSystem.cs:302` — mock leaderboard data when server unreachable
- `ROADMAP.md` — multiple Tier B docs unchecked; "0.4.x — Domain depth & telemetry" describes wiring the existing telemetry pipeline into the in-game dashboard as the next big lift
- No Paradox Mods build receipt / no install-via-this-repo path documented; distribution runs via closed parent
- README explicit "Saves are not version-stable yet" — save/version mismatch invariant not yet met (DOSSIER §5 negative control)
- Working tree has 21 modified files uncommitted (CODE_OF_CONDUCT.md, csproj, ROADMAP, SECURITY, installer Program.cs/Metadata.cs, docs-site deploy.md, sessions, snapshots) — no clean checkpoint on the active branch

## FWD PATH TO FINISH
1. Reconcile dirty working tree: `git diff` the 21 modified files, decide keep/revert, commit on `fix/civic-installer-file-size-20260904`.
2. Replace the three mock patterns (FeatureRegistry:216 dimmed-mock, ArenaLeaderboardSystem:302, Mayor_XXXX fallbacks) with honest failure UI — "live data unavailable" not placeholder names; gate behind feature flag.
3. Land save-format version-stamp in serialization generators (`CivicSurvival.Analyzers/Generators/SerializeResetPersistGenerator.cs:168`) so saves fail loud across versions instead of silently corrupting.
4. Wire `Domains/Telemetry/Systems/` per ROADMAP 0.4.x into the in-game dashboard using existing `TelemetryPayloadReader.g.cs` / writer pairs.
5. Capture real install-evidence: Paradox Mods page evidence, fresh CS2 install run, save/reload VHS, then update `civic-wp01-evidence-manifest.json`.
6. Apply DOSSIER §5 negative-control cases: undeclared private dep, telemetry without consent, misleading UI, save/version mismatch, installer mutating unowned files, perf-under-scale — each must fail loud.

## TIME-TO-FINISH ESTIMATE
- Tight MVP (mock removal + checkpoint): ~24-32 hours focused work
- Honest finish-line (steps 1-5 incl. install evidence): ~60-90 hours solo; ~3-5 working days with a 3-worker swarm (worker owns a step each, integration owner stitches). Tier-2 design review adds ~4-6 hours.

## SUNSET/KEEP CALL
**KEEP with conditions.** Public mirror is a legitimate transparency surface and the parent product ships to real players (Paradox Mods listing exists). Do NOT sunset. Block all new feature work until (a) working tree is reconciled, (b) three mock/synthetic fallbacks are replaced with honest UI, (c) save versioning is enforced.

## CONFIDENCE
Medium. Verified via direct read of `AGENT-PROMPT.md`, `DOSSIER.md`, `START-HERE.md`, `STATE.md`, plus local repo `git log`, `git status`, grep of stubs/mocks across the C# tree. Did not run installer or game. Did not query the closed parent repo. Canonical STATE.md sampled upstream commit `f300fe9` (2026-09-12) is older than local branch tip `6b43663` — revision drift suspected between bound sampled upstream and local working tree.

## OUT-OF-SCOPE / NOT-INSPECTED
- Closed-source producer repo (server, Paradox Mods build pipeline, business logic) — not inspected; this audit only sees the public mirror
- `installer/bin/` and `installer/obj/` (build outputs) — not opened
- Historical git refs behind this branch (only `git log -5` and `git status` consulted)
- Behavioral / runtime path: did not boot CS2 or load the mod, did not run the installer
- Test/lint/coverage status: not measured; scorecard baseline file (`.github/scorecard-baseline.json`) referenced but not parsed
