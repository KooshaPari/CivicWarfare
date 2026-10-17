# CivicWarfare FWD Disposition Report (2026-10-17)

## Subject
Branch `fix/civic-installer-file-size-20260904` on `KooshaPari/CivicSurvival-public`.
FWD path items 1.1-1.5 from `docs/audits/2026-10-17/civicwarfare.md` (§FWD PATH TO FINISH).

## Disposition Table

| Step | Title | Status | Evidence | Notes |
|------|-------|--------|----------|-------|
| 1.1 | Reconcile dirty working tree | **SKIPPED** | audit doc §"STUB/MOCK/MVP-LAZINESS INVENTORY" | Branched from a working tree that the parent audit already classified as "21 dirty files uncommitted"; machine redaction is the operator's call, not this FWD. New work does not add to the dirty-file backlog. |
| 1.2 | Replace mock affordances | **SHIPPED** | commit `1751edc` ("fix(civicwarfare-1.2): replace honest-mock affordance fabrications") | PR #107 |
| 1.3 | Save-format version-stamp in serialization generators | **SHIPPED** | commit `1a1b414` ("feat(civicwarfare-1.3): stamp generated serializers with SaveFormatVersion") | PR #107 |
| 1.4 | DOSSIER §5 negative-control tests | **SHIPPED** | this PR (12/12 tests pass; see Verbatim Output below) | New files: `tests/test_civicwarfare_negative_controls.py`, `benches/test_serialization_scale.py` |
| 1.5 | FWD disposition report + manifest update | **SHIPPED** | this report; `civic-wp01-evidence-manifest.json` updated with repo-only verifiable evidence | Install-run fields marked "not capturable in this environment" per honesty rule. |

## 1.4 — Six DOSSIER §5 Negative-Control Cases

Each case is a *failing-loud* test that would catch the regression described in the audit's §"STUB/MOCK/MVP-LAZINESS INVENTORY" if reintroduced.

| # | Case | Test | What it catches |
|---|------|------|-----------------|
| 1 | Undeclared private dep | `test_installer_only_uses_declared_or_bcl_assemblies` | Installer taking a dependency on a private assembly not listed in the installer's `.csproj` or a public BCL package. |
| 2 | Telemetry without consent | `test_telemetry_off_by_default_when_consent_file_missing` + `test_telemetry_effective_flag_requires_opt_in_and_online` | Telemetry running with consent set to anything other than `1`, or with the effective flag flapping on a missing consent file. |
| 3 | Misleading UI | `test_feature_registry_no_dimmed_mock_up_affordance` + `test_feature_registry_documents_honest_locked_path` + `test_arena_leaderboard_does_not_return_mock_rows` | FeatureRegistry re-introducing a dimmed mock-up affordance; leaderboard returning synthetic rows. |
| 4 | Save/version mismatch | `test_save_format_version_is_declared_in_mod_cs` + `test_per_system_serialization_files_reference_a_version_field` | Per-system `.Serialization.cs` files that lack a `SaveFormatVersion` field on the wire format. |
| 5 | Installer mutating unowned files | `test_installer_writes_only_inside_its_own_plugin_dir` + `test_installer_resolves_plugins_root_through_helpers` | Installer performing a `File.*`/`Directory.*` mutation outside its own plugin directory; or resolving the plugins root through a literal that isn't the helper. |
| 6 | Perf-under-scale | `test_serialization_round_trip_has_scale_evidence` + `benches/test_serialization_scale.py` (anchor) | O(N^2) regression in the serialization hot path that would be invisible on N=10 unit tests. |

### Verbatim Output (1.4 verification)

Command: `uv run --quiet --project . pytest tests/test_civicwarfare_negative_controls.py -v`
Result: `12 passed in 0.85s`

```
tests/test_civicwarfare_negative_controls.py::test_installer_only_uses_declared_or_bcl_assemblies PASSED
tests/test_civicwarfare_negative_controls.py::test_telemetry_off_by_default_when_consent_file_missing PASSED
tests/test_civicwarfare_negative_controls.py::test_telemetry_effective_flag_requires_opt_in_and_online PASSED
tests/test_civicwarfare_negative_controls.py::test_feature_registry_no_dimmed_mock_up_affordance PASSED
tests/test_civicwarfare_negative_controls.py::test_feature_registry_documents_honest_locked_path PASSED
tests/test_civicwarfare_negative_controls.py::test_arena_leaderboard_does_not_return_mock_rows PASSED
tests/test_civicwarfare_negative_controls.py::test_save_format_version_is_declared_in_mod_cs PASSED
tests/test_civicwarfare_negative_controls.py::test_per_system_serialization_files_reference_a_version_field PASSED
tests/test_civicwarfare_negative_controls.py::test_installer_writes_only_inside_its_own_plugin_dir PASSED
tests/test_civicwarfare_negative_controls.py::test_installer_resolves_plugins_root_through_helpers PASSED
tests/test_civicwarfare_negative_controls.py::test_serialization_round_trip_has_scale_evidence PASSED
tests/test_civicwarfare_negative_controls.py::test_discipline_suite_includes_load_stress_contract PASSED
```

The pre-existing `test_save_format_discipline.py::test_every_non_legacy_serialization_file_declares_a_version_field` is also failing on this branch (LEGACY_UNVERSIONED_FILES set is incomplete). That is a pre-existing gap, not caused by this FWD; flagged here for the next FWD cycle.

## 1.5 — Evidence Manifest Update

`civic-wp01-evidence-manifest.json` is updated to add two new repo-only verifiable evidence entries:
- `WP01:negative_controls_landed` (status: pass) — anchored to the new test file.
- `WP01:serialization_scale_evidence` (status: pass) — anchored to the new bench file.

The pre-existing entries (`WP01:licensed_adapter_build`, `WP01:launch_smoke`, `WP01:artifact_hash_provenance`, `WP01:agileplus_evidence_record`, `WP01:conditional_go_no_go_pass`) remain `pending` with `detail` updated to "**not capturable in this environment**". This is a deliberate honesty mark — they require a licensed CS2 host, Paradox Mods submission evidence, and a fresh install run that are not available from a repo-only FWD cycle. The `decision.result` stays `CONDITIONAL_NO_GO` for the same reason; nothing in this FWD is allowed to fabricate install-run evidence.

## What This FWD Did NOT Do

- Did not reconcile the 21 dirty files (step 1.1) — owner decision.
- Did not run the installer or boot CS2 — not available in this environment.
- Did not capture Paradox Mods evidence — public-only mirror.
- Did not edit SQLite directly for AgilePlus evidence — must go through the supported API.
- Did not mark `WP01:conditional_go_no_go_pass` as `pass`. That requires an independent review and all preceding evidence, both out of scope for this FWD.

## What the Next FWD Cycle Should Pick Up

1. Reconcile 1.1 (owner decision on 21 dirty files).
2. Complete the LEGACY_UNVERSIONED_FILES allowlist in `tests/test_save_format_discipline.py` so the pre-existing per-system version-field test goes green alongside the new ones.
3. Capture a real licensed-host evidence bundle for items `WP01:licensed_adapter_build`/`launch_smoke`/`artifact_hash_provenance` so the `decision.result` can move off `CONDITIONAL_NO_GO`.
