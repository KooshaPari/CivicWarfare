# AgilePlus registration snapshot (2026-09-05)

This snapshot records the first supported lifecycle registration of the Civic
warfare program in the canonical AgilePlus core. It is operational evidence,
not a WP01 acceptance record.

## Commands and resulting state

```text
agileplus --db <canonical core.db> --repo <CivicSurvival-public> specify \
  --feature civic-warfare-program \
  --from-file .agileplus/civic-warfare-program/spec.md \
  --target-branch main
agileplus --db <canonical core.db> --repo <CivicSurvival-public> plan \
  --feature civic-warfare-program --max-wps 20
```

The canonical MCP endpoint at `127.0.0.1:8765/mcp` reports the feature in
`planned` state and exposes 20 planned work packages through
`get_work_packages`. Its audit chain verifies as valid with two Civic entries:

- `Created -> Specified`
- `Researched -> Planned`

The `get_feature` summary currently reports `wp_count: 0` and `wp_done: 0`
while the work-package query returns 20 rows. This is a control-plane summary
defect tracked separately; it does not change the underlying WP rows.

## Gate interpretation

`check_governance` currently returns no violations, but that is not evidence
that WP01 passed: no licensed CS2 build, launch smoke, artifact provenance,
canonical evidence receipt, or independent human approval has been recorded.
The feature must remain `planned`/NO-GO until those gates are satisfied through
an evidence-aware, auditable path.
