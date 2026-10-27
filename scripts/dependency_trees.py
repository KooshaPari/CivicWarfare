"""Materialisation of the base and head dependency trees for comparison.

The advisory-delta comparison audits two trees: the commit the gate was
asked about, and the commit it is compared against. Both have to come from
git rather than from the working tree, because a checkout that is ahead of,
behind from, or otherwise different from the requested commit makes the
delta meaningless.

The two trees must also agree on registry configuration. An audit run
against a different registry can report a different advisory set for the
same lockfile, so the registry configuration is copied into both.
"""

from __future__ import annotations

import shutil
import subprocess
import tempfile
from pathlib import Path

from errors import DependencyDeltaError
from scan_types import Runner, ScanCommand

# The manifests and registry configuration that must be identical between
# the two audited trees for their advisory sets to be comparable.
_TREE_FILES = (
    "package.json",
    "package-lock.json",
    "npm-shrinkwrap.json",
    # Registry/auth configuration must be IDENTICAL between the two
    # audits, or they are not comparable: a different registry can
    # report a different advisory set for the same lockfile.
    ".npmrc",
)


def repo_root(start: Path) -> Path:
    """Walk up from *start* to the directory that contains .git."""
    current = start.resolve()
    for candidate in (current, *current.parents):
        if (candidate / ".git").exists():
            return candidate
    return current


def materialise_base_tree(scan: ScanCommand, base_ref: str) -> Path:
    """Materialise the BASE manifest + lockfile into a temp dir for auditing."""
    staging = Path(tempfile.mkdtemp(prefix="dependency-delta-base-"))
    root = repo_root(scan.cwd)
    for name in (
        "package.json",
        "package-lock.json",
        "npm-shrinkwrap.json",
        # Registry/auth configuration must be IDENTICAL between the two
        # audits, or they are not comparable: a different registry can
        # report a different advisory set for the same lockfile.
        ".npmrc",
    ):
        source = scan.cwd / name
        # The base tree is authoritative for the base audit even when the
        # head deleted the manifest. Falling through on a missing working
        # tree file would drop the base lockfile and compare against
        # nothing, so the base blob is located through git instead.
        try:
            relative = source.resolve().relative_to(root.resolve())
        except ValueError:
            if name == ".npmrc" and source.exists():
                (staging / name).write_bytes(source.read_bytes())
            continue
        # Read the BASE version of the file straight from git: the working
        # tree may already contain the head's lockfile, and the base may
        # have had no lockfile at all.
        blob = subprocess.run(
            ("git", "show", f"{base_ref}:{relative.as_posix()}"),
            cwd=root,
            check=False,
            capture_output=True,
        )
        if blob.returncode != 0:
            # Not tracked at the base ref (e.g. a local .npmrc). Fall back to
            # the working-tree copy so both audits use the same registry.
            if name == ".npmrc" and source.exists():
                (staging / name).write_bytes(source.read_bytes())
            continue
        (staging / name).write_bytes(blob.stdout)
    return staging


def materialise_head_tree(scan: ScanCommand, head_ref: str) -> Path:
    """Materialise the HEAD manifest + lockfile into a temp dir for auditing.

    Auditing the live checkout is not the same thing as auditing --head: if
    the working tree is ahead of, behind, or otherwise different from the
    commit the gate was asked about, the delta is computed against the wrong
    tree. Materialising the head side removes that class of skew.
    """
    staging = Path(tempfile.mkdtemp(prefix="dependency-delta-head-"))
    root = repo_root(scan.cwd)
    for name in (
        "package.json",
        "package-lock.json",
        "npm-shrinkwrap.json",
        ".npmrc",
    ):
        source = scan.cwd / name
        if not source.exists():
            continue
        try:
            relative = source.resolve().relative_to(root.resolve())
        except ValueError:
            continue
        blob = subprocess.run(
            ("git", "show", f"{head_ref}:{relative.as_posix()}"),
            cwd=root,
            check=False,
            capture_output=True,
        )
        if blob.returncode != 0:
            if name == ".npmrc" and source.exists():
                (staging / name).write_bytes(source.read_bytes())
            continue
        (staging / name).write_bytes(blob.stdout)
    return staging


def run_node_audit(scan: ScanCommand, runner: Runner, base_ref: str | None) -> str:
    """Run the same audit against the BASE tree so the two sets can be compared."""
    if scan.base_cwd is None:
        raise DependencyDeltaError("node scanner has no base tree to compare against")
    if base_ref is None:
        raise DependencyDeltaError("node scanner has no base ref to compare against")
    staging = materialise_base_tree(scan, base_ref)
    try:
        # If the base tree has no lockfile we cannot know which advisories
        # were already present. Comparing against an empty base set would
        # blame the PR for every advisory in the graph, so fail closed
        # instead of guessing.
        if not any(
            (staging / name).exists() for name in ("package-lock.json", "npm-shrinkwrap.json")
        ):
            raise DependencyDeltaError(
                f"node scanner cannot compare against the base tree at {base_ref}: "
                "no package-lock.json or npm-shrinkwrap.json in the base commit"
            )
        base = runner(
            scan.command,
            cwd=staging,
            check=False,
            text=True,
            capture_output=True,
        )
    finally:
        shutil.rmtree(staging, ignore_errors=True)
    if base.returncode not in (0, 1):
        raise DependencyDeltaError(
            f"node scanner failed on the base tree at {base_ref}: {' '.join(scan.command)} "
            f"(exit {base.returncode})"
        )
    return base.stdout
