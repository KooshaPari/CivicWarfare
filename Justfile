# CivicSurvival Public -- Justfile
#
# Equivalent to the Makefile. Provided for environments where make is
# unavailable (Windows, minimal containers). Recipes are intentionally
# identical (where possible) so contributors pick whichever runner exists.
# A third runner (Taskfile.yml / go-task) is also shipped -- see BUILDING.md.

set windows-shell := ["powershell.exe", "-NoLogo", "-Command"]

PY := if `command -v python3` != "" { "python3" } else { "python" }
PYTEST := "pytest"
RUFF := if `command -v ruff` != "" { "ruff" } else { "python -m ruff" }
DOTNET := if `command -v dotnet` != "" { "dotnet" } else { "dotnet" }

# Default recipe -- equivalent to `make help`
default:
    @just --list

# Run pytest (alias for `make test`)
test:
    {{PYTEST}} -q --ignore=tests/public-audit

# Run pytest with verbose output
test-verbose:
    {{PYTEST}} -v --ignore=tests/public-audit

# Run ruff lint
lint:
    {{RUFF}} check scripts tests

# Run ruff format (auto-fix)
format:
    {{RUFF}} format scripts tests

# Run ruff format --check
format-check:
    {{RUFF}} format --check scripts tests

# Run the 88-pillar scorecard locally
scorecard:
    {{PY}} scripts/scorecard_ci.py .

# Run scorecard with --fail-on-drop
scorecard-check:
    {{PY}} scripts/scorecard_ci.py . --fail-on-drop --baseline-file .github/scorecard-baseline.json

# Print release version
release-current:
    {{PY}} scripts/release.py current

# Verify release surfaces
release-verify:
    {{PY}} scripts/release.py verify

# Build the mod DLL (Release, requires CS2 modding toolchain)
build:
    {{DOTNET}} build CivicSurvival/CivicSurvival.csproj -c Release -p:GameManagedPath="C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed" -p:EnableCivicBurst=false

# Fast dev build (Debug, skips NuGet restore)
build-dev:
    {{DOTNET}} build CivicSurvival/CivicSurvival.csproj -c Debug -p:GameManagedPath="C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed" -p:EnableCivicBurst=false --no-restore

# Build the .NET 9 native installer (win-x64)
build-installer:
    cd installer && rm -rf bin obj dist && {{DOTNET}} publish -c Release -r win-x64 --self-contained false -o dist

# Build mod + installer + install into CS2 (full dev workflow)
install: build build-installer
    installer/dist/civicsurvival-installer.exe install CivicSurvival/bin/Release/netstandard2.0/CivicSurvival.dll

# Build + update existing install (preserves config)
update: build build-installer
    installer/dist/civicsurvival-installer.exe update CivicSurvival/bin/Release/netstandard2.0/CivicSurvival.dll

# Full uninstall of the mod from CS2
remove:
    installer/dist/civicsurvival-installer.exe remove

# Discover CS2 install + verify BepInEx is present
status-cs2:
    installer/dist/civicsurvival-installer.exe check

# Show installed mod status
status-mod:
    installer/dist/civicsurvival-installer.exe status

# Launch CS2 via the Steam URL handler
launch:
    cmd /c start steam://run/949230

# Full local CI parity
ci: lint format-check test scorecard
    @echo "ci: OK"
