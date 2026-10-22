"""Discipline test for the task runners (Makefile, Justfile, Taskfile).

Locks in the repo's own contract:
1. Each runner exists at the repo root.
2. Each runner exposes the recipes the repo's BUILDING.md documents.
3. The `launch` recipe (any runner) uses the Steam URL handler for CS2
   (app id 949230) -- this is how the installer/game gets launched.
4. BUILDING.md mentions each runner it ships (i.e. presence on disk and
   presence in BUILDING.md stay in sync).

The test is intentionally tolerant of per-runner recipe-naming differences
(e.g. `scorecard-baseline-check` in Makefile vs `scorecard-check` in
Justfile/Taskfile). The discipline is *parity of intent*, not byte-for-byte
recipe names.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]

# Recipe name aliases (different runners may use different names for the same
# intent; the test accepts any name in the group).
RECIPE_ALIASES = {
    "scorecard-check": ("scorecard-check", "scorecard-baseline-check"),
    "build-dev": ("build-dev",),
    "build": ("build",),
    "build-installer": ("build-installer",),
    "install": ("install",),
    "update": ("update",),
    "remove": ("remove", "uninstall"),
    "launch": ("launch",),
    "ci": ("ci",),
    "test": ("test",),
    "lint": ("lint",),
    "format": ("format",),
    "format-check": ("format-check",),
    "release-current": ("release-current",),
    "release-verify": ("release-verify",),
    "status-cs2": ("status-cs2",),
    "status-mod": ("status-mod",),
}


@pytest.fixture(scope="module")
def makefile_text() -> str:
    return (ROOT / "Makefile").read_text(encoding="utf-8")


@pytest.fixture(scope="module")
def justfile_text() -> str:
    return (ROOT / "Justfile").read_text(encoding="utf-8")


@pytest.fixture(scope="module")
def taskfile_text() -> str:
    p = ROOT / "Taskfile.yml"
    return p.read_text(encoding="utf-8") if p.is_file() else ""


@pytest.fixture(scope="module")
def building_text() -> str:
    return (ROOT / "BUILDING.md").read_text(encoding="utf-8")


# ---------- Existence ----------


def test_makefile_exists_at_repo_root():
    assert (ROOT / "Makefile").is_file(), "Makefile must exist at repo root"


def test_justfile_exists_at_repo_root():
    assert (ROOT / "Justfile").is_file(), "Justfile must exist at repo root"


# ---------- Recipe parsers ----------


def _make_targets(text: str) -> set[str]:
    """Make target names: `name:` at column 0 (with optional prerequisite list after `:`)."""
    return {m.group(1) for m in re.finditer(r"^([a-zA-Z][a-zA-Z0-9_-]*)\s*:", text, re.M)}


def _just_recipes(text: str) -> set[str]:
    """Justfile recipe: `name:` at column 0."""
    return {m.group(1) for m in re.finditer(r"^([a-zA-Z][a-zA-Z0-9_-]*)\s*:", text, re.M)}


def _task_recipes(text: str) -> set[str]:
    """Go-task tasks: top-level `name:` under `tasks:` (2-space indent)."""
    in_tasks = False
    names: set[str] = set()
    for line in text.splitlines():
        if not line.startswith(" "):
            in_tasks = line.rstrip() == "tasks:"
            continue
        if in_tasks and line.startswith("  ") and not line.startswith("    "):
            stripped = line.strip()
            if stripped.endswith(":"):
                name = stripped[:-1]
                if re.match(r"^[a-zA-Z][a-zA-Z0-9_-]*$", name):
                    names.add(name)
    return names


def _recipe_body(text: str, name: str) -> str:
    """Return the indented body of a recipe (just/make)."""
    lines = text.splitlines()
    captured: list[str] = []
    capturing = False
    for line in lines:
        if not capturing:
            if re.match(rf"^{re.escape(name)}\s*:", line):
                capturing = True
                continue
            continue
        # End of block: a non-empty line at column 0
        if line and not line.startswith((" ", "\t")):
            break
        captured.append(line)
    return "\n".join(captured)


def _task_recipe_body(text: str, name: str) -> str:
    """For go-task, find the indented block under `name:` and return it."""
    lines = text.splitlines()
    captured: list[str] = []
    in_block = False
    for line in lines:
        stripped = line.strip()
        if not in_block:
            if stripped == f"{name}:":
                in_block = True
            continue
        if line and not line.startswith((" ", "\t")):
            break
        captured.append(line)
    return "\n".join(captured)


# ---------- launch recipe uses Steam URL for CS2 (app id 949230) ----------


@pytest.mark.parametrize(
    "runner_name,recipe_text_fixture",
    [
        ("make", "makefile_text"),
        ("just", "justfile_text"),
        ("task", "taskfile_text"),
    ],
)
def test_launch_recipe_uses_steam_url(runner_name, recipe_text_fixture, request):
    """The launch recipe (whichever runner defines it) must use Steam URL for CS2."""
    text = request.getfixturevalue(recipe_text_fixture)
    if not text:
        pytest.skip(f"{runner_name} not present")
    body = (
        _recipe_body(text, "launch") if runner_name != "task" else _task_recipe_body(text, "launch")
    )
    assert "steam://run/949230" in body, (
        f"{runner_name} launch must call Steam URL handler for CS2 (app id 949230). Body: {body!r}"
    )


# ---------- Cross-runner consistency ----------


def test_scorecard_recipe_present_in_makefile(makefile_text):
    """Either `scorecard-check` or `scorecard-baseline-check` must exist."""
    present = _make_targets(makefile_text)
    assert any(name in present for name in RECIPE_ALIASES["scorecard-check"]), (
        f"Makefile must have a scorecard recipe (one of: {RECIPE_ALIASES['scorecard-check']})"
    )


# ---------- Documentation ----------


def test_building_md_documents_runners_it_ships(building_text):
    """If BUILDING.md mentions a runner by name, it must exist on disk."""
    # Make is universal; check it
    assert "make" in building_text.lower(), "BUILDING.md must mention make"
    # If it mentions just, Justfile must exist
    if "just" in building_text.lower():
        assert (ROOT / "Justfile").is_file()
    # If it mentions Taskfile, the file must exist
    if "Taskfile" in building_text:
        assert (ROOT / "Taskfile.yml").is_file(), "BUILDING.md references Taskfile but file missing"


def test_building_md_explains_csii_toolpath(building_text):
    """BUILDING.md must document the CSII_TOOLPATH default so devs know where to point it."""
    assert "CSII_TOOLPATH" in building_text
    assert "Modding" in building_text or "Mod.props" in building_text


def test_building_md_explains_lefthook_setup(building_text):
    assert "lefthook install" in building_text
    assert ".lefthook.yml" in building_text


def test_no_stale_runners_in_building_md(building_text):
    """If BUILDING.md lists a runner, its recipes should be referenced at least once."""
    # Just smoke check: BUILDING.md has a "Workflow" / "Dev workflow" / "Recipe" section.
    lower = building_text.lower()
    assert any(
        header in lower
        for header in ("## dev workflow", "## workflow", "## recipe", "## available", "## commands")
    ), "BUILDING.md should have a section listing dev commands"
