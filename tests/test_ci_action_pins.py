import json
import re
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
CI_WORKFLOW = WORKFLOWS / "ci.yml"
ACTION_KEY = re.compile(r"^\s*(?:-\s*)?uses:\s+(?P<ref>[^\s#]+)")
# The ref must be exactly 40 hex characters followed by a provenance comment.
# Hex is matched case-insensitively because GitHub resolves an uppercase SHA
# to the same commit, so rejecting it would be a false positive.
IMMUTABLE_ACTION = re.compile(r"^\s*(?:-\s*)?uses:\s+[^\s@]+@[0-9a-fA-F]{40}\s+#\s+\S.*$")


def _workflow(path: Path) -> dict:
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
        check=False,
        text=True,
    )
    assert result.returncode == 0, result.stderr
    return json.loads(result.stdout)


def test_all_workflow_actions_use_immutable_commits_with_provenance_comments():
    action_lines = []
    for workflow in sorted((*WORKFLOWS.glob("*.yml"), *WORKFLOWS.glob("*.yaml"))):
        for line_number, line in enumerate(workflow.read_text().splitlines(), start=1):
            match = ACTION_KEY.match(line)
            if match and not match.group("ref").startswith("./"):
                action_lines.append((workflow.relative_to(ROOT), line_number, line))

    assert action_lines, "workflows must invoke at least one external action"
    assert all(IMMUTABLE_ACTION.match(line) for _, _, line in action_lines), action_lines


def test_uppercase_sha_is_accepted_as_an_immutable_ref():
    """GitHub resolves an uppercase SHA to the same commit as its lowercase form."""
    line = "      - uses: actions/checkout@" + "A" * 40 + " # v4.4.0"
    assert IMMUTABLE_ACTION.match(line), "an uppercase SHA is a valid immutable ref"


def test_over_length_sha_is_rejected():
    """A 41-character hex string is not a commit SHA."""
    line = "      - uses: actions/checkout@" + "a" * 41 + " # v4.4.0"
    assert not IMMUTABLE_ACTION.match(line), "a 41-character ref must not be accepted"


def test_double_at_refs_are_not_mistaken_for_pins():
    """A ``uses:`` line with two ``@`` signs is malformed, not pinned.

    GitHub resolves the whole trailing token as the ref, so such a line never
    resolves to a real commit.
    """
    line = "      - uses: actions/checkout@" + "a" * 40 + "@" + "b" * 40 + " # v4.4.0"
    assert not IMMUTABLE_ACTION.match(line), "a double-@ ref must not be accepted as a pin"


def test_ci_checkouts_do_not_persist_credentials():
    workflow = _workflow(CI_WORKFLOW)
    checkout_steps = [
        step
        for job in workflow["jobs"].values()
        for step in job.get("steps", [])
        if step.get("uses", "").startswith("actions/checkout@")
    ]

    assert checkout_steps, "CI workflow must contain at least one checkout step"
    assert all(
        step.get("with", {}).get("persist-credentials") is False for step in checkout_steps
    ), checkout_steps


def test_ci_runs_action_pin_policy_in_hosted_python_job():
    workflow = _workflow(CI_WORKFLOW)
    steps = {step["name"]: step for step in workflow["jobs"]["python"]["steps"] if "name" in step}
    test_path = "tests/test_ci_action_pins.py"

    expected_commands = {
        "Ruff check dependency delta": "ruff check ",
        "Ruff format dependency delta": "ruff format --check ",
        "Run hostile dependency delta fixtures": "python3 -m pytest -q ",
    }
    for step_name, command in expected_commands.items():
        run = steps[step_name]["run"]
        assert run.startswith(command)
        assert test_path in run
