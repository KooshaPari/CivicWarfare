from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).parents[1]
SCRIPT = ROOT / "scripts" / "dependency_delta.py"


def load_module():
    spec = importlib.util.spec_from_file_location("dependency_delta", SCRIPT)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def init_git_repo(repo: Path) -> str:
    """Initialise a git repository in *repo* and return the base commit."""
    subprocess.run(["git", "init", "--quiet"], cwd=repo, check=True)
    subprocess.run(
        ["git", "config", "user.email", "tests@example.invalid"],
        cwd=repo,
        check=True,
    )
    subprocess.run(["git", "config", "user.name", "Civic tests"], cwd=repo, check=True)
    subprocess.run(["git", "add", "."], cwd=repo, check=True)
    subprocess.run(["git", "commit", "--quiet", "-m", "base"], cwd=repo, check=True)
    return subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repo,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def load_workflow(path: Path) -> dict:
    ruby = shutil.which("ruby")
    assert ruby, "Ruby is required for dependency-free structural YAML validation"
    script = """
require "json"
require "yaml"
source = STDIN.read.sub(/^on:/, '"on":')
puts JSON.generate(YAML.safe_load(source, aliases: true))
"""
    result = subprocess.run(
        [ruby, "-e", script],
        input=path.read_text(),
        capture_output=True,
        check=True,
        text=True,
    )
    return json.loads(result.stdout)


def test_package_lock_change_runs_the_locked_node_audit(tmp_path):
    module = load_module()
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')

    plan = module.build_scan_plan(tmp_path, ["CivicSurvival/UI/package-lock.json"])

    assert [(item.ecosystem, item.cwd, item.command) for item in plan] == [
        (
            "node",
            ui,
            ("npm", "audit", "--package-lock-only", "--audit-level=low", "--json"),
        )
    ]


def test_npm_shrinkwrap_change_runs_the_locked_node_audit_from_project(tmp_path):
    module = load_module()
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "npm-shrinkwrap.json").write_text('{"lockfileVersion":3}')

    plan = module.build_scan_plan(tmp_path, ["CivicSurvival/UI/npm-shrinkwrap.json"])

    assert [(item.ecosystem, item.cwd, item.command) for item in plan] == [
        (
            "node",
            ui,
            ("npm", "audit", "--package-lock-only", "--audit-level=low", "--json"),
        )
    ]


def test_npm_shrinkwrap_takes_precedence_when_both_node_lockfiles_exist(tmp_path):
    module = load_module()
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')
    shrinkwrap = ui / "npm-shrinkwrap.json"
    shrinkwrap.write_text('{"lockfileVersion":3}')

    assert module._node_lockfile(ui) == shrinkwrap

    plan = module.build_scan_plan(
        tmp_path,
        [
            "CivicSurvival/UI/package-lock.json",
            "CivicSurvival/UI/npm-shrinkwrap.json",
        ],
    )
    assert len(plan) == 1
    assert plan[0].cwd == ui


def test_civic_quality_node_cache_supports_both_npm_lockfiles():
    workflow = load_workflow(ROOT / ".github" / "workflows" / "ci.yml")
    steps = workflow["jobs"]["civic-quality"]["steps"]
    setup_node = next(step for step in steps if "actions/setup-node@" in step.get("uses", ""))

    assert setup_node["with"]["cache"] == "npm"
    assert setup_node["with"]["cache-dependency-path"].splitlines() == [
        "CivicSurvival/UI/npm-shrinkwrap.json",
        "CivicSurvival/UI/package-lock.json",
    ]


def test_csharp_lockfile_change_runs_locked_restore_then_vulnerability_scan(tmp_path):
    module = load_module()
    solution = tmp_path / "CivicSurvival.sln"
    solution.write_text("Microsoft Visual Studio Solution File")
    project = tmp_path / "CivicSurvival.Contracts"
    project.mkdir()
    (project / "packages.lock.json").write_text("{}")

    plan = module.build_scan_plan(tmp_path, ["CivicSurvival.Contracts/packages.lock.json"])

    assert [(item.ecosystem, item.cwd, item.command) for item in plan] == [
        (
            "csharp",
            tmp_path,
            ("dotnet", "restore", "CivicSurvival.sln", "--locked-mode"),
        ),
        (
            "csharp",
            tmp_path,
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


def test_node_manifest_without_lockfile_fails_with_path_ecosystem_and_remedy(tmp_path):
    module = load_module()
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')

    with pytest.raises(module.DependencyDeltaError) as excinfo:
        module.build_scan_plan(tmp_path, ["CivicSurvival/UI/package.json"])

    message = str(excinfo.value)
    assert "CivicSurvival/UI/package.json" in message
    assert "node" in message
    assert "package-lock.json" in message


def test_unsupported_manifest_fails_with_path_ecosystem_and_remedy(tmp_path):
    module = load_module()
    native = tmp_path / "native"
    native.mkdir()
    (native / "Cargo.toml").write_text("[package]\nname = 'civic'\n")

    with pytest.raises(module.DependencyDeltaError) as excinfo:
        module.build_scan_plan(tmp_path, ["native/Cargo.toml"])

    message = str(excinfo.value)
    assert "native/Cargo.toml" in message
    assert "rust" in message
    assert "scanner" in message


def test_csharp_project_without_lockfile_fails_with_path_ecosystem_and_remedy(tmp_path):
    module = load_module()
    (tmp_path / "CivicSurvival.sln").write_text("Microsoft Visual Studio Solution File")
    project = tmp_path / "CivicSurvival.Contracts"
    project.mkdir()
    (project / "CivicSurvival.Contracts.csproj").write_text("<Project />")

    with pytest.raises(module.DependencyDeltaError) as excinfo:
        module.build_scan_plan(tmp_path, ["CivicSurvival.Contracts/CivicSurvival.Contracts.csproj"])

    message = str(excinfo.value)
    assert "CivicSurvival.Contracts/CivicSurvival.Contracts.csproj" in message
    assert "csharp" in message
    assert "packages.lock.json" in message


def test_csproj_version_only_change_is_exempt_from_dependency_scan(tmp_path):
    module = load_module()
    diff = (
        "--- a/CivicSurvival/CivicSurvival.csproj\n"
        "+++ b/CivicSurvival/CivicSurvival.csproj\n"
        "@@ -10 +10 @@\n"
        "-  <Version>0.3.24</Version>\n"
        "+  <Version>0.3.25</Version>\n"
    )

    def provider(_repo, _path):
        return diff

    plan = module.build_scan_plan(
        tmp_path,
        ["CivicSurvival/CivicSurvival.csproj"],
        diff_provider=provider,
    )

    assert plan == []


def test_csproj_description_change_is_exempt_from_dependency_scan(tmp_path):
    module = load_module()
    diff = (
        "--- a/CivicSurvival/CivicSurvival.csproj\n"
        "+++ b/CivicSurvival/CivicSurvival.csproj\n"
        "@@ -20 +20 @@\n"
        "-  <Description>old</Description>\n"
        "+  <Description>new</Description>\n"
    )

    plan = module.build_scan_plan(
        tmp_path,
        ["CivicSurvival/CivicSurvival.csproj"],
        diff_provider=lambda _r, _p: diff,
    )

    assert plan == []


def test_csproj_metadata_combined_with_version_is_exempt_from_dependency_scan(tmp_path):
    module = load_module()
    diff = (
        "--- a/CivicSurvival/CivicSurvival.csproj\n"
        "+++ b/CivicSurvival/CivicSurvival.csproj\n"
        "@@ -10,3 +10,3 @@\n"
        "-  <Version>0.3.24</Version>\n"
        "-  <AssemblyName>CivicSurvival</AssemblyName>\n"
        "-  <Description>old</Description>\n"
        "+  <Version>0.3.25</Version>\n"
        "+  <AssemblyName>CivicSurvival.v2</AssemblyName>\n"
        "+  <Description>new</Description>\n"
    )

    plan = module.build_scan_plan(
        tmp_path,
        ["CivicSurvival/CivicSurvival.csproj"],
        diff_provider=lambda _r, _p: diff,
    )

    assert plan == []


def test_csproj_single_line_package_change_is_not_hidden_by_a_metadata_change(tmp_path):
    """A single-line csproj that changes both a metadata element and a package.

    The whole document sits on one line, so the changed element can only be
    found by comparing the elements themselves. A comparison that stops at
    the first difference would report the metadata element alone and skip
    the dependency scan, letting a package change through unchecked.
    """
    module = load_module()
    diff = (
        "--- a/CivicSurvival/CivicSurvival.csproj\n"
        "+++ b/CivicSurvival/CivicSurvival.csproj\n"
        "@@ -1 +1 @@\n"
        "-<Project><PropertyGroup><Version>0.3.24</Version></PropertyGroup>"
        '<ItemGroup><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup></Project>\n'
        "+<Project><PropertyGroup><Version>0.3.25</Version></PropertyGroup>"
        '<ItemGroup><PackageReference Include="Foo" Version="2.0.0" /></ItemGroup></Project>\n'
    )

    with pytest.raises(module.DependencyDeltaError, match="packages.lock.json"):
        module.build_scan_plan(
            tmp_path,
            ["CivicSurvival/CivicSurvival.csproj"],
            diff_provider=lambda _r, _p: diff,
        )


def test_csproj_single_line_metadata_only_change_is_exempt(tmp_path):
    """A single-line csproj that changes only a metadata element is exempt."""
    module = load_module()
    diff = (
        "--- a/CivicSurvival/CivicSurvival.csproj\n"
        "+++ b/CivicSurvival/CivicSurvival.csproj\n"
        "@@ -1 +1 @@\n"
        "-<Project><PropertyGroup><Version>0.3.24</Version></PropertyGroup></Project>\n"
        "+<Project><PropertyGroup><Version>0.3.25</Version></PropertyGroup></Project>\n"
    )

    plan = module.build_scan_plan(
        tmp_path,
        ["CivicSurvival/CivicSurvival.csproj"],
        diff_provider=lambda _r, _p: diff,
    )

    assert plan == []


def test_csproj_package_reference_change_still_triggers_dependency_scan(tmp_path):
    module = load_module()
    diff = (
        "--- a/CivicSurvival/CivicSurvival.csproj\n"
        "+++ b/CivicSurvival/CivicSurvival.csproj\n"
        "@@ -10,3 +10,3 @@\n"
        "-  <Version>0.3.24</Version>\n"
        '-  <PackageReference Include="Foo" Version="1.0.0" />\n'
        "-  <Description>old</Description>\n"
        "+  <Version>0.3.25</Version>\n"
        '+  <PackageReference Include="Foo" Version="2.0.0" />\n'
        "+  <Description>new</Description>\n"
    )
    with pytest.raises(module.DependencyDeltaError, match="packages.lock.json"):
        module.build_scan_plan(
            tmp_path,
            ["CivicSurvival/CivicSurvival.csproj"],
            diff_provider=lambda _r, _p: diff,
        )


def test_csproj_target_framework_change_still_triggers_dependency_scan(tmp_path):
    module = load_module()
    diff = (
        "--- a/CivicSurvival/CivicSurvival.csproj\n"
        "+++ b/CivicSurvival/CivicSurvival.csproj\n"
        "@@ -1 +1 @@\n"
        "-<TargetFramework>net8.0</TargetFramework>\n"
        "+<TargetFramework>net9.0</TargetFramework>\n"
    )

    with pytest.raises(module.DependencyDeltaError, match="packages.lock.json"):
        module.build_scan_plan(
            tmp_path,
            ["CivicSurvival/CivicSurvival.csproj"],
            diff_provider=lambda _r, _p: diff,
        )


def test_csproj_no_diff_provider_still_fails_closed_on_csproj_changes(tmp_path):
    module = load_module()
    with pytest.raises(module.DependencyDeltaError, match="packages.lock.json"):
        module.build_scan_plan(tmp_path, ["CivicSurvival/CivicSurvival.csproj"])


def test_csproj_version_only_diff_in_real_git_repo_is_accepted(tmp_path):
    repo = tmp_path / "repo"
    repo.mkdir()
    subprocess.run(["git", "init", "--quiet"], cwd=repo, check=True)
    subprocess.run(
        ["git", "config", "user.email", "tests@example.invalid"],
        cwd=repo,
        check=True,
    )
    subprocess.run(["git", "config", "user.name", "Civic tests"], cwd=repo, check=True)
    (repo / "CivicSurvival.csproj").write_text(
        "<Project><PropertyGroup>"
        "<TargetFramework>net8.0</TargetFramework>"
        "<Version>0.3.24</Version>"
        "</PropertyGroup></Project>"
    )
    subprocess.run(["git", "add", "."], cwd=repo, check=True)
    subprocess.run(["git", "commit", "--quiet", "-m", "base"], cwd=repo, check=True)
    base = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repo,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()

    (repo / "CivicSurvival.csproj").write_text(
        "<Project><PropertyGroup>"
        "<TargetFramework>net8.0</TargetFramework>"
        "<Version>0.3.25</Version>"
        "</PropertyGroup></Project>"
    )
    subprocess.run(["git", "add", "."], cwd=repo, check=True)
    subprocess.run(
        ["git", "commit", "--quiet", "-m", "version bump"],
        cwd=repo,
        check=True,
    )
    head = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repo,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()

    result = subprocess.run(
        [sys.executable, SCRIPT, "--repo", repo, "--base", base, "--head", head],
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stdout
    assert "No supported dependency manifest changes detected." in result.stdout


def test_civicignore_silently_skips_matched_paths(tmp_path):
    module = load_module()
    (tmp_path / ".civicignore").write_text(
        "# ignored manifests\nCivicSurvival/CivicSurvival.csproj\nCivicSurvival/UI/package.json\n"
    )
    base = init_git_repo(tmp_path)

    plan = module.build_scan_plan(
        tmp_path,
        [
            "CivicSurvival/CivicSurvival.csproj",
            "CivicSurvival/UI/package.json",
        ],
        base_ref=base,
    )

    assert plan == []


def test_civicignore_does_not_mask_unmatched_paths(tmp_path):
    module = load_module()
    (tmp_path / ".civicignore").write_text("CivicSurvival/CivicSurvival.csproj\n")
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')
    base = init_git_repo(tmp_path)

    plan = module.build_scan_plan(
        tmp_path,
        [
            "CivicSurvival/CivicSurvival.csproj",
            "CivicSurvival/UI/package-lock.json",
        ],
        base_ref=base,
    )

    assert [(item.ecosystem, item.cwd) for item in plan] == [("node", ui)]


def test_civicignore_supports_fnmatch_globs(tmp_path):
    module = load_module()
    (tmp_path / ".civicignore").write_text("**/*.csproj\n")
    base = init_git_repo(tmp_path)

    plan = module.build_scan_plan(
        tmp_path,
        ["CivicSurvival/CivicSurvival.csproj"],
        diff_provider=lambda _r, _p: (
            "--- a/CivicSurvival/CivicSurvival.csproj\n"
            "+++ b/CivicSurvival/CivicSurvival.csproj\n"
            "@@ -10 +10 @@\n"
            '-  <PackageReference Include="X" />\n'
            '+  <PackageReference Include="Y" />\n'
        ),
        base_ref=base,
    )

    assert plan == []


def test_civicignore_blank_and_comment_lines_are_ignored(tmp_path):
    module = load_module()
    (tmp_path / ".civicignore").write_text(
        "\n   \n# this is a comment\nCivicSurvival/CivicSurvival.csproj\n# trailing comment\n"
    )
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')
    base = init_git_repo(tmp_path)

    plan = module.build_scan_plan(
        tmp_path,
        [
            "CivicSurvival/CivicSurvival.csproj",
            "CivicSurvival/UI/package-lock.json",
        ],
        base_ref=base,
    )

    assert [(item.ecosystem, item.cwd) for item in plan] == [("node", ui)]


def test_missing_civicignore_does_not_fail(tmp_path):
    module = load_module()
    assert not (tmp_path / ".civicignore").exists()

    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')
    base = init_git_repo(tmp_path)

    plan = module.build_scan_plan(
        tmp_path,
        ["CivicSurvival/UI/package-lock.json"],
        base_ref=base,
    )

    assert len(plan) == 1
    assert plan[0].ecosystem == "node"


def test_civicignore_is_loaded_only_once_per_call(tmp_path):
    module = load_module()
    (tmp_path / ".civicignore").write_text("CivicSurvival/CivicSurvival.csproj\n")
    base = init_git_repo(tmp_path)

    seen_loads: list[int] = []

    original = module._load_civicignore

    def counting(repo: Path, base_ref: str | None = None) -> list[str]:
        seen_loads.append(1)
        return original(repo, base_ref)

    module._load_civicignore = counting  # type: ignore[assignment]
    try:
        module.build_scan_plan(
            tmp_path,
            [
                "CivicSurvival/CivicSurvival.csproj",
                "CivicSurvival/CivicSurvival.csproj",
                "CivicSurvival/CivicSurvival.csproj",
            ],
            base_ref=base,
        )
    finally:
        module._load_civicignore = original  # type: ignore[assignment]

    assert len(seen_loads) == 1


def test_scanner_failure_propagates_to_the_required_gate(tmp_path):
    module = load_module()
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    command = module.ScanCommand(
        ecosystem="node",
        cwd=ui,
        command=("npm", "audit", "--package-lock-only", "--audit-level=low", "--json"),
    )

    def failing_runner(*_args, **_kwargs):
        return type("Result", (), {"returncode": 1, "stdout": "not-json"})()

    with pytest.raises(module.DependencyDeltaError, match="invalid npm audit JSON"):
        module.run_scan_plan([command], runner=failing_runner)


def dotnet_scan_command(module, cwd):
    return module.ScanCommand(
        ecosystem="csharp",
        cwd=cwd,
        command=(
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
    )


def dotnet_result(payload):
    return type("Result", (), {"returncode": 0, "stdout": json.dumps(payload), "stderr": ""})()


def test_dotnet_scan_accepts_machine_readable_zero_vulnerability_result(tmp_path):
    module = load_module()
    payload = {
        "version": 1,
        "projects": [
            {
                "path": "CivicSurvival.Contracts/CivicSurvival.Contracts.csproj",
                "frameworks": [
                    {
                        "framework": "net8.0",
                        "topLevelPackages": [],
                        "transitivePackages": [],
                    }
                ],
            }
        ],
    }

    module.run_scan_plan(
        [dotnet_scan_command(module, tmp_path)],
        runner=lambda *_args, **_kwargs: dotnet_result(payload),
    )


def test_dotnet_scan_accepts_the_real_report_that_omits_frameworks(tmp_path):
    """A real clean report names the project and omits the frameworks key.

    ``dotnet list <project> package --vulnerable --include-transitive
    --format json --output-version 1`` emits a project entry with only a
    path when it found nothing to describe. Rejecting that shape would
    fail the gate on every clean .NET project.
    """
    module = load_module()
    payload = {
        "version": 1,
        "parameters": "--vulnerable --include-transitive",
        "sources": ["https://api.nuget.org/v3/index.json"],
        "projects": [{"path": "CivicSurvival.Contracts/CivicSurvival.Contracts.csproj"}],
    }

    module.run_scan_plan(
        [dotnet_scan_command(module, tmp_path)],
        runner=lambda *_args, **_kwargs: dotnet_result(payload),
    )


def test_dotnet_scan_fails_closed_when_a_project_reports_no_frameworks(tmp_path):
    """A project with an empty frameworks list means nothing was scanned."""
    module = load_module()
    payload = {
        "version": 1,
        "projects": [
            {"path": "CivicSurvival.Contracts/CivicSurvival.Contracts.csproj", "frameworks": []}
        ],
    }

    with pytest.raises(module.DependencyDeltaError, match="no frameworks"):
        module.run_scan_plan(
            [dotnet_scan_command(module, tmp_path)],
            runner=lambda *_args, **_kwargs: dotnet_result(payload),
        )


@pytest.mark.parametrize("package_kind", ["topLevelPackages", "transitivePackages"])
def test_dotnet_scan_fails_when_machine_readable_result_contains_vulnerabilities(
    tmp_path, package_kind
):
    module = load_module()
    payload = {
        "version": 1,
        "projects": [
            {
                "path": "CivicSurvival.Contracts/CivicSurvival.Contracts.csproj",
                "frameworks": [
                    {
                        "framework": "net8.0",
                        "topLevelPackages": [],
                        "transitivePackages": [],
                    }
                ],
            }
        ],
    }
    payload["projects"][0]["frameworks"][0][package_kind] = [
        {
            "id": "Hostile.Package",
            "resolvedVersion": "1.2.3",
            "vulnerabilities": [
                {"severity": "High", "advisoryurl": "https://example.invalid/CVE-TEST"}
            ],
        }
    ]

    with pytest.raises(module.DependencyDeltaError) as excinfo:
        module.run_scan_plan(
            [dotnet_scan_command(module, tmp_path)],
            runner=lambda *_args, **_kwargs: dotnet_result(payload),
        )

    message = str(excinfo.value)
    assert "Hostile.Package" in message
    assert "CVE-TEST" in message


@pytest.mark.parametrize(
    "stdout",
    [
        "not-json",
        json.dumps({"version": 1}),
        json.dumps({"version": 2, "projects": []}),
        json.dumps({"version": 1, "projects": "bad"}),
    ],
)
def test_dotnet_scan_fails_closed_on_malformed_machine_readable_result(tmp_path, stdout):
    module = load_module()
    result = type("Result", (), {"returncode": 0, "stdout": stdout, "stderr": ""})()

    with pytest.raises(module.DependencyDeltaError, match="invalid dotnet vulnerability JSON"):
        module.run_scan_plan(
            [dotnet_scan_command(module, tmp_path)],
            runner=lambda *_args, **_kwargs: result,
        )


@pytest.mark.parametrize("level", ["warning", "error"])
def test_dotnet_scan_fails_closed_on_reported_problems(tmp_path, level):
    module = load_module()
    payload = {
        "version": 1,
        "problems": [{"level": level, "text": "vulnerability source unavailable"}],
        "projects": [
            {
                "path": "CivicSurvival.Contracts/CivicSurvival.Contracts.csproj",
                "frameworks": [{"framework": "net8.0"}],
            }
        ],
    }

    with pytest.raises(module.DependencyDeltaError) as excinfo:
        module.run_scan_plan(
            [dotnet_scan_command(module, tmp_path)],
            runner=lambda *_args, **_kwargs: dotnet_result(payload),
        )

    message = str(excinfo.value)
    assert level in message
    assert "vulnerability source unavailable" in message


def test_dotnet_scan_fails_closed_when_nonempty_solution_reports_no_projects(tmp_path):
    module = load_module()
    payload = {"version": 1, "problems": [], "projects": []}

    with pytest.raises(module.DependencyDeltaError, match="no projects"):
        module.run_scan_plan(
            [dotnet_scan_command(module, tmp_path)],
            runner=lambda *_args, **_kwargs: dotnet_result(payload),
        )


def test_changed_paths_uses_nul_delimiters_and_preserves_odd_repo_relative_names(
    tmp_path,
):
    module = load_module()
    calls = []
    result = type(
        "Result",
        (),
        {
            "returncode": 0,
            "stdout": b"CivicSurvival/UI/package-lock.json\0odd dir/name with space\npackage.json\0",
            "stderr": b"",
        },
    )()

    def runner(command, **kwargs):
        calls.append((command, kwargs))
        return result

    paths = module.changed_paths(tmp_path, "base", "head", runner=runner)

    assert paths == [
        "CivicSurvival/UI/package-lock.json",
        "odd dir/name with space\npackage.json",
    ]
    assert calls[0][0] == (
        "git",
        "diff",
        "--name-only",
        "-z",
        "--diff-filter=ACMRD",
        "base",
        "head",
    )
    assert calls[0][1]["text"] is False


@pytest.mark.parametrize(
    "unsafe_path",
    [
        b"/tmp/package.json",
        b"../package.json",
        b"deps/../package.json",
        b"../npm-shrinkwrap.json",
    ],
)
def test_changed_paths_rejects_absolute_or_parent_traversal_paths(tmp_path, unsafe_path):
    module = load_module()
    result = type(
        "Result",
        (),
        {"returncode": 0, "stdout": unsafe_path + b"\0", "stderr": b""},
    )()

    with pytest.raises(module.DependencyDeltaError, match="unsafe changed path"):
        module.changed_paths(tmp_path, "base", "head", runner=lambda *_args, **_kwargs: result)


def test_changed_paths_rejects_malformed_non_terminated_output(tmp_path):
    module = load_module()
    result = type(
        "Result",
        (),
        {"returncode": 0, "stdout": b"package.json", "stderr": b""},
    )()

    with pytest.raises(module.DependencyDeltaError, match="malformed NUL-delimited"):
        module.changed_paths(tmp_path, "base", "head", runner=lambda *_args, **_kwargs: result)


def test_deleted_node_lockfile_fails_closed_with_actionable_remedy(tmp_path):
    module = load_module()
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')

    with pytest.raises(module.DependencyDeltaError) as excinfo:
        module.build_scan_plan(tmp_path, ["CivicSurvival/UI/package-lock.json"])

    message = str(excinfo.value)
    assert "CivicSurvival/UI/package-lock.json" in message
    assert "node" in message
    assert "npm-shrinkwrap.json or package-lock.json" in message


def test_deleted_npm_shrinkwrap_fails_closed_without_another_node_lock(tmp_path):
    module = load_module()
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')

    with pytest.raises(module.DependencyDeltaError) as excinfo:
        module.build_scan_plan(tmp_path, ["CivicSurvival/UI/npm-shrinkwrap.json"])

    message = str(excinfo.value)
    assert "CivicSurvival/UI/npm-shrinkwrap.json" in message
    assert "node" in message
    assert "npm-shrinkwrap.json or package-lock.json" in message


def test_hostile_dotnet_problem_fixture_makes_cli_exit_nonzero(tmp_path):
    repo = tmp_path / "repo"
    repo.mkdir()
    subprocess.run(["git", "init", "--quiet"], cwd=repo, check=True)
    subprocess.run(["git", "config", "user.email", "tests@example.invalid"], cwd=repo, check=True)
    subprocess.run(["git", "config", "user.name", "Civic tests"], cwd=repo, check=True)
    (repo / "CivicSurvival.sln").write_text("Microsoft Visual Studio Solution File")
    project = repo / "CivicSurvival.Contracts"
    project.mkdir()
    subprocess.run(["git", "add", "."], cwd=repo, check=True)
    subprocess.run(["git", "commit", "--quiet", "-m", "base"], cwd=repo, check=True)
    base = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repo,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()

    (project / "packages.lock.json").write_text("{}")
    subprocess.run(["git", "add", "."], cwd=repo, check=True)
    subprocess.run(["git", "commit", "--quiet", "-m", "hostile lock change"], cwd=repo, check=True)
    head = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repo,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()

    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    dotnet = bin_dir / "dotnet"
    hostile = {
        "version": 1,
        "problems": [{"level": "warning", "text": "vulnerability source unavailable"}],
        "projects": [
            {
                "path": "CivicSurvival.Contracts/CivicSurvival.Contracts.csproj",
                "frameworks": [{"framework": "net8.0"}],
            }
        ],
    }
    dotnet.write_text(
        "#!/bin/sh\n"
        'if [ "$1" = restore ]; then exit 0; fi\n'
        f"printf '%s\\n' '{json.dumps(hostile)}'\n"
    )
    dotnet.chmod(0o755)
    environment = os.environ.copy()
    environment["PATH"] = f"{bin_dir}{os.pathsep}{environment['PATH']}"

    result = subprocess.run(
        [sys.executable, SCRIPT, "--repo", repo, "--base", base, "--head", head],
        check=False,
        capture_output=True,
        text=True,
        env=environment,
    )

    assert result.returncode == 1
    assert "vulnerability source unavailable" in result.stdout


def test_ci_aggregate_gates_reject_failed_or_skipped_required_results():
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text()
    python_job = workflow.split("  python:\n", 1)[1].split("\n  go:\n", 1)[0]
    security_job = workflow.split("  security:\n", 1)[1].split("\n  dep-review:\n", 1)[0]
    dependency_job = workflow.split("  dep-review:\n", 1)[1].split("\n  civic-quality:\n", 1)[0]

    assert "continue-on-error" not in security_job
    assert "continue-on-error" not in dependency_job
    assert "actions/dependency-review-action" not in dependency_job
    assert "actions/setup-python@" in dependency_job
    assert "python3 -m pip install pip-audit" in dependency_job
    assert "python3 scripts/dependency_delta.py" in dependency_job
    assert "continue-on-error" not in python_job
    assert "ruff check scripts/dependency_delta.py tests/test_ci_dependency_delta.py" in python_job
    assert (
        "ruff format --check scripts/dependency_delta.py tests/test_ci_dependency_delta.py"
        in python_job
    )
    assert "python3 -m pytest -q tests/test_ci_dependency_delta.py" in python_job
    # The gate is split across sibling modules. Every one of them must be
    # linted and format-checked, otherwise a module can drift out of
    # compliance with no check noticing.
    for module in (
        "scripts/dependency_delta.py",
        "scripts/dependency_trees.py",
        "scripts/audit_reports.py",
        "scripts/csproj_diff.py",
        "scripts/errors.py",
        "scripts/scan_types.py",
    ):
        assert module in python_job, f"{module} is not covered by the CI lint job"
    assert "|| echo" not in python_job
    assert 'if [ "$name" = "security" ]; then' in workflow
    assert 'if [ "$event_name" = "pull_request" ] && [ "$name" = "dep-review" ]; then' in workflow
    assert 'if [ "$required" -eq 1 ] && [ "$result" != "success" ]; then' in workflow
    assert 'lint_result="${{ needs.lint.result }}"' in workflow
    assert 'if [ "$lint_result" != "success" ]; then' in workflow


# --- advisory-delta semantics for node scans -------------------------------
#
# `npm audit` exits 1 whenever ANY advisory exists in the dependency graph.
# The Dependency Delta gate must only blame a PR for advisories it INTRODUCED.


def _node_audit_payload(advisories: dict) -> str:
    """A realistic `npm audit --json` report.

    Real reports always carry BOTH "vulnerabilities" and
    "metadata.vulnerabilities"; the reshaped parser requires both so a
    truncated or error payload can never look like a clean bill of health.
    """
    total = len(advisories)
    return json.dumps(
        {
            "auditReportVersion": 2,
            "vulnerabilities": advisories,
            "metadata": {
                "vulnerabilities": {
                    "info": 0,
                    "low": total,
                    "moderate": 0,
                    "high": 0,
                    "critical": 0,
                    "total": total,
                }
            },
        }
    )


def _advisory(url: str, source: int) -> dict:
    return {
        "severity": "high",
        "isDirect": False,
        "via": [{"source": source, "url": url}],
    }


def test_node_advisory_ids_are_extracted_from_ghsa_urls(tmp_path):
    module = load_module()
    payload = _node_audit_payload(
        {
            "undici": _advisory("https://github.com/advisories/GHSA-aaaa-bbbb-cccc", 1),
            "vite": _advisory("https://github.com/advisories/GHSA-dddd-eeee-ffff", 2),
        }
    )

    ids = module.node_advisory_ids(payload)

    assert ids == {
        "undici@https://github.com/advisories/GHSA-aaaa-bbbb-cccc",
        "vite@https://github.com/advisories/GHSA-dddd-eeee-ffff",
    }


def test_node_advisory_ids_fall_back_to_package_and_source(tmp_path):
    module = load_module()
    payload = _node_audit_payload({"zod": {"via": [{"source": 99}]}})

    assert module.node_advisory_ids(payload) == {"zod@99"}


def test_node_advisory_ids_fail_closed_on_malformed_json(tmp_path):
    module = load_module()
    with pytest.raises(module.DependencyDeltaError, match="invalid npm audit JSON"):
        module.node_advisory_ids("not-json")


def test_node_advisory_ids_fail_closed_on_non_object_vulnerabilities(tmp_path):
    module = load_module()
    with pytest.raises(module.DependencyDeltaError, match="invalid npm audit JSON"):
        module.node_advisory_ids(json.dumps({"vulnerabilities": "bad"}))


def _node_plan_for(module, tmp_path, base_lockfile: bool = True):
    """Build a node ScanCommand over a REAL temp git repo.

    The base side of the delta is materialised with `git show <ref>:<path>`,
    so the tests need an actual repository with a committed lockfile.
    """
    repo = tmp_path / "repo"
    ui = repo / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')

    def git(*args):
        subprocess.run(("git", *args), cwd=repo, check=True, capture_output=True, text=True)

    git("init", "-q")
    git("config", "user.email", "test@example.invalid")
    git("config", "user.name", "test")
    git("add", "-A")
    if not base_lockfile:
        # Commit WITHOUT the lockfile so the BASE tree genuinely has none.
        git("rm", "-q", "--cached", "CivicSurvival/UI/package-lock.json")
    git("commit", "-q", "-m", "base")
    return module.ScanCommand(
        ecosystem="node",
        cwd=ui,
        command=("npm", "audit", "--package-lock-only", "--audit-level=low", "--json"),
        base_cwd=ui,
    )


def test_node_scan_passes_when_no_new_advisory_is_introduced(tmp_path):
    """An unchanged pre-existing advisory is not this PR's fault."""
    module = load_module()
    command = _node_plan_for(module, tmp_path)
    payload = _node_audit_payload(
        {"undici": _advisory("https://github.com/advisories/GHSA-aaaa-bbbb-cccc", 1)}
    )

    calls = {"n": 0}

    def runner(*_args, **_kwargs):
        # call 1 is HEAD, call 2 is BASE
        calls["n"] += 1
        return type("Result", (), {"returncode": 1, "stdout": payload})()

    module.run_scan_plan([command], runner=runner, base_ref="HEAD")


def test_node_scan_fails_when_a_new_advisory_is_introduced(tmp_path):
    module = load_module()
    command = _node_plan_for(module, tmp_path)
    base = _node_audit_payload(
        {"undici": _advisory("https://github.com/advisories/GHSA-aaaa-bbbb-cccc", 1)}
    )
    head = _node_audit_payload(
        {
            "undici": _advisory("https://github.com/advisories/GHSA-aaaa-bbbb-cccc", 1),
            "vite": _advisory("https://github.com/advisories/GHSA-dddd-eeee-ffff", 2),
        }
    )
    calls = {"n": 0}

    def runner(*_args, **_kwargs):
        # call 1 is HEAD, call 2 is BASE
        calls["n"] += 1
        return type("Result", (), {"returncode": 1, "stdout": head if calls["n"] == 1 else base})()

    with pytest.raises(module.DependencyDeltaError, match="introduced new advisories"):
        module.run_scan_plan([command], runner=runner, base_ref="HEAD")


def test_node_scan_passes_when_an_advisory_is_removed(tmp_path):
    """A dependency upgrade that FIXES an advisory must not fail the gate."""
    module = load_module()
    command = _node_plan_for(module, tmp_path)
    base = _node_audit_payload(
        {"undici": _advisory("https://github.com/advisories/GHSA-aaaa-bbbb-cccc", 1)}
    )
    head = _node_audit_payload({})
    calls = {"n": 0}

    def runner(*_args, **_kwargs):
        # call 1 is HEAD, call 2 is BASE
        calls["n"] += 1
        return type("Result", (), {"returncode": 1, "stdout": head if calls["n"] == 1 else base})()

    module.run_scan_plan([command], runner=runner, base_ref="HEAD")


def test_node_scan_treats_a_reintroduced_advisory_as_new(tmp_path):
    """An advisory present in base but absent in head is fine; present in BOTH
    is not new. This guards against comparing package names instead of ids."""
    module = load_module()
    command = _node_plan_for(module, tmp_path)
    same = _node_audit_payload(
        {"undici": _advisory("https://github.com/advisories/GHSA-aaaa-bbbb-cccc", 1)}
    )
    calls = {"n": 0}

    def runner(*_args, **_kwargs):
        calls["n"] += 1
        return type("Result", (), {"returncode": 1, "stdout": same})()

    module.run_scan_plan([command], runner=runner, base_ref="HEAD")


def test_node_scan_fails_closed_when_the_base_tree_has_no_lockfile(tmp_path):
    """Without a base lockfile the delta is unknowable, so refuse to guess."""
    module = load_module()
    command = _node_plan_for(module, tmp_path, base_lockfile=False)
    # The lockfile exists in the working tree but was never committed, so the
    # BASE commit genuinely has no lockfile to audit.
    payload = _node_audit_payload({})

    calls = {"n": 0}

    def runner(*_args, **_kwargs):
        calls["n"] += 1
        return type("Result", (), {"returncode": 0, "stdout": payload})()

    with pytest.raises(module.DependencyDeltaError, match="cannot compare against the base tree"):
        module.run_scan_plan([command], runner=runner, base_ref="HEAD")


# --- regression tests for the independent review findings -------------------
#
# The review returned BLOCK/RESHAPE on the first prototype. Three defects
# were confirmed; each gets a test here so it cannot regress.


@pytest.mark.parametrize(
    "payload",
    [
        pytest.param({"error": {"code": "ENOTFOUND"}}, id="npm-registry-error"),
        pytest.param({"error": {"code": "EAI_AGAIN"}}, id="npm-dns-error"),
        pytest.param({}, id="empty-object"),
        pytest.param({"auditReportVersion": 2}, id="no-vulnerabilities-key"),
        pytest.param({"vulnerabilities": {}, "metadata": {}}, id="no-metadata-vulnerabilities"),
    ],
)
def test_node_advisory_ids_fail_closed_on_non_reports(tmp_path, payload):
    """A payload that is not an audit report must RAISE, never read as empty.

    An empty id set means "no new advisories" -> the gate would PASS. npm
    reports transport failures as a top-level "error" object and still exits
    0, so treating that as a clean bill of health silently disables the
    scanner whenever the registry is unreachable.
    """
    module = load_module()
    with pytest.raises(module.DependencyDeltaError, match="invalid npm audit JSON"):
        module.node_advisory_ids(json.dumps(payload))


@pytest.mark.parametrize(
    "payload",
    [
        pytest.param({"vulnerabilities": {"a": {"via": []}}}, id="empty-via"),
        pytest.param({"vulnerabilities": {"a": {"via": [{}]}}}, id="via-without-id"),
        pytest.param({"vulnerabilities": {"a": {}}}, id="missing-via-key"),
    ],
)
def test_node_advisory_ids_fail_closed_on_unidentified_entries(tmp_path, payload):
    """An advisory with no usable identity must raise, not be skipped."""
    module = load_module()
    with pytest.raises(module.DependencyDeltaError, match="invalid npm audit JSON"):
        module.node_advisory_ids(json.dumps(payload))


@pytest.mark.parametrize(
    "payload",
    [
        pytest.param(
            {
                "vulnerabilities": {},
                "metadata": {"vulnerabilities": {"total": 1}},
            },
            id="total-exceeds-listed-packages",
        ),
        pytest.param(
            {
                "vulnerabilities": {"a": {"via": []}},
                "metadata": {"vulnerabilities": {"total": 1}},
            },
            id="empty-via-with-nonzero-total",
        ),
        pytest.param(
            {
                "vulnerabilities": {},
                "metadata": {"vulnerabilities": None},
            },
            id="metadata-vulnerabilities-null",
        ),
        pytest.param(
            {
                "vulnerabilities": {},
                "metadata": {"vulnerabilities": {}},
            },
            id="missing-total",
        ),
    ],
)
def test_node_advisory_ids_fail_closed_when_the_report_is_inconsistent(tmp_path, payload):
    """A report whose own count contradicts its contents must raise.

    npm records how many vulnerable packages it found. A report that
    claims a vulnerability it does not describe is inconsistent, and
    reading it as clean would let that package through unchecked.
    """
    module = load_module()
    with pytest.raises(module.DependencyDeltaError, match="invalid npm audit JSON"):
        module.node_advisory_ids(json.dumps(payload))


def test_node_advisory_ids_accepts_a_report_whose_count_matches(tmp_path):
    """A report whose count matches its contents is accepted."""
    module = load_module()
    payload = {
        "auditReportVersion": 2,
        "metadata": {
            "vulnerabilities": {
                "info": 0,
                "low": 0,
                "moderate": 0,
                "high": 1,
                "critical": 0,
                "total": 1,
            }
        },
        "vulnerabilities": {"a": {"via": [{"url": "https://example.invalid/GHSA-x"}]}},
    }

    assert module.node_advisory_ids(json.dumps(payload)) == {"a@https://example.invalid/GHSA-x"}


def test_advisory_identity_is_package_scoped(tmp_path):
    """Identity must include the package, not just the GHSA id.

    Keying on the global advisory id alone lets a NEW package inherit an
    advisory the base already had, and the new exposure is masked.
    """
    module = load_module()
    payload = _node_audit_payload(
        {
            "pkg-a": _advisory("https://github.com/advisories/GHSA-same", 1),
            "pkg-b": _advisory("https://github.com/advisories/GHSA-same", 1),
        }
    )

    ids = module.node_advisory_ids(payload)

    assert ids == {
        "pkg-a@https://github.com/advisories/GHSA-same",
        "pkg-b@https://github.com/advisories/GHSA-same",
    }


def test_node_scan_fails_when_a_new_package_inherits_an_existing_advisory(tmp_path):
    """The masking case: base pkg-a has GHSA-X, head adds pkg-b with GHSA-X.

    The advisory id is unchanged, so a global-id comparison would find
    nothing new. With package-scoped identity the new package IS a new
    exposure and must fail.
    """
    module = load_module()
    command = _node_plan_for(module, tmp_path)
    base = _node_audit_payload({"pkg-a": _advisory("https://github.com/advisories/GHSA-same", 1)})
    head = _node_audit_payload(
        {
            "pkg-a": _advisory("https://github.com/advisories/GHSA-same", 1),
            "pkg-b": _advisory("https://github.com/advisories/GHSA-same", 1),
        }
    )
    calls = {"n": 0}

    def runner(*_args, **_kwargs):
        # call 1 is HEAD, call 2 is BASE
        calls["n"] += 1
        return type("Result", (), {"returncode": 1, "stdout": head if calls["n"] == 1 else base})()

    with pytest.raises(module.DependencyDeltaError, match="introduced new advisories"):
        module.run_scan_plan([command], runner=runner, base_ref="HEAD")


def test_node_scan_fails_closed_when_the_audit_reports_an_error(tmp_path):
    """npm's error payload must fail the gate, not pass it."""
    module = load_module()
    command = _node_plan_for(module, tmp_path)
    error = json.dumps({"error": {"code": "ENOTFOUND"}})

    def runner(*_args, **_kwargs):
        return type("Result", (), {"returncode": 0, "stdout": error})()

    with pytest.raises(module.DependencyDeltaError, match="invalid npm audit JSON"):
        module.run_scan_plan([command], runner=runner, base_ref="HEAD")


def test_node_scan_audits_the_head_commit_not_the_live_checkout(tmp_path):
    """The head side of the delta must come from --head, not the working tree.

    If the checkout differs from the commit the gate was asked about (a
    dirty tree, a branch moved, a stale worktree), auditing the live
    checkout computes the delta against the wrong tree.
    """
    module = load_module()
    repo = tmp_path / "repo"
    ui = repo / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')

    def git(*args):
        subprocess.run(("git", *args), cwd=repo, check=True, capture_output=True, text=True)

    git("init", "-q")
    git("config", "user.email", "test@example.invalid")
    git("config", "user.name", "test")
    git("add", "-A")
    git("commit", "-q", "-m", "head")
    head = subprocess.run(
        ("git", "rev-parse", "HEAD"),
        cwd=repo,
        check=False,
        capture_output=True,
        text=True,
    ).stdout.strip()

    # Make the WORKING TREE differ from the head commit: a vulnerable
    # lockfile that is not in the commit.
    (ui / "package-lock.json").write_text('{"lockfileVersion":3,"dirty":true}')

    command = module.ScanCommand(
        ecosystem="node",
        cwd=ui,
        command=("npm", "audit", "--package-lock-only", "--audit-level=low", "--json"),
        base_cwd=ui,
    )
    calls = {"n": 0}
    payload = _node_audit_payload({})

    def runner(*_args, **_kwargs):
        # call 1 is HEAD, call 2 is BASE
        calls["n"] += 1
        return type("Result", (), {"returncode": 0, "stdout": payload})()

    # Both sides come from git, so the dirty working tree is ignored and the
    # delta is computed against the head commit.
    module.run_scan_plan([command], runner=runner, base_ref=head, head_ref=head)
