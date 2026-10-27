"""Independent black-box contracts, with real git history and controlled audit output."""

import importlib.util
import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

SPEC = importlib.util.spec_from_file_location(
    "independent_delta", Path(__file__).parents[1] / "scripts/dependency_delta.py"
)
m = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = m
SPEC.loader.exec_module(m)


def git(root, *args):
    return subprocess.run(
        ["git", "-c", "core.hooksPath=/dev/null", *args],
        cwd=root,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def history(tmp_path, lock=True):
    root = tmp_path / "project"
    root.mkdir()
    git(root, "init", "-q")
    git(root, "config", "user.name", "Independent Reviewer")
    git(root, "config", "user.email", "review@example.invalid")
    (root / "package.json").write_text(
        json.dumps({"name": "review", "dependencies": {"demo": "2.0.0"}})
    )
    if lock:
        (root / "package-lock.json").write_text(
            json.dumps({"lockfileVersion": 3, "version": "2.0.0"})
        )
    git(root, "add", ".")
    git(root, "commit", "-qm", "base fixture")
    base = git(root, "rev-parse", "HEAD")
    (root / "package.json").write_text(
        json.dumps({"name": "review", "dependencies": {"demo": "1.0.0"}})
    )
    (root / "package-lock.json").write_text(json.dumps({"lockfileVersion": 3, "version": "1.0.0"}))
    scan = m.build_scan_plan(root, ["package-lock.json"])[0]
    return root, base, scan


def payload(*ids):
    entries = [
        {"url": "https://github.com/advisories/" + i, "source": n} for n, i in enumerate(ids)
    ]
    # npm reports metadata.vulnerabilities.total as the number of vulnerable
    # PACKAGES, not the number of advisories. Every advisory here belongs to
    # the single package "demo", so the total is 1 whenever any advisory is
    # present. Verified against a live npm audit: one package with 24
    # advisories reports total 1.
    return json.dumps(
        {
            "auditReportVersion": 2,
            "metadata": {
                "vulnerabilities": {
                    "info": 0,
                    "low": 0,
                    "moderate": 0,
                    "high": 0,
                    "critical": 0,
                    "total": 1 if ids else 0,
                }
            },
            "vulnerabilities": {"demo": {"via": entries}} if ids else {},
        }
    )


def run(scan, base, head_text, base_text, head_code=1, base_code=1, inspect=None):
    calls = []

    def runner(command, **kwargs):
        head = Path(kwargs["cwd"]) == scan.cwd
        calls.append(head)
        if inspect:
            inspect(head, Path(kwargs["cwd"]))
        return subprocess.CompletedProcess(
            command, head_code if head else base_code, head_text if head else base_text, ""
        )

    m.run_scan_plan([scan], runner=runner, base_ref=base)
    assert calls == [True, False]


def test_a_additional_advisory_same_package(tmp_path):
    _, base, scan = history(tmp_path)
    with pytest.raises(m.DependencyDeltaError, match="GHSA-Y"):
        run(scan, base, payload("GHSA-X", "GHSA-Y"), payload("GHSA-X"))


def test_b_missing_real_git_base_lock(tmp_path):
    root, base, scan = history(tmp_path, lock=False)
    assert "package-lock.json" not in git(root, "ls-tree", "--name-only", base)
    with pytest.raises(m.DependencyDeltaError, match="no package-lock.json"):
        run(scan, base, payload(), payload(), 0, 0)


@pytest.mark.parametrize("side", ["head", "base"])
def test_c_invalid_json(tmp_path, side):
    _, base, scan = history(tmp_path)
    with pytest.raises(m.DependencyDeltaError, match="invalid npm audit JSON"):
        run(
            scan,
            base,
            "{broken" if side == "head" else payload(),
            "{broken" if side == "base" else payload(),
            0,
            0,
        )


@pytest.mark.parametrize(
    "head_ids,base_ids,hcode,bcode,blocked",
    [
        ([], [], 0, 0, False),
        (["GHSA-X"], [], 1, 0, True),
        ([], ["GHSA-X"], 0, 1, False),
        (["GHSA-X"], ["GHSA-X"], 1, 1, False),
    ],
)
def test_d_exit_matrix(tmp_path, head_ids, base_ids, hcode, bcode, blocked):
    _, base, scan = history(tmp_path)
    if blocked:
        with pytest.raises(m.DependencyDeltaError, match="introduced"):
            run(scan, base, payload(*head_ids), payload(*base_ids), hcode, bcode)
    else:
        run(scan, base, payload(*head_ids), payload(*base_ids), hcode, bcode)


def test_e_base_manifest_and_lock_both_from_commit(tmp_path):
    root, base, scan = history(tmp_path)

    def inspect(head, cwd):
        if not head:
            for name in ["package.json", "package-lock.json"]:
                assert (cwd / name).read_text() == git(root, "show", base + ":" + name)
                assert (cwd / name).read_text() != (root / name).read_text()

    run(scan, base, payload(), payload(), 0, 0, inspect)


@pytest.mark.parametrize("side", ["head", "base"])
def test_crash_exit_must_fail(tmp_path, side):
    _, base, scan = history(tmp_path)
    with pytest.raises(m.DependencyDeltaError):
        run(
            scan, base, payload(), payload(), 2 if side == "head" else 0, 2 if side == "base" else 0
        )


@pytest.mark.parametrize(
    "bad",
    [
        "{}",
        '{"error":{"code":"EAUDITNOLOCK"}}',
        '{"vulnerabilities":{"demo":{}}}',
        '{"vulnerabilities":{"demo":{"via":[{}]}}}',
        '{"vulnerabilities":{"demo":{"via":[42]}}}',
    ],
)
def test_invalid_schema_must_fail(tmp_path, bad):
    _, base, scan = history(tmp_path)
    with pytest.raises(m.DependencyDeltaError):
        run(scan, base, bad, payload(), 1, 0)


def test_no_node_dotnet_fallthrough(tmp_path, monkeypatch):
    _, base, scan = history(tmp_path)

    def forbidden(_):
        pytest.fail("node entered dotnet parser")

    monkeypatch.setattr(m, "_dotnet_vulnerability_findings", forbidden)
    run(scan, base, payload(), payload(), 0, 0)


def test_base_shrinkwrap_must_survive_head_deletion(tmp_path):
    root, _, scan = history(tmp_path)
    (root / "npm-shrinkwrap.json").write_text(
        '{"lockfileVersion":3,"version":"base-authoritative"}'
    )
    git(root, "add", ".")
    git(root, "commit", "-qm", "base contains both lockfiles")
    base = git(root, "rev-parse", "HEAD")
    (root / "npm-shrinkwrap.json").unlink()
    staged = m._materialise_base_tree(scan, base)
    try:
        assert (staged / "npm-shrinkwrap.json").exists(), (
            "authoritative base shrinkwrap silently omitted"
        )
    finally:
        shutil.rmtree(staged)


def test_same_id_version_downgrade_is_accepted(tmp_path):
    _, base, scan = history(tmp_path)
    run(scan, base, payload("GHSA-X"), payload("GHSA-X"))


def test_advisory_rename_blocks(tmp_path):
    _, base, scan = history(tmp_path)
    with pytest.raises(m.DependencyDeltaError, match="GHSA-renamed"):
        run(scan, base, payload("GHSA-renamed"), payload("GHSA-original"))


def test_committed_csproj_dependency_change_is_not_skipped(tmp_path):
    root, _, _ = history(tmp_path)
    project = root / "Demo.csproj"
    project.write_text('<Project>\n<PackageReference Include="Demo" Version="1" />\n</Project>\n')
    (root / "packages.lock.json").write_text("{}")
    (root / "CivicSurvival.sln").write_text("fixture")
    git(root, "add", ".")
    git(root, "commit", "-qm", "cs base")
    base = git(root, "rev-parse", "HEAD")
    project.write_text(project.read_text().replace('Version="1"', 'Version="2"'))
    git(root, "add", ".")
    git(root, "commit", "-qm", "cs head")
    head = git(root, "rev-parse", "HEAD")
    plan = m.build_scan_plan(
        root, ["Demo.csproj"], diff_provider=lambda r, p: m._file_diff(r, base, head, p)
    )
    assert plan, "committed dependency change incorrectly produces empty plan"


def test_dotnet_restore_and_scan_failure_must_fail(tmp_path):
    plan = [
        m.ScanCommand("csharp", tmp_path, ("dotnet", "restore", "--locked-mode")),
        m.ScanCommand("csharp", tmp_path, ("dotnet", "list", "--vulnerable")),
    ]
    with pytest.raises(m.DependencyDeltaError):
        m.run_scan_plan(
            plan, runner=lambda *a, **k: subprocess.CompletedProcess(a[0], 2, "", "failure")
        )


def test_head_ignore_cannot_disable_dependency_gate(tmp_path):
    root, _, _ = history(tmp_path)
    (root / ".civicignore").write_text("*\n")
    assert m.build_scan_plan(root, ["package-lock.json"]), (
        "head-controlled ignore bypassed dependency scan"
    )
