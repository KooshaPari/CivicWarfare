# WP01 Manifest Reconciliation (2026-09-05)

## Scope

The checked-in `civic-wp01-evidence-manifest.json` previously described
`CivicSurvival.Analyzers` as private and absent, recorded a 261-error adapter
build under that explanation, and used legacy `id` fields that the current
`scripts/verify_wp01_evidence.py` does not accept. Those claims are historical
and are not current proof.

## Current evidence

- `CivicSurvival.Analyzers/` is tracked in the public repository.
- `CivicSurvival/CivicSurvival.csproj` references it as an analyzer-only project
  (`ReferenceOutputAssembly="false"`, `OutputItemType="Analyzer"`).
- `dotnet build CivicSurvival.Analyzers/CivicSurvival.Analyzers.csproj --nologo`
  succeeds with warnings and zero errors on the reconciliation host.
- The current checkout has no licensed CS2 installation or managed game
  assemblies, so no adapter build, launch smoke, or artifact provenance is
  claimed here.

## Preservation and gate state

The historical manifest data is preserved in Git history. The root manifest is
now an explicit pending bundle using the verifier's current field names and
keeps WP01 `CONDITIONAL_NO_GO`. Its `subject.commit` must be replaced with the
exact immutable successor commit, together with the external host artifacts,
before running the verifier for acceptance. This reconciliation does not
advance WP01 or authorize WP02/gameplay implementation.
