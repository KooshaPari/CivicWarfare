"""Shared data types for the Dependency Delta scanner modules."""

from __future__ import annotations

import subprocess
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class ScanCommand:
    """A single scanner invocation and the directory it must run in."""

    ecosystem: str
    cwd: Path
    command: tuple[str, ...]
    # Base-side working directory for advisory-delta comparison (node only).
    base_cwd: Path | None = None


Runner = Callable[..., subprocess.CompletedProcess[str]]
