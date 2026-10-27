"""Failure types shared by the Dependency Delta scanner modules."""

from __future__ import annotations


class DependencyDeltaError(RuntimeError):
    """A changed dependency surface could not be scanned safely.

    Every module in the scanner raises this for a condition that must fail
    the gate: a report that cannot be parsed, a scanner that crashed, or a
    dependency change that no scanner covers. Failing closed is the only
    safe behaviour, because a silently skipped scan is indistinguishable
    from a clean dependency tree.
    """
