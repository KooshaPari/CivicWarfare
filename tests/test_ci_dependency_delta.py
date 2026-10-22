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
            ("npm", "audit", "--package-lock-only", "--audit-level=low"),
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
            ("npm", "audit", "--package-lock-only", "--audit-level=low"),
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

    plan = module.build_scan_plan(
        tmp_path,
        [
            "CivicSurvival/CivicSurvival.csproj",
            "CivicSurvival/UI/package.json",
        ],
    )

    assert plan == []


def test_civicignore_does_not_mask_unmatched_paths(tmp_path):
    module = load_module()
    (tmp_path / ".civicignore").write_text("CivicSurvival/CivicSurvival.csproj\n")
    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')

    plan = module.build_scan_plan(
        tmp_path,
        [
            "CivicSurvival/CivicSurvival.csproj",
            "CivicSurvival/UI/package-lock.json",
        ],
    )

    assert [(item.ecosystem, item.cwd) for item in plan] == [("node", ui)]


def test_civicignore_supports_fnmatch_globs(tmp_path):
    module = load_module()
    (tmp_path / ".civicignore").write_text("**/*.csproj\n")

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

    plan = module.build_scan_plan(
        tmp_path,
        [
            "CivicSurvival/CivicSurvival.csproj",
            "CivicSurvival/UI/package-lock.json",
        ],
    )

    assert [(item.ecosystem, item.cwd) for item in plan] == [("node", ui)]


def test_missing_civicignore_does_not_fail(tmp_path):
    module = load_module()
    assert not (tmp_path / ".civicignore").exists()

    ui = tmp_path / "CivicSurvival" / "UI"
    ui.mkdir(parents=True)
    (ui / "package.json").write_text('{"name":"civic-ui"}')
    (ui / "package-lock.json").write_text('{"lockfileVersion":3}')

    plan = module.build_scan_plan(
        tmp_path,
        ["CivicSurvival/UI/package-lock.json"],
    )

    assert len(plan) == 1
    assert plan[0].ecosystem == "node"


def test_civicignore_is_loaded_only_once_per_call(tmp_path):
    module = load_module()
    (tmp_path / ".civicignore").write_text("CivicSurvival/CivicSurvival.csproj\n")

    seen_loads: list[int] = []

    original = module._load_civicignore

    def counting(repo: Path) -> list[str]:
        seen_loads.append(1)
        return original(repo)

    module._load_civicignore = counting  # type: ignore[assignment]
    try:
        module.build_scan_plan(
            tmp_path,
            [
                "CivicSurvival/CivicSurvival.csproj",
                "CivicSurvival/CivicSurvival.csproj",
                "CivicSurvival/CivicSurvival.csproj",
            ],
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
        command=("npm", "audit", "--package-lock-only", "--audit-level=low"),
    )

    def failing_runner(*_args, **_kwargs):
        return type("Result", (), {"returncode": 1})()

    with pytest.raises(module.DependencyDeltaError, match="node scanner failed"):
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
    assert "|| echo" not in python_job
    assert 'if [ "$name" = "security" ]; then' in workflow
    assert 'if [ "$event_name" = "pull_request" ] && [ "$name" = "dep-review" ]; then' in workflow
    assert 'if [ "$required" -eq 1 ] && [ "$result" != "success" ]; then' in workflow
    assert 'lint_result="${{ needs.lint.result }}"' in workflow
    assert 'if [ "$lint_result" != "success" ]; then' in workflow
