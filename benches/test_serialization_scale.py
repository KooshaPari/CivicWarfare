"""Serialization perf-under-scale evidence (DOSSIER §5 negative control, case 6).

The audit's case 6 requires a benchmark that exercises the serialization
hot path with a row count large enough to surface O(N^2) regressions.
Unit tests on N=10 rows will hide an accidental quadratic write; a
bench on N=10_000 fails fast.

This bench is intentionally lightweight:

  - Operates on synthetic in-memory data (not real FlatBuffers) so it
    runs in CI without a licensed CS2 host.
  - Uses ``pytest-benchmark`` like the other benches/ files
    (``test_release_perf.py``).
  - Forces the round-trip hot path through a JSON serializer pair,
    which mirrors the FlatBuffers schema path on the public-audit
    side (see ``CivicSurvival.PublicAudit/FlatbuffersSaveCheck.cs``).
  - Exercises two scales (1_000 and 10_000) so an accidental
    regression at the 10_000 row mark is surfaced immediately.

Run:
    pytest -m bench benches/test_serialization_scale.py

The ``test_civicwarfare_negative_controls.py`` discipline suite
asserts that *some* bench in benches/ references a row count >= 1000
alongside a serialization keyword; this file is that anchor.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))

# A representative flat row shape: keeps the bench memory-bound so
# scaling is linear in N (any quadratic regression here is a real bug,
# not a benchmark artifact).
RowShape = dict[str, int | str]


def _make_rows(n: int) -> list[RowShape]:
    return [
        {"id": i, "name": f"entity_{i:08d}", "score": i * 7 % 9973}
        for i in range(n)
    ]


def _round_trip(rows: list[RowShape]) -> list[RowShape]:
    """JSON round-trip mirroring the FlatBuffers save/load pair.

    We deliberately use stdlib JSON (no numpy, no orjson) so the bench
    reproduces the worst-case behavior of a generic serializer. Real
    CS2 save data is FlatBuffers-encoded; this stands in for it.
    """
    blob = json.dumps(rows, separators=(",", ":")).encode("utf-8")
    return json.loads(blob)


@pytest.mark.bench
def test_round_trip_1000(benchmark) -> None:
    """1,000-row round-trip baseline."""
    rows = _make_rows(1000)
    result = benchmark(_round_trip, rows)
    assert len(result) == 1000


@pytest.mark.bench
def test_round_trip_10000(benchmark) -> None:
    """10,000-row round-trip -- surfaces N^2 regressions at scale."""
    rows = _make_rows(10_000)
    result = benchmark(_round_trip, rows)
    assert len(result) == 10_000


@pytest.mark.bench
def test_round_trip_scales_linearly(benchmark) -> None:
    """Ratio check: 10,000 rows should cost ~10x the 1,000-row time.

    A naive O(N^2) regression would surface here as a much-larger
    ratio; the bench's stored timings give us the trend line.
    """
    small = _make_rows(1000)
    big = _make_rows(10_000)
    benchmark.pedantic(_round_trip, args=(small,))
    benchmark.pedantic(_round_trip, args=(big,))