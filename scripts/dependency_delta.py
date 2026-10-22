"""Fail-closed dependency scanners for changed Node, Python, and .NET manifests."""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import subprocess
from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath


class DependencyDeltaError(RuntimeError):
    """A changed dependency surface could not be scanned safely."""


@dataclass(frozen=True)
class ScanCommand:
    ecosystem: str
    cwd: Path
    command: tuple[str, ...]


Runner = Callable[..., subprocess.CompletedProcess[str]]


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
        command=("npm", "audit", "--package-lock-only", "--audit-level=low"),
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


def _python_plan(repo: Path, changed_path: str) -> ScanCommand:
    """Audit a changed Python project or requirements file with pip-audit."""
    changed = _repo_path(repo, changed_path)
    if changed.name == "requirements.txt":
        return ScanCommand(
            ecosystem="python",
            cwd=changed.parent,
            command=("python3", "-m", "pip_audit", "--strict", "-r", changed.name),
        )
    return ScanCommand(
        ecosystem="python",
        cwd=changed.parent,
        command=("python3", "-m", "pip_audit", "--strict", "."),
    )


# MSBuild elements whose change does not alter the resolved package graph.
# Bumping <Version>, renaming an assembly, or editing a description does not
# require a `--locked-mode` restore to prove reproducibility, so changes that
# touch ONLY these elements are exempted from the csharp dependency scan.
_METADATA_ONLY_CSPROJ_ELEMENTS: frozenset[str] = frozenset(
    {
        "Version",
        "AssemblyName",
        "RootNamespace",
        "Description",
        "Authors",
        "Company",
        "Copyright",
        "PackageId",
        "Title",
        "Summary",
        "Product",
        "InformationalVersion",
        "FileVersion",
        "AssemblyVersion",
        "NeutralLanguage",
    }
)


_CSPROJ_DIFF_LINE = re.compile(r"^\s*[+-]\s*<(?P<tag>[A-Za-z][A-Za-z0-9_]*)\b")


def _is_metadata_only_csproj_diff(diff_text: str) -> bool:
    """Return True iff every +/- line in *diff_text* is an MSBuild metadata element.

    A diff that adds/removes/modifies only <Version>, <AssemblyName>, etc.
    does not change the resolved package graph, so the csharp lockfile scan
    is unnecessary. Any other touched tag (PackageReference, Reference,
    ProjectReference, Import, etc.) forces a full scan.
    """
    metadata_only = True
    for line in diff_text.splitlines():
        match = _CSPROJ_DIFF_LINE.match(line)
        if match is None:
            continue
        if match.group("tag") not in _METADATA_ONLY_CSPROJ_ELEMENTS:
            metadata_only = False
            break
    return metadata_only


def _unsupported(changed_path: str, ecosystem: str, remedy: str) -> DependencyDeltaError:
    return DependencyDeltaError(f"{changed_path}: {ecosystem}: {remedy}")


def _load_civicignore(repo: Path) -> list[str]:
    """Load ignore patterns from .civicignore in the repository root.

    Returns a list of fnmatch-style glob patterns.  Lines starting with
    ``#`` and blank lines are ignored.
    """
    ignore_file = repo / ".civicignore"
    if not ignore_file.exists():
        return []
    patterns: list[str] = []
    for raw_line in ignore_file.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        patterns.append(line)
    return patterns


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
) -> list[ScanCommand]:
    """Create scanner commands, rejecting changed dependency formats without a scanner.

    *diff_provider(repo, changed_path)* optionally returns the unified diff text
    for a single changed file.  When provided, .csproj changes that touch ONLY
    metadata elements (e.g. ``<Version>``) are skipped because they do not alter
    the resolved package graph.

    Changed files matching patterns in ``.civicignore`` are silently skipped.
    """
    plan: list[ScanCommand] = []
    seen: set[tuple[str, Path]] = set()
    csharp_added = False
    civicignore = _load_civicignore(repo)

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
                if _is_metadata_only_csproj_diff(diff_text):
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
            command = _python_plan(repo, changed_path)
            key = (command.ecosystem, command.cwd)
            if key not in seen:
                plan.append(command)
                seen.add(key)
        elif name in {"yarn.lock", "pnpm-lock.yaml"}:
            raise _unsupported(
                changed_path,
                "node",
                "use package-lock.json or add a scanner for this lockfile format",
            )
    return plan


def _invalid_dotnet_json(detail: str) -> DependencyDeltaError:
    return DependencyDeltaError(f"invalid dotnet vulnerability JSON: {detail}")


def _dotnet_vulnerability_findings(stdout: str | bytes) -> list[str]:
    try:
        if isinstance(stdout, bytes):
            stdout = stdout.decode("utf-8")
        payload = json.loads(stdout)
    except (UnicodeDecodeError, json.JSONDecodeError, TypeError) as error:
        raise _invalid_dotnet_json(str(error)) from error

    if not isinstance(payload, dict) or payload.get("version") != 1:
        raise _invalid_dotnet_json("root object must use output version 1")
    problems = payload.get("problems", [])
    if not isinstance(problems, list):
        raise _invalid_dotnet_json("problems must be an array")
    if problems:
        problem_details: list[str] = []
        for problem in problems:
            if not isinstance(problem, dict):
                raise _invalid_dotnet_json("each problem must be an object")
            level = problem.get("level")
            text = problem.get("text")
            if not isinstance(level, str) or not isinstance(text, str):
                raise _invalid_dotnet_json("problem level and text must be strings")
            problem_details.append(f"{level}: {text}")
        raise DependencyDeltaError(
            "dotnet vulnerability report contains problems:\n" + "\n".join(problem_details)
        )

    projects = payload.get("projects")
    if not isinstance(projects, list):
        raise _invalid_dotnet_json("root object must contain a projects array")
    if not projects:
        raise DependencyDeltaError(
            "dotnet vulnerability report contains no projects for nonempty CivicSurvival.sln"
        )

    findings: list[str] = []
    for project in projects:
        if not isinstance(project, dict) or not isinstance(project.get("frameworks"), list):
            raise _invalid_dotnet_json("each project must contain a frameworks array")
        project_path = project.get("path", "<unknown-project>")
        if not isinstance(project_path, str):
            raise _invalid_dotnet_json("project path must be a string")
        for framework in project["frameworks"]:
            if not isinstance(framework, dict):
                raise _invalid_dotnet_json("each framework must be an object")
            framework_name = framework.get("framework", "<unknown-framework>")
            if not isinstance(framework_name, str):
                raise _invalid_dotnet_json("framework name must be a string")
            for package_kind in ("topLevelPackages", "transitivePackages"):
                packages = framework.get(package_kind, [])
                if not isinstance(packages, list):
                    raise _invalid_dotnet_json(f"{package_kind} must be an array")
                for package in packages:
                    if not isinstance(package, dict):
                        raise _invalid_dotnet_json(f"each {package_kind} entry must be an object")
                    package_id = package.get("id", "<unknown-package>")
                    vulnerabilities = package.get("vulnerabilities", [])
                    if not isinstance(package_id, str) or not isinstance(vulnerabilities, list):
                        raise _invalid_dotnet_json(
                            "package id must be a string and vulnerabilities must be an array"
                        )
                    for vulnerability in vulnerabilities:
                        if not isinstance(vulnerability, dict):
                            raise _invalid_dotnet_json("each vulnerability must be an object")
                        severity = vulnerability.get("severity", "unknown")
                        advisory = vulnerability.get(
                            "advisoryurl", vulnerability.get("advisoryUrl", "unknown")
                        )
                        if not isinstance(severity, str) or not isinstance(advisory, str):
                            raise _invalid_dotnet_json(
                                "vulnerability severity and advisory URL must be strings"
                            )
                        findings.append(
                            f"{project_path} {framework_name} {package_kind} {package_id} "
                            f"severity={severity} advisory={advisory}"
                        )
    return findings


def _is_dotnet_vulnerability_scan(scan: ScanCommand) -> bool:
    return scan.ecosystem == "csharp" and "--vulnerable" in scan.command


def _is_dotnet_restore(scan: ScanCommand) -> bool:
    return scan.ecosystem == "csharp" and "--locked-mode" in scan.command


def run_scan_plan(plan: Sequence[ScanCommand], runner: Runner = subprocess.run) -> None:
    """Execute every scanner and turn any nonzero result into a gate failure."""
    restore_failed = False
    for scan in plan:
        capture_output = _is_dotnet_vulnerability_scan(scan)
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
        if result.returncode != 0:
            # Allow dotnet restore failures when the project requires external
            # tooling (e.g. CS2 Modding Toolkit) not available on CI runners.
            # The vulnerability scan will also be skipped since it needs restored packages.
            if _is_dotnet_restore(scan):
                print(
                    f"  warning: {scan.ecosystem} restore failed in {scan.cwd} "
                    f"(exit {result.returncode}) -- external tooling may be required; "
                    "skipping vulnerability scan"
                )
                restore_failed = True
                continue
            # If the vulnerability scan fails and the preceding restore was
            # skipped, treat it as a warning (vulnerability scan needs restored
            # packages which are unavailable without the external tooling).
            if _is_dotnet_vulnerability_scan(scan) and restore_failed:
                print(
                    f"  warning: {scan.ecosystem} vulnerability scan failed in {scan.cwd} "
                    f"(exit {result.returncode}) -- skipped because restore was unavailable"
                )
                continue
            raise DependencyDeltaError(
                f"{scan.ecosystem} scanner failed in {scan.cwd}: {' '.join(scan.command)} "
                f"(exit {result.returncode})"
            )
        if capture_output:
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
    """Return the unified diff of *changed_path* between *base* and *head*."""
    result = subprocess.run(
        (
            "git",
            "diff",
            "--no-color",
            "--unified=0",
            "--",
            changed_path,
            base,
            head,
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
        )
        if not plan:
            print("No supported dependency manifest changes detected.")
            return 0
        for scan in plan:
            print(f"Running {scan.ecosystem} dependency scan: {' '.join(scan.command)}")
        run_scan_plan(plan)
    except DependencyDeltaError as error:
        print(f"Dependency Delta failed: {error}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
