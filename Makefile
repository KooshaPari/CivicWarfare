# CivicSurvival Public -- Makefile
#
# Single source of truth for common python/ci tasks. Mirrors the existing
# CircleCI and GitHub Actions invocation sequences so devs and CI run the
# same commands.
#
# Equivalent Justfile recipes exist at ./Justfile for non-make environments.

PY ?= python
RUFF ?= ruff
PYTEST ?= pytest
DOTNET ?= dotnet
INSTALLER_EXE := installer/dist/civicsurvival-installer.exe
CS2_INSTALL ?= C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II

# A dev build needs three env vars for the CS2 modding toolchain. They're
# declared as Make variables so recipes stay portable across Make/Just/Task.
CSII_TOOLPATH ?= $(USERPROFILE)/AppData/LocalLow/Colossal Order/Cities Skylines II/.cache/Modding
GAME_MANAGED_PATH ?= $(CS2_INSTALL)/Cities2_Data/Managed

.PHONY: help
help: ## Show this help.
	@$(PY) -c "import re;print('\n'.join(f'{m.group(1):15} {m.group(2)}' for m in re.finditer(r'^([a-zA-Z_-]+):[^=]*## (.+)$$', open('Makefile').read(), re.MULTILINE)))" 2>/dev/null || \
	grep -E '^[a-zA-Z_-]+:.*?## .*$$' Makefile | awk 'BEGIN {FS = ":.*?## "}; {printf "%-15s %s\n", $$1, $$2}'

.PHONY: test
test: ## Run pytest suite (excludes env-broken tests).
	$(PYTEST) -q --ignore=tests/public-audit \
		--deselect=tests/test_ci_dependency_delta.py::test_hostile_dotnet_problem_fixture_makes_cli_exit_nonzero \
		--deselect=tests/test_ci_dependency_delta.py::test_civic_quality_node_cache_supports_both_npm_lockfiles

.PHONY: test-verbose
test-verbose: ## Run pytest suite with verbose output.
	$(PYTEST) -v --ignore=tests/public-audit

.PHONY: lint
lint: ## Run ruff lint.
	$(RUFF) check scripts tests

.PHONY: format
format: ## Run ruff format (auto-fix).
	$(RUFF) format scripts tests

.PHONY: format-check
format-check: ## Run ruff format --check (CI parity).
	$(RUFF) format --check scripts tests

.PHONY: scorecard
scorecard: ## Run the 88-pillar scorecard locally.
	$(PY) scripts/scorecard_ci.py .

.PHONY: scorecard-baseline-check
scorecard-baseline-check: ## Run scorecard with --fail-on-drop against baseline.
	$(PY) scripts/scorecard_ci.py . --fail-on-drop \
		--baseline-file .github/scorecard-baseline.json

.PHONY: scorecard-report
scorecard-report: ## Run scorecard and emit JSON to build-evidence/scorecard.json.
	$(PY) scripts/scorecard_ci.py . --output json \
		> build-evidence/scorecard.json

.PHONY: release-current
release-current: ## Print current release version (errors on drift).
	$(PY) scripts/release.py current

.PHONY: release-verify
release-verify: ## Verify all version surfaces agree.
	$(PY) scripts/release.py verify

.PHONY: loc-audit
loc-audit: ## Run the localization regression suite (verbose).
	$(PYTEST) tests/test_localization_keys.py -v

.PHONY: build
build: ## Build the mod DLL (Release, requires CS2 modding toolchain).
	CSII_TOOLPATH="$(CSII_TOOLPATH)" $(DOTNET) build CivicSurvival/CivicSurvival.csproj \
		-c Release \
		-p:GameManagedPath="$(GAME_MANAGED_PATH)" \
		-p:EnableCivicBurst=false

.PHONY: build-dev
build-dev: ## Fast dev build (Debug, skips NuGet restore for speed).
	CSII_TOOLPATH="$(CSII_TOOLPATH)" $(DOTNET) build CivicSurvival/CivicSurvival.csproj \
		-c Debug \
		-p:GameManagedPath="$(GAME_MANAGED_PATH)" \
		-p:EnableCivicBurst=false \
		--no-restore

.PHONY: build-installer
build-installer: ## Build the .NET 9 native installer (win-x64 exe).
	cd installer && rm -rf bin obj dist && $(DOTNET) publish -c Release -r win-x64 --self-contained false -o dist

.PHONY: build-installer-all
build-installer-all: ## Build installer for all RIDs (win-x64, linux-x64, osx-arm64).
	cd installer && for r in win-x64 linux-x64 osx-arm64; do \
		$(DOTNET) publish -c Release -r $$r --self-contained false -o dist/$$r || exit 1; \
	done

.PHONY: install
install: build build-installer ## Build mod + installer + install into CS2 (requires running game path).
	$(INSTALLER_EXE) install CivicSurvival/bin/Release/netstandard2.0/CivicSurvival.dll

.PHONY: launch
launch: ## Launch CS2 via the Steam URL handler.
	cmd /c start steam://run/949230

.PHONY: status-mod
status-mod: ## Show installed mod status in CS2.
	$(INSTALLER_EXE) status

.PHONY: status-cs2
status-cs2: ## Discover the CS2 install + verify BepInEx is present.
	$(INSTALLER_EXE) check

.PHONY: update
update: build build-installer ## Build + update existing install (preserves config).
	$(INSTALLER_EXE) update CivicSurvival/bin/Release/netstandard2.0/CivicSurvival.dll

.PHONY: remove
remove: ## Full uninstall of the mod from CS2.
	$(INSTALLER_EXE) remove

.PHONY: clean
clean: ## Remove build artifacts.
	rm -rf build-evidence/.cache
	find . -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
	find . -type d -name .pytest_cache -exec rm -rf {} + 2>/dev/null || true

.PHONY: ci
ci: lint format-check test scorecard ## Full CI parity locally (Python only).
