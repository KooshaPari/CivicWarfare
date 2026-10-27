"""Fail-closed dependency scanners for changed Node and .NET manifests."""

from __future__ import annotations

import argparse
import fnmatch
import os
import subprocess
import sys
from collections.abc import Callable, Iterable, Sequence
from pathlib import Path, PurePosixPath, PureWindowsPath

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from audit_reports import (
    dotnet_vulnerability_findings,
    node_advisory_ids,
)
from csproj_diff import is_metadata_only_csproj_diff
from dependency_trees import (
    materialise_base_tree,
    materialise_head_tree,
    repo_root,
    run_node_audit,
)
from errors import DependencyDeltaError
from scan_types import Runner, ScanCommand

# The parsers live in their own module, but the scanner module keeps the
# private aliases it has always exposed so that callers and tests can
# reach them through this module.
_dotnet_vulnerability_findings = dotnet_vulnerability_findings
_node_advisory_ids = node_advisory_ids
_materialise_base_tree = materialise_base_tree
_materialise_head_tree = materialise_head_tree


def _relative(path: str) -> Path:
    if not path or "\0" in path:
        raise DependencyDeltaError(
            f"unsafe changed path {path!r}: path must be non-empty and NUL-free"
        )
    parts = path.split("/")
    if (
        PurePosixPath(path).is_absolute()
        or PureWindowsPath(path).is_absolute()
        or any(part in {"", ".", ".."} for part in parts)
    ):
        raise DependencyDeltaError(
            f"unsafe changed path {path!r}: expected a normalized repository-relative path without traversal"
        )
    return Path(*parts)


def _repo_path(repo: Path, changed_path: str) -> Path:
    repo_root = repo.resolve()
    candidate = (repo_root / _relative(changed_path)).resolve(strict=False)
    try:
        candidate.relative_to(repo_root)
    except ValueError as error:
        raise DependencyDeltaError(
            f"unsafe changed path {changed_path!r}: resolved path escapes repository root"
        ) from error
    return candidate


def _node_lockfile(manifest_dir: Path) -> Path | None:
    for name in ("npm-shrinkwrap.json", "package-lock.json"):
        lockfile = manifest_dir / name
        if lockfile.exists():
            return lockfile
    return None


def _node_plan(repo: Path, changed_path: str) -> ScanCommand:
    manifest_dir = _repo_path(repo, changed_path).parent
    package_json = manifest_dir / "package.json"
    if not package_json.exists() or _node_lockfile(manifest_dir) is None:
        raise DependencyDeltaError(
            f"{changed_path}: node: add/update npm-shrinkwrap.json or package-lock.json "
            "alongside package.json so npm audit can run with a reproducible lockfile"
        )
    return ScanCommand(
        ecosystem="node",
        cwd=manifest_dir,
        command=("npm", "audit", "--package-lock-only", "--audit-level=low", "--json"),
        base_cwd=manifest_dir,
    )


def _csharp_plan(repo: Path, changed_path: str) -> list[ScanCommand]:
    solution = repo / "CivicSurvival.sln"
    changed = _repo_path(repo, changed_path)
    lockfile = (
        changed if changed.name == "packages.lock.json" else changed.parent / "packages.lock.json"
    )
    if not solution.exists() or not lockfile.exists():
        raise DependencyDeltaError(
            f"{changed_path}: csharp: add packages.lock.json and keep CivicSurvival.sln "
            "available so restore can run in --locked-mode"
        )
    return [
        ScanCommand("csharp", repo, ("dotnet", "restore", "CivicSurvival.sln", "--locked-mode")),
        ScanCommand(
            "csharp",
            repo,
            (
                "dotnet",
                "list",
                "CivicSurvival.sln",
                "package",
                "--vulnerable",
                "--include-transitive",
                "--format",
                "json",
                "--output-version",
                "1",
            ),
        ),
    ]


def _unsupported(changed_path: str, ecosystem: str, remedy: str) -> DependencyDeltaError:
    return DependencyDeltaError(f"{changed_path}: {ecosystem}: {remedy}")


def _parse_civicignore(text: str) -> list[str]:
    """Return the ignore patterns contained in *text*.

    Blank lines and ``#`` comments are discarded.
    """
    patterns: list[str] = []
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        patterns.append(line)
    return patterns


def _load_civicignore(repo: Path, base_ref: str | None = None) -> list[str]:
    """Load ignore patterns for the repository.

    When *base_ref* is given the patterns are read from the BASE commit
    rather than the working tree. The working tree holds the pull request's
    own head, so a PR that added a catch-all pattern to ``.civicignore``
    would otherwise exempt its own changed files from the scan it is
    supposed to pass. The base commit is the only version of the ignore
    list that a pull request cannot influence.

    When *base_ref* is ``None`` no patterns are honoured at all: failing
    closed is the only safe reading when the base is unknown.
    """
    if base_ref is None:
        return []
    root = repo_root(repo)
    blob = subprocess.run(
        ("git", "show", f"{base_ref}:.civicignore"),
        cwd=root,
        check=False,
        capture_output=True,
    )
    if blob.returncode != 0:
        return []
    return _parse_civicignore(blob.stdout.decode("utf-8", errors="replace"))


def _is_ignored(path: str, patterns: list[str]) -> bool:
    """Return True if *path* matches any of the ignore *patterns*.

    Patterns are matched using fnmatch (case-sensitive) against the
    full repository-relative path.
    """
    return any(fnmatch.fnmatch(path, pat) for pat in patterns)


def build_scan_plan(
    repo: Path,
    changed_files: Iterable[str],
    *,
    diff_provider: Callable[[Path, str], str] | None = None,
    base_ref: str | None = None,
) -> list[ScanCommand]:
    """Create scanner commands, rejecting changed dependency formats without a scanner.

    *diff_provider(repo, changed_path)* optionally returns the unified diff text
    for a single changed file.  When provided, .csproj changes that touch ONLY
    metadata elements (e.g. ``<Version>``) are skipped because they do not alter
    the resolved package graph.

    Changed files matching patterns in ``.civicignore`` are silently skipped.
    The patterns come from the *base_ref* commit when one is supplied, so a
    pull request cannot exempt its own changes by editing the ignore list.
    """
    plan: list[ScanCommand] = []
    seen: set[tuple[str, Path]] = set()
    csharp_added = False
    civicignore = _load_civicignore(repo, base_ref)

    for changed_path in sorted(set(changed_files)):
        if civicignore and _is_ignored(changed_path, civicignore):
            continue
        name = _relative(changed_path).name
        if name in {"package.json", "package-lock.json", "npm-shrinkwrap.json"}:
            command = _node_plan(repo, changed_path)
            key = (command.ecosystem, command.cwd)
            if key not in seen:
                plan.append(command)
                seen.add(key)
        elif name.endswith(".csproj") or name == "packages.lock.json":
            if diff_provider is not None and name.endswith(".csproj"):
                diff_text = diff_provider(repo, changed_path)
                if is_metadata_only_csproj_diff(diff_text):
                    continue
            if not csharp_added:
                plan.extend(_csharp_plan(repo, changed_path))
                csharp_added = True
        elif name in {"Cargo.toml", "Cargo.lock"}:
            raise _unsupported(
                changed_path,
                "rust",
                "add a supported Rust dependency scanner before changing this manifest",
            )
        elif name in {"go.mod", "go.sum"}:
            raise _unsupported(
                changed_path,
                "go",
                "add a supported Go dependency scanner before changing this manifest",
            )
        elif name in {
            "pyproject.toml",
            "setup.py",
            "requirements.txt",
            "Pipfile.lock",
            "poetry.lock",
        }:
            raise _unsupported(
                changed_path,
                "python",
                "add a supported Python dependency scanner before changing this manifest",
            )
        elif name in {"yarn.lock", "pnpm-lock.yaml"}:
            raise _unsupported(
                changed_path,
                "node",
                "use package-lock.json or add a scanner for this lockfile format",
            )
    return plan


def _is_node_audit(scan: ScanCommand) -> bool:
    return scan.ecosystem == "node" and "audit" in scan.command


def _is_dotnet_vulnerability_scan(scan: ScanCommand) -> bool:
    return scan.ecosystem == "csharp" and "--vulnerable" in scan.command


def _is_dotnet_restore(scan: ScanCommand) -> bool:
    return scan.ecosystem == "csharp" and "--locked-mode" in scan.command


def run_scan_plan(
    plan: Sequence[ScanCommand],
    runner: Runner = subprocess.run,
    base_ref: str | None = None,
    head_ref: str | None = None,
) -> None:
    """Execute every scanner and turn any nonzero result into a gate failure.

    *base_ref* enables advisory-delta comparison for node scans: a PR is only
    blamed for advisories it actually introduced. *head_ref* pins which commit
    the head side of that comparison is taken from.
    """
    for scan in plan:
        capture_output = _is_dotnet_vulnerability_scan(scan) or _is_node_audit(scan)
        try:
            result = runner(
                scan.command,
                cwd=scan.cwd,
                check=False,
                text=True,
                capture_output=capture_output,
            )
        except OSError as error:
            raise DependencyDeltaError(
                f"{scan.ecosystem} scanner could not start in {scan.cwd}: {' '.join(scan.command)}: {error}"
            ) from error
        if result.returncode != 0 and not _is_node_audit(scan):
            # A failed restore must fail the gate closed. Tolerating it lets
            # the PR pass with NO vulnerability scan having run at all, which
            # is indistinguishable from "the PR is clean". The CS2 Modding
            # Toolkit project that needs a local CS2 install is already
            # excluded through .civicignore, so the exemption is redundant.
            raise DependencyDeltaError(
                f"{scan.ecosystem} scanner failed in {scan.cwd}: {' '.join(scan.command)} "
                f"(exit {result.returncode})"
            )
        if _is_node_audit(scan):
            # npm audit exits 1 whenever ANY advisory exists in the graph, and
            # 0 when it finds none. Judge the DELTA in BOTH cases: a new
            # advisory id is a failure, an unchanged pre-existing advisory is
            # not this PR's fault, and an audit that cannot be compared
            # against a base tree fails closed rather than guessing.
            #
            # The head side is materialised from --head, NOT taken from the
            # live checkout: the working tree may differ from the commit
            # the gate was asked about, and auditing the wrong tree makes
            # the delta meaningless.
            head_staging = materialise_head_tree(scan, head_ref) if head_ref else None
            try:
                if head_staging is not None:
                    head_result = runner(
                        scan.command,
                        cwd=head_staging,
                        check=False,
                        text=True,
                        capture_output=True,
                    )
                    head_stdout = head_result.stdout
                else:
                    head_result = result
                    head_stdout = result.stdout
            finally:
                if head_staging is not None:
                    import shutil

                    shutil.rmtree(head_staging, ignore_errors=True)
            # npm audit legitimately exits 0 (no advisories) and 1 (advisories
            # found). Any other exit code means the audit itself crashed, and
            # a crashed audit must never be read as "no advisories": that
            # would silently pass a PR whose dependency tree was never
            # scanned. The base side already fails closed this way.
            if head_result.returncode not in (0, 1):
                raise DependencyDeltaError(
                    f"node scanner failed on the head tree at "
                    f"{head_ref or 'the working tree'}: {' '.join(scan.command)} "
                    f"(exit {head_result.returncode})"
                )
            head_ids = _node_advisory_ids(head_stdout)
            base_ids = _node_advisory_ids(run_node_audit(scan, runner, base_ref))
            introduced = head_ids - base_ids
            if introduced:
                raise DependencyDeltaError(
                    "node dependency scan introduced new advisories:\n"
                    + "\n".join(sorted(introduced))
                )
            continue
        if _is_dotnet_vulnerability_scan(scan):
            findings = _dotnet_vulnerability_findings(result.stdout)
            if findings:
                raise DependencyDeltaError(
                    "dotnet vulnerability scan found vulnerable packages:\n" + "\n".join(findings)
                )


def changed_paths(repo: Path, base: str, head: str, runner: Runner = subprocess.run) -> list[str]:
    result = runner(
        ("git", "diff", "--name-only", "-z", "--diff-filter=ACMRD", base, head),
        cwd=repo,
        check=False,
        text=False,
        capture_output=True,
    )
    if result.returncode != 0:
        stderr = result.stderr
        if isinstance(stderr, bytes):
            stderr = stderr.decode("utf-8", errors="replace")
        raise DependencyDeltaError(
            f"git could not determine changed dependency manifests between {base} and {head}: "
            f"{str(stderr).strip()}"
        )
    if not isinstance(result.stdout, bytes):
        raise DependencyDeltaError("malformed NUL-delimited git diff output: expected bytes")
    if not result.stdout:
        return []
    if not result.stdout.endswith(b"\0"):
        raise DependencyDeltaError("malformed NUL-delimited git diff output: missing final NUL")

    raw_paths = result.stdout[:-1].split(b"\0")
    if any(not raw_path for raw_path in raw_paths):
        raise DependencyDeltaError("malformed NUL-delimited git diff output: empty path")
    paths = [os.fsdecode(raw_path) for raw_path in raw_paths]
    for path in paths:
        _repo_path(repo, path)
    return paths


def _file_diff(repo: Path, base: str, head: str, changed_path: str) -> str:
    """Return the unified diff of *changed_path* between *base* and *head*.

    The revisions must precede the ``--`` pathspec separator. Placing the
    separator first makes git read *changed_path* as a pathspec and *base*
    and *head* as further pathspec entries, which yields an empty diff and
    silently disables the csproj metadata-only exemption.
    """
    result = subprocess.run(
        (
            "git",
            "diff",
            "--no-color",
            "--unified=0",
            base,
            head,
            "--",
            changed_path,
        ),
        cwd=repo,
        check=False,
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        raise DependencyDeltaError(
            f"git could not produce diff for {changed_path!r} between {base} and {head}: "
            f"{result.stderr.strip()}"
        )
    return result.stdout


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, help="Git base commit")
    parser.add_argument("--head", required=True, help="Git head commit")
    parser.add_argument("--repo", type=Path, default=Path.cwd(), help="Repository root")
    args = parser.parse_args()

    try:

        def diff_provider(repo, path):
            return _file_diff(repo, args.base, args.head, path)

        plan = build_scan_plan(
            args.repo,
            changed_paths(args.repo, args.base, args.head),
            diff_provider=diff_provider,
            base_ref=args.base,
        )
        if not plan:
            print("No supported dependency manifest changes detected.")
            return 0
        for scan in plan:
            print(f"Running {scan.ecosystem} dependency scan: {' '.join(scan.command)}")
        run_scan_plan(plan, base_ref=args.base, head_ref=args.head)
    except DependencyDeltaError as error:
        print(f"Dependency Delta failed: {error}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
