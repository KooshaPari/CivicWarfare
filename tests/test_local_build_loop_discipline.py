"""Discipline suite for the local dev-build loop.

These tests lock in the actual preconditions for the build to succeed.
Every one of them is grounded in a real failure observed during the
2026-09-04 build attempt, so a future contributor hitting one of these
errors gets a precise pointer instead of a 4-hour debug spiral.

The build needs four env vars set (CSII_TOOLPATH, CSII_UNITYMODPROJECTPATH,
CSII_ENTITIESVERSION, CSII_MODPOSTPROCESSORPATH) plus a Unity Editor
invocation that actually materializes Library/PackageCache/com.unity.entities@*
so the Mod.props source-generator references resolve.

Failing these tests means the dev-build cannot succeed and the contributor
should either:
  1. Trigger the GitHub release pipeline (release.yml) -- it has all four env
     vars + a clean Unity Editor first-run that materializes PackageCache.
  2. Open the Unity mod project once in Unity Editor at
     $(CSII_UNITYMODPROJECTPATH), run it to completion (so Library/PackageCache
     is populated), then retry the dotnet build.

Also covers:
  - CS8701 (default interface impls) -- fixed by pinning LangVersion 9.0 in
    the csproj, NOT by overriding Mod.props.
  - 6 source-level interfaces that use default interface impls on net48
    (pre-existing code; not vacuous -- they actually fail to compile).
"""

from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
CSPROJ = ROOT / "CivicSurvival" / "CivicSurvival.csproj"


def _read(p: Path) -> str:
    return p.read_text(encoding="utf-8", errors="replace")


# ---------------------------------------------------------------------------
# Toolchain + tool discovery
# ---------------------------------------------------------------------------


def test_csproj_imports_mod_props() -> None:
    """CivicSurvival.csproj must import Mod.props from the CS2 modding toolchain.

    Without the import, none of the Mod.targets orchestration runs
    (Unity analyzer references, ModPostProcessor, manifest stamping) -- the
    build appears to succeed but produces a non-loadable DLL.
    """
    text = _read(CSPROJ)
    # The actual MSBuild expression wraps an EnvVar function call in $(...),
    # so the closing `)` is doubled: $((...))\\Mod.props. Accept both the
    # function-call form (the real one) and the simpler literal form.
    fn_pat = re.compile(
        r"<Import\s+[^>]*?System\.Environment]::GetEnvironmentVariable\("
        r'\s*[\'"]?CSII_TOOLPATH[\'"]?'
    )
    literal_pat = re.compile(r'<Import\s+Project="\$\(CSII_TOOLPATH\)[\\/]+Mod\.props"')
    has_import = fn_pat.search(text) or literal_pat.search(text)
    assert has_import, (
        "CivicSurvival.csproj must import Mod.props via CSII_TOOLPATH. "
        f"Found import lines: {[l.strip() for l in text.splitlines() if l.strip().startswith('<Import')]}"
    )


def test_csproj_or_mod_props_pins_langversion_9() -> None:
    """LangVersion 9.0 must be pinned somewhere reachable (csproj or Mod.props).

    SDK 11 preview defaults to C# 12, which silently enables default
    interface implementations on .NET Framework 4.8, producing 6x CS8701 in:
        IPostLoadValidation.cs (lines 23, 54)
        IInitializable.cs (line 37)
        IBuildingRefRebindOwner.cs (line 15)
        INarrativeResolver.cs (any interface with default impl)

    The csproj inherits LangVersion from Mod.props (which sets 9.0) OR must
    pin it locally. Either contract holds.
    """
    csproj_text = _read(CSPROJ)
    # Either the csproj pins it locally...
    if re.search(r"<LangVersion>\s*9\.0\s*</LangVersion>", csproj_text):
        return
    # ...OR the imported Mod.props sets it (resolved at build time).
    mod_props_path = os.environ.get("CSII_TOOLPATH", "")
    if mod_props_path:
        candidate = Path(mod_props_path) / "Mod.props"
        if candidate.exists():
            text = candidate.read_text(encoding="utf-8", errors="replace")
            if re.search(r"<LangVersion>\s*9\.0\s*</LangVersion>", text):
                return
    pytest.fail(
        "LangVersion 9.0 must be pinned in CivicSurvival.csproj or in the "
        "imported Mod.props (CSII_TOOLPATH env var). Default SDK LangVersion "
        "enables default interface implementations on net48, producing 6x "
        "CS8701 errors."
    )


def test_csproj_does_not_override_game_managed_path_to_dotnet() -> None:
    """CivicSurvival.csproj must not inject $(GameManagedPath) into .NET targets.

    The Mod.props chain handles ManagedPath resolution. Overriding it in
    the csproj breaks the chain because the ModPostProcessor.exe reads
    $(ManagedPath) from the .binlog, not from the csproj.
    """
    text = _read(CSPROJ)
    # GameManagedPath should only appear inside <Reference> HintPath elements,
    # never as a <ManagedPath> override.
    bad = re.search(r"<ManagedPath>\s*\$\(GameManagedPath\)\s*</ManagedPath>", text)
    assert not bad, (
        "CivicSurvival.csproj must not override $(ManagedPath) with "
        "$(GameManagedPath). Let Mod.props handle resolution."
    )


# ---------------------------------------------------------------------------
# Source-generator preconditions
# ---------------------------------------------------------------------------


def test_csproj_or_targets_uses_mod_post_processor_path() -> None:
    """The RunModPostProcessor target path must be reachable from the build.

    Mod.targets:100-104 requires $(ModPostProcessorPath) (or
    $(ModPublisherPath)) to be set. The csproj reads it via an
    EnvironmentVariableTarget.User function call (because of legacy
    Windows registry storage), so the test asserts the call site exists
    in either CivicSurvival.csproj or the imported Mod.targets.
    """
    csproj_text = _read(CSPROJ)
    if "ModPostProcessorPath" in csproj_text or "ModPublisherPath" in csproj_text:
        return
    # Fall back to checking the imported Mod.targets (the actual definition site).
    mod_targets = (
        Path(os.environ.get("CSII_TOOLPATH", "")) / "Mod.targets"
        if os.environ.get("CSII_TOOLPATH")
        else None
    )
    if mod_targets and mod_targets.exists():
        mt_text = mod_targets.read_text(encoding="utf-8", errors="replace")
        if "ModPostProcessorPath" in mt_text or "ModPublisherPath" in mt_text:
            return
    pytest.fail(
        "CivicSurvival.csproj or the imported Mod.targets must reference "
        "$(ModPostProcessorPath) or $(ModPublisherPath) for the "
        "RunModPostProcessor target to function."
    )


# ---------------------------------------------------------------------------
# Discipline for the local-build run-script path
# ---------------------------------------------------------------------------


def test_building_md_documents_local_build_loop() -> None:
    """BUILDING.md must document the local-build loop AND its known blockers.

    Specifically:
    - The four required env vars (CSII_TOOLPATH, CSII_UNITYMODPROJECTPATH,
      CSII_ENTITIESVERSION, CSII_MODPOSTPROCESSORPATH).
    - The fact that Unity Editor must be opened once on the mod project so
      Library/PackageCache/com.unity.entities@* gets materialized -- the
      source-generators referenced by Mod.props can't resolve otherwise.
    - The release-pipeline path as the canonical alternative.
    """
    text = _read(ROOT / "BUILDING.md")
    for var in (
        "CSII_TOOLPATH",
        "CSII_UNITYMODPROJECTPATH",
        "CSII_ENTITIESVERSION",
        "CSII_MODPOSTPROCESSORPATH",
    ):
        assert var in text, f"BUILDING.md must document the {var} env var for the local build loop."
    assert "PackageCache" in text, (
        "BUILDING.md must explain that Unity Editor must be opened once "
        "on the mod project so Library/PackageCache/com.unity.entities@* "
        "is materialized; otherwise the source-generators can't resolve."
    )
    assert "release" in text.lower(), (
        "BUILDING.md must reference the release pipeline as the canonical "
        "alternative to the local build loop."
    )


# ---------------------------------------------------------------------------
# Public surface: the release pipeline is the supported build path
# ---------------------------------------------------------------------------


def test_release_yml_uses_self_hosted_runner() -> None:
    """.github/workflows/release.yml must declare a self-hosted runner.

    The public source mirror has no public CI minutes budget; the release
    pipeline uses a self-hosted runner that has Unity Editor installed
    and the proper toolchain configuration. A workflow that defaults to
    ubuntu-latest will fail with "unity: command not found" or
    "CSII_ENTITIESVERSION: incorrect path(s)".
    """
    release = ROOT / ".github" / "workflows" / "release.yml"
    text = _read(release)
    # The release pipeline must declare at least one job on self-hosted.
    assert re.search(r"runs-on:\s*\[?\s*self-hosted", text) or re.search(
        r"runs-on:\s*self-hosted", text
    ), (
        "release.yml must declare a self-hosted runner for the build job; "
        "the public mirror cannot host Unity Editor + CSII toolchain env "
        "vars on a GitHub-hosted runner."
    )


def test_release_yml_exposes_modpostprocessor_env_var() -> None:
    """release.yml must set CSII_MODPOSTPROCESSORPATH for the build job.

    Without this env var, the Mod.targets RunModPostProcessor target
    silently skips, and the built DLL is not loadable in CS2 (no
    source-generator-injected methods).
    """
    release = ROOT / ".github" / "workflows" / "release.yml"
    text = _read(release)
    # Either explicit env: entry OR a vars: reference is fine.
    assert "CSII_MODPOSTPROCESSORPATH" in text, (
        "release.yml must set CSII_MODPOSTPROCESSORPATH on the build job "
        "so the RunModPostProcessor MSBuild target can run."
    )
    assert "CSII_ENTITIESVERSION" in text, (
        "release.yml must set CSII_ENTITIESVERSION on the build job."
    )


# ---------------------------------------------------------------------------
# Run-loop check: catch the most common error pattern (Unity PackageCache)
# ---------------------------------------------------------------------------


def test_civicignore_excludes_unity_library() -> None:
    """.gitignore must exclude Unity's Library/ directory.

    The Unity mod project at $(CSII_UNITYMODPROJECTPATH) has a Library/
    directory containing PackageCache/ + ScriptAssemblies/ + Bee/artifacts/
    that are all generated, multi-GB. Committing it would massively bloat
    the repo.
    """
    text = _read(ROOT / ".gitignore")
    has_library = any(
        re.search(rf"^{pat}\s*$", text, re.MULTILINE)
        for pat in (r"Library/", r"Library/\*\*", r"/Library/", r"\*\*/Library/")
    )
    # The UnityModsProject lives outside the repo so a /Library/ ignore is
    # not strictly needed, but if anyone copies the project inside it must
    # be ignored.
    if (ROOT / "CivicSurvival" / "UnityModsProject").exists() or (
        ROOT / "UnityModsProject"
    ).exists():
        assert has_library, (
            "Unity Library/ directory must be in .gitignore when "
            "UnityModsProject/ is inside the repo."
        )
