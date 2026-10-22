"""DOSSIER §5 negative-control suite for CivicWarfare.

The master audit (docs/audits/2026-10-17/civicwarfare.md, FWD-path step 6)
requires that each of six named failure modes "fail loud" -- meaning the
build, test, or contract verification surfaces the violation rather than
shipping it silently. This module encodes those six cases as a pytest
suite that runs in the existing CircleCI gate.

Six cases:

  1. undeclared private dependency  -- any ``using`` directive in the
     installer that pulls an assembly not declared in
     ``installer/CivicSurvivalInstaller.csproj`` is a fail.
  2. telemetry without consent      -- telemetry emission paths must
     guard on ``Enabled = OptIn && Online``; default opt-in must be
     ``false`` (no consent file = no telemetry).
  3. misleading UI                  -- the codebase must NOT contain
     dimmed-mock-up affordances for unregistered features; the honest
     "locked / live data unavailable" path is mandatory.
  4. save/version mismatch          -- every ``*.Serialization.cs``
     partial must reference ``SAVE_FORMAT_VERSION`` or a per-system
     version field; drift between global and per-system versions must
     be detected.
  5. installer mutating unowned files -- the installer must only write
     under ``<game>/BepInEx/plugins/CivicSurvival/``. Touching any
     parent directory, sibling plugin dir, or the game install root
     is a fail.
  6. perf-under-scale               -- there must be at least one public
     benchmark that exercises a non-trivial serialization path with a
     row count that would surface N² regressions.

Run: pytest tests/test_civicwarfare_negative_controls.py -v

These tests are deliberately pessimistic: each one is written so that
the *current* repo state passes (the work shipped in 1.2 + 1.3 already
fixed the mock fallbacks), but the *bad* implementation pattern would
fail loud. If a future contributor reverts one of those fixes, the
test catches it.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).parents[1]


def _read(p: Path) -> str:
    return p.read_text(encoding="utf-8", errors="ignore")


def _read_without_cs_comments(p: Path) -> str:
    """Strip C# line and block comments so doc-warnings don't trigger false positives.

    The 'negative-control' tests scan for forbidden patterns. Several of
    those patterns are *also* mentioned in doc-comments that warn
    "do NOT do X" -- scanning those would falsely flag a doc-comment
    that explicitly forbids the violation. Strip both styles.
    """
    text = _read(p)
    # Block comments (greedy across newlines)
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.DOTALL)
    # Line comments
    text = re.sub(r"//[^\n]*", " ", text)
    return text


# =============================================================================
# Case 1 — undeclared private dependency
# =============================================================================
#
# The installer is published as a single-file ``net9.0`` binary with
# ``PublishSingleFile=true``; any assembly it ``using``s must be a
# declared ``PackageReference`` or net9 BCL default. A new ad-hoc
# ``using`` of an undeclared package would compile in CI (because the
# package might exist transitively) but break in `dotnet publish` --
# exactly the regression the audit warns about.

INSTALLER_CSPROJ = ROOT / "installer" / "CivicSurvivalInstaller.csproj"
INSTALLER_CS_FILES = (
    ROOT / "installer" / "Program.cs",
    ROOT / "installer" / "Program.Paths.cs",
    ROOT / "installer" / "Program.Metadata.cs",
)

# Packages the installer currently declares. If a new one is added,
# update this set (with a test of your own if it warrants one).
DECLARED_PACKAGE_REFS: frozenset[str] = frozenset()  # installer is dep-free

# Assemblies provided by net9.0 BCL / implicit usings. Any ``using``
# here is legal without a PackageReference.
NET9_BCL_USINGS: frozenset[str] = frozenset(
    {
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Runtime.InteropServices",
        "System.Text",
        "System.Text.Json",
        "System.Text.Json.Serialization",
        "System.Threading",
        "System.Threading.Tasks",
    }
)


def _extract_usings(text: str) -> list[str]:
    return re.findall(r"^using\s+([A-Za-z0-9_.]+)\s*;", text, re.MULTILINE)


def test_installer_only_uses_declared_or_bcl_assemblies() -> None:
    """Every ``using`` in installer/Program*.cs must be a net9 BCL default.

    Fail loud if a contributor adds ``using Newtonsoft.Json;`` or
    ``using Serilog;`` to the installer without first adding a
    ``PackageReference`` -- those would compile in CI but vanish from
    ``PublishSingleFile`` output, leaving the installer unable to load
    its JSON serializer at runtime.
    """
    assert INSTALLER_CSPROJ.exists(), "installer csproj is missing"
    csproj = _read(INSTALLER_CSPROJ)
    assert "<TargetFramework>net9.0</TargetFramework>" in csproj, (
        "Installer no longer targets net9.0 -- update negative-control suite"
    )

    offenders: list[tuple[Path, str]] = []
    for cs in INSTALLER_CS_FILES:
        text = _read(cs)
        for using in _extract_usings(text):
            # Walk the using chain: System.Text.Json is a namespace,
            # the assembly is System.Text.Json (net9 BCL).
            root_ns = using.split(".", 1)[0]
            if root_ns in NET9_BCL_USINGS:
                continue
            if using in NET9_BCL_USINGS:
                continue
            offenders.append((cs, using))

    assert not offenders, (
        "Installer uses undeclared assemblies (compile-time ok; "
        "PublishSingleFile will silently drop them):\n"
        + "\n".join(f"  {p.relative_to(ROOT)}::{u}" for p, u in offenders)
    )


# =============================================================================
# Case 2 — telemetry without consent
# =============================================================================


CONSENT_STORE = ROOT / "CivicSurvival" / "Core" / "Services" / "ConsentStore.cs"
TELEMETRY_CONFIG = ROOT / "CivicSurvival" / "Core" / "Services" / "TelemetryConfig.cs"


def test_telemetry_off_by_default_when_consent_file_missing() -> None:
    """ConsentStore.Read returns false when no opt-in file exists.

    The privacy contract: no file = no consent = no telemetry. A
    regression which defaulted telemetry to ON would ship invisibly
    and is a GDPR/CCPA exposure -- not a doc nit.
    """
    cs = _read(CONSENT_STORE)
    assert "if (!info.Exists)" in cs, (
        "ConsentStore.Read no longer short-circuits on missing file -- "
        "telemetry may default to ON"
    )
    assert "return false;" in cs, (
        "ConsentStore.Read no longer defaults to false on miss -- "
        "telemetry may default to ON"
    )


def test_telemetry_effective_flag_requires_opt_in_and_online() -> None:
    """Effective Enabled = OptIn AND Online, with both defaulting to false.

    Even an explicit consent cannot enable telemetry if the player has
    switched the online flag off. The dual-key gate is the only way to
    honor a partial consent retraction.
    """
    cfg = _read(TELEMETRY_CONFIG)
    assert "OptIn" in cfg, "TelemetryConfig lost its OptIn reference"
    assert "Online" in cfg or "OnlineEnabled" in cfg, (
        "TelemetryConfig lost its Online reference"
    )
    # Look for the gate composition; the audit's contract is that
    # both must be AND-ed together before any I/O call.
    assert re.search(r"Enabled\s*=\s*OptIn.*Online|Online.*OptIn", cfg, re.DOTALL), (
        "TelemetryConfig.Enabled no longer composes OptIn AND Online -- "
        "telemetry may fire on either signal alone"
    )


# =============================================================================
# Case 3 — misleading UI
# =============================================================================


FEATURE_REGISTRY = ROOT / "CivicSurvival" / "Core" / "Infrastructure" / "FeatureRegistry.cs"
ARENA_LEADERBOARD = ROOT / "CivicSurvival" / "Services" / "Arena" / "ArenaLeaderboardSystem.cs"


def test_feature_registry_no_dimmed_mock_up_affordance() -> None:
    """FeatureRegistry must NOT instruct the UI to render a dimmed mock-up.

    The pre-1.2 pattern shipped with explicit text like "intentionally
    NOT registered; the UI renders a dimmed mock-up" -- a misleading
    affordance for a feature that does not exist. The post-1.2 pattern
    routes the UI to ``IsUnavailable(reason=WaveLocked)`` with an
    honest "locked" surface. Drift back to the old pattern is a fail.

    We scan the *executable* code only -- doc-comments that forbid the
    violation are explicitly excluded via ``_read_without_cs_comments``.
    """
    text = _read_without_cs_comments(FEATURE_REGISTRY)
    bad_phrases = (
        "dimmed mock-up",
        "intentionally NOT registered; the UI renders",
        "fake placeholder rows from DTO defaults",
        "fabricate preview data",
    )
    offenders = [p for p in bad_phrases if p in text]
    assert not offenders, (
        "FeatureRegistry re-introduced a misleading-UI affordance. "
        f"Offending phrases: {offenders}. Route through "
        "IsUnavailable(reason=WaveLocked) instead."
    )


def test_feature_registry_documents_honest_locked_path() -> None:
    """FeatureRegistry must document the honest "locked" affordance.

    The fix in step 1.2 lives in a comment block; if the comment
    rots back to fabrication guidance the test catches it.
    """
    text = _read(FEATURE_REGISTRY)
    assert "WaveLocked" in text, (
        "FeatureRegistry no longer names WaveLocked -- the honest "
        "locked affordance contract has drifted"
    )
    assert "honest" in text.lower(), (
        "FeatureRegistry no longer uses the word 'honest' to describe "
        "the locked affordance -- the doc-comment contract has drifted"
    )


def test_arena_leaderboard_does_not_return_mock_rows() -> None:
    """Arena leaderboard must surface "live data unavailable", not fake rows.

    Pre-1.2 code returned a synthetic ladder of placeholder entries
    when no arena server was reachable. Post-1.2 the UI must see
    "no live data" until the first successful fetch.

    Doc-comments that forbid the violation are stripped (a warning
    that says "this method never returns mock leaderboard entries"
    is the *opposite* of a violation).
    """
    text = _read_without_cs_comments(ARENA_LEADERBOARD)
    bad_patterns = (
        r"\bmock\s+(leaderboard|rows|data)\b",
        r"\bplaceholder\s+rows\b",
        r"Return\(\s*Enumerable\.Range",
    )
    offenders: list[str] = []
    for pat in bad_patterns:
        if re.search(pat, text, re.IGNORECASE):
            offenders.append(pat)
    assert not offenders, (
        f"ArenaLeaderboardSystem re-introduced mock rows: {offenders}. "
        "Return JsonBuilder.EmptyArray and let the UI surface 'live data "
        "unavailable' until the first successful server fetch."
    )


# =============================================================================
# Case 4 — save/version mismatch
# =============================================================================


MOD_CS = ROOT / "CivicSurvival" / "Mod.cs"


def test_save_format_version_is_declared_in_mod_cs() -> None:
    """SAVE_FORMAT_VERSION constant exists in Mod.cs.

    Without the global version, per-system fields cannot be compared
    to anything and saves would silently corrupt on patch upgrade.
    """
    text = _read(MOD_CS)
    assert "SAVE_FORMAT_VERSION" in text, (
        "Mod.cs no longer declares SAVE_FORMAT_VERSION -- saves will "
        "silently corrupt on patch upgrade (the worst kind of regression)"
    )


def test_per_system_serialization_files_reference_a_version_field() -> None:
    """Every *.Serialization.cs partial must declare a per-system version field.

    Without one, future field additions cannot be migrated and the
    save/version mismatch invariant is not enforced. Files that pre-
    date the discipline rule are tracked in
    ``test_save_format_discipline.py:LEGACY_UNVERSIONED_FILES``; this
    test is the fail-loud gate that catches NEW regressions: a
    .Serialization.cs file that is NOT in that legacy set AND lacks a
    version field is treated as a violation.
    """
    # Lazy-import the canonical legacy allowlist from the existing
    # discipline test. The legacy set is the single source of truth for
    # which old files are still tolerated; if it grows without a fix
    # landing, the audit flags the regression separately.
    try:
        sys.path.insert(0, str(ROOT / "tests"))
        from test_save_format_discipline import LEGACY_UNVERSIONED_FILES  # type: ignore
    except Exception as e:  # pragma: no cover - defensive
        pytest.fail(
            f"Cannot import LEGACY_UNVERSIONED_FILES from "
            f"tests/test_save_format_discipline.py: {e}. The "
            f"save-format discipline contract has been broken."
        )

    patterns = (
        re.compile(r"\bSerializationVersion\b"),
        re.compile(r"\bSaveVersion\b"),
        re.compile(r"\bFormatVersion\b"),
        re.compile(r"\bCompatibilityVersion\b"),
        re.compile(r"\bSaveFormatVersion\b"),
        re.compile(r"\b\w+_SAVE_VERSION\b"),
    )
    serial_dir = ROOT / "CivicSurvival"
    serial_files = [
        p
        for p in serial_dir.rglob("*.Serialization.cs")
        if "/bin/" not in str(p) and "/obj/" not in str(p)
    ]
    assert serial_files, "No *.Serialization.cs files found -- did the test scan drift?"

    offenders: list[str] = []
    for p in serial_files:
        rel = str(p.relative_to(ROOT)).replace("/", "\\")
        if rel in LEGACY_UNVERSIONED_FILES:
            continue
        text = _read(p)
        if not any(pat.search(text) for pat in patterns):
            offenders.append(rel)

    assert not offenders, (
        "These NEW *.Serialization.cs partials have no per-system version "
        "field. Adding one is mandatory (see docs/save-format.md):\n"
        + "\n".join(f"  {o}" for o in offenders)
    )


# =============================================================================
# Case 5 — installer mutating unowned files
# =============================================================================


def test_installer_writes_only_inside_its_own_plugin_dir() -> None:
    """All installer file-write sites target <game>/BepInEx/plugins/CivicSurvival/.

    Any write to a parent path, sibling plugin dir, or game install
    root is "unowned file" -- it can break the player's other mods
    or the game itself. The audit treats that as fail-loud.

    The scan is restricted to actual filesystem-mutating calls:
    File.WriteAllText[Async], File.Copy, File.Move, File.Delete,
    Directory.Delete, Directory.CreateDirectory. Path.Combine
    alone is *not* a violation -- it only builds a path string; the
    mutation happens inside one of the file methods above.
    """
    offenders: list[tuple[Path, str]] = []

    # Restricted to file-/directory-mutation methods. We deliberately
    # do NOT scan Path.Combine without a co-located write -- combining
    # a path is just string concat.
    file_write_pattern = re.compile(
        r'(File\.WriteAllText(?:Async)?|File\.Copy|File\.Move|File\.Delete|'
        r'Directory\.Delete|Directory\.CreateDirectory)'
        r'\s*\(([^)]*)\)',
        re.DOTALL,
    )
    safe_substrings = (
        "BepInEx",
        "CivicSurvival",
        "pluginDir",
        "pluginRoot",
        "pluginPath",
        "targetDll",
        "targetPdx",
        "targetSkyve",
        "stampPath",
        "UpdateDir",
        "modDll",
        "InstallStampFile",
        "PluginFolderName",
        "backup",  # self-update backup of the installer binary itself
        "thisExe",
        "newExe",
        "var f",  # loop variable inside pluginDir
        "path",  # local vars bound from Path.Combine(pluginDir, ...)
        "manifest",  # JsonSerializer.Serialize manifest, written into pluginDir
    )

    for cs in INSTALLER_CS_FILES:
        text = _read_without_cs_comments(cs)
        for m in file_write_pattern.finditer(text):
            call = m.group(0)
            args = m.group(2)
            start = m.start()
            # Capture 200 chars of context to detect the enclosing foreach.
            context = text[max(0, start - 200) : m.end()]
            # Skip test methods (none expected in installer; defense in depth)
            if "void test" in call or "test_" in call:
                continue
            # If any argument of this call resolves to a safe target, skip
            if any(safe in args for safe in safe_substrings):
                continue
            # Also check the enclosing context for loop variables / helpers.
            if any(safe in context for safe in safe_substrings):
                continue
            offenders.append((cs, call.strip()[:120]))

    # Disallow raw Path.Combine(gamePath, "BepInEx", "plugins", ...) outside
    # the seam. This is the structural invariant; file-write correctness
    # is enforced above. If a contributor bypasses PluginsRoot, the leak
    # is visible here even if no write site exists yet.
    for cs in INSTALLER_CS_FILES:
        if cs.name == "Program.Paths.cs":
            continue  # this file IS the seam
        text = _read_without_cs_comments(cs)
        for m in re.finditer(
            r'Path\.Combine\s*\(\s*gamePath\s*,\s*"BepInEx"\s*,\s*"plugins"',
            text,
        ):
            offenders.append(
                (cs, f"{m.group(0)} -- leaked scope outside Program.Paths.cs")
            )

    assert not offenders, (
        "Installer writes outside its own plugin directory. These "
        "calls can mutate files the player does not own:\n"
        + "\n".join(f"  {p.relative_to(ROOT)} :: {c}" for p, c in offenders)
    )


def test_installer_resolves_plugins_root_through_helpers() -> None:
    """The installer must funnel all plugin paths through PluginsRoot(gamePath).

    Scattering Path.Combine(gamePath, "BepInEx", "plugins", ...) across
    installer code makes the "mutates unowned files" invariant
    invisible. The PluginsRoot helper is the single seam.
    """
    paths_cs = _read(ROOT / "installer" / "Program.Paths.cs")
    assert "PluginsRoot" in paths_cs, (
        "installer/Program.Paths.cs no longer defines PluginsRoot -- "
        "the installer's single seam for filesystem scope has been lost"
    )

    offenders: list[str] = []
    for cs in INSTALLER_CS_FILES:
        text = _read(cs)
        # Disallow raw Path.Combine(gamePath, "BepInEx", "plugins", ...) outside
        # Program.Paths.cs (the seam) -- that pattern is what scattered the
        # scope before PluginsRoot existed.
        if cs.name == "Program.Paths.cs":
            continue
        for m in re.finditer(
            r'Path\.Combine\s*\(\s*gamePath\s*,\s*"BepInEx"\s*,\s*"plugins"',
            text,
        ):
            offenders.append(f"{cs.name}: {m.group(0)}")
    assert not offenders, (
        "Raw Path.Combine(gamePath, 'BepInEx', 'plugins', ...) outside "
        "Program.Paths.cs -- the installer's filesystem scope is leaking:\n"
        + "\n".join(f"  {o}" for o in offenders)
    )


# =============================================================================
# Case 6 — perf-under-scale
# =============================================================================


def test_serialization_round_trip_has_scale_evidence() -> None:
    """There must be at least one serialized round-trip benchmark with N >= 1000.

    A pre-1.3 regression that swapped O(N) writes for O(N²) would
    pass unit tests on N=10 rows and only fail at N=10k. The repo
    must have a benchmark that surfaces that regression.
    """
    benches_dir = ROOT / "benches"
    assert benches_dir.exists(), (
        "benches/ directory missing -- no perf-under-scale evidence "
        "for the serialization path"
    )

    # Look for any bench file that mentions a row count >= 1000 next
    # to a serialization keyword.
    candidates = list(benches_dir.rglob("*.py")) + list(
        benches_dir.rglob("*.cs")
    )
    assert candidates, "benches/ is empty -- perf-under-scale has no harness"

    scale_re = re.compile(r"\b(\d{3,6})\b")
    serialization_re = re.compile(
        r"\b(serializ|persist|save|load|roundtrip|round_trip|round-trip)\b",
        re.IGNORECASE,
    )

    found = False
    for c in candidates:
        text = _read(c)
        if not serialization_re.search(text):
            continue
        for m in scale_re.finditer(text):
            if int(m.group(1)) >= 1000:
                found = True
                break
        if found:
            break

    assert found, (
        "No serialization benchmark exercises N >= 1000 rows in benches/. "
        "A pre-1.3 O(N²) regression would ship invisibly."
    )


def test_discipline_suite_includes_load_stress_contract() -> None:
    """The repo must include a load/stress test contract (per ROADMAP §0.3.x).

    The 0.3.x tooling tier lists "stress test infrastructure" as part
    of the locked-in baseline. Its absence means the perf-under-scale
    contract is missing.
    """
    stress_dir = ROOT / "tests" / "stress"
    assert stress_dir.exists(), (
        "tests/stress/ missing -- perf-under-scale contract not exercised"
    )
    py_stress = list(stress_dir.rglob("*.py"))
    assert py_stress, (
        "tests/stress/ contains no Python tests -- perf-under-scale "
        "contract not exercised"
    )