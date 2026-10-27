"""Parsing of dependency scanner output into actionable findings.

Both parsers fail closed. A scanner that crashes, returns a truncated
report, or emits a shape the parser does not recognise must never be read
as "no vulnerabilities found", because that silently turns an unscanned
dependency tree into a passing gate.
"""

from __future__ import annotations

import json

from errors import DependencyDeltaError


def _invalid_dotnet_json(detail: str) -> DependencyDeltaError:
    return DependencyDeltaError(f"invalid dotnet vulnerability JSON: {detail}")


def dotnet_vulnerability_findings(stdout: str | bytes) -> list[str]:
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
        if not isinstance(project, dict):
            raise _invalid_dotnet_json("each project must be an object")
        project_path = project.get("path", "<unknown-project>")
        if not isinstance(project_path, str):
            raise _invalid_dotnet_json("project path must be a string")
        if "frameworks" not in project:
            # A real clean report lists the project and omits the frameworks
            # key entirely, because the command found no vulnerable package
            # to describe. There is nothing to scan and nothing to report.
            continue
        frameworks = project["frameworks"]
        if not isinstance(frameworks, list):
            raise _invalid_dotnet_json("each project must contain a frameworks array")
        if not frameworks:
            # The project was resolved but no target framework was reported.
            # That means the scan did not reach the packages, so it must not
            # be read as a clean result.
            raise _invalid_dotnet_json(f"project {project_path} reports no frameworks")
        for framework in frameworks:
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


def _invalid_node_json(detail: str) -> DependencyDeltaError:
    return DependencyDeltaError(f"invalid npm audit JSON: {detail}")


def node_advisory_ids(stdout: str | bytes) -> set[str]:
    """Return the set of advisory identities reported by `npm audit --json`.

    The identity is the GHSA URL when present, else "<package>@<source>".
    Comparing advisory IDs (not package names) is what lets a PR that FIXES
    one advisory while a package stays vulnerable still count as a fix.
    """
    try:
        if isinstance(stdout, bytes):
            stdout = stdout.decode("utf-8")
        payload = json.loads(stdout)
    except (UnicodeDecodeError, json.JSONDecodeError, TypeError) as error:
        raise _invalid_node_json(str(error)) from error

    if not isinstance(payload, dict):
        raise _invalid_node_json("root object must be an object")
    # npm reports transport/registry failures as a top-level "error" object
    # and still exits 0 with an empty vulnerability list. That must never be
    # read as "no advisories" - it means the scan did not happen.
    if "error" in payload:
        error = payload["error"]
        if isinstance(error, dict):
            detail = error.get("code") or error.get("summary") or error
        else:
            detail = error
        raise _invalid_node_json(f"npm audit reported an error: {detail}")
    # A real audit report always carries both "vulnerabilities" and
    # "metadata.vulnerabilities". Require both, so a truncated or
    # unexpected payload cannot silently look like a clean bill of health.
    if "vulnerabilities" not in payload:
        raise _invalid_node_json("response has no vulnerabilities key")
    if "metadata" not in payload:
        raise _invalid_node_json("response has no metadata key")
    metadata = payload["metadata"]
    if not isinstance(metadata, dict) or "vulnerabilities" not in metadata:
        raise _invalid_node_json("response has no metadata.vulnerabilities")
    summary = metadata["vulnerabilities"]
    if not isinstance(summary, dict):
        raise _invalid_node_json("metadata.vulnerabilities must be an object")
    vulnerabilities = payload["vulnerabilities"]
    if not isinstance(vulnerabilities, dict):
        raise _invalid_node_json("vulnerabilities must be an object")

    identities: set[str] = set()
    for package, info in vulnerabilities.items():
        if not isinstance(info, dict):
            raise _invalid_node_json(f"each vulnerability entry must be an object: {package}")
        if "via" not in info:
            raise _invalid_node_json(f"vulnerability entry has no via key: {package}")
        via = info["via"]
        if not isinstance(via, list):
            raise _invalid_node_json(f"via must be an array: {package}")
        if not via:
            # A package listed as vulnerable always names the advisory that
            # affects it. An empty list describes no advisory at all, so the
            # entry cannot be judged and must not be treated as clean.
            raise _invalid_node_json(f"vulnerability entry for {package} has an empty via list")
        for entry in via:
            if isinstance(entry, dict):
                url = entry.get("url")
                source = entry.get("source")
                if isinstance(url, str) and url:
                    # Identity is (package, advisory). Keying on the GHSA id
                    # alone would let a NEW package inherit an advisory that
                    # the base already had, and the new exposure would be
                    # masked by the existing one.
                    identities.add(f"{package}@{url}")
                elif source is not None:
                    identities.add(f"{package}@{source}")
                else:
                    raise _invalid_node_json(f"advisory for {package} has neither url nor source")
            elif isinstance(entry, str):
                identities.add(f"{package}@{entry}")
            else:
                raise _invalid_node_json(f"advisory entry for {package} is not an object")

    # The report carries its own count of the vulnerable packages it found.
    # That count is always present in a real report, and it must agree with
    # the entries actually listed. A report that omits the count, or whose
    # count disagrees with its contents, is incomplete or inconsistent and
    # must not be read as a clean bill of health: a report that claims a
    # vulnerability it does not describe would otherwise be accepted as if
    # it had found nothing.
    if "total" not in summary:
        raise _invalid_node_json("response has no metadata.vulnerabilities.total")
    reported_total = summary["total"]
    if not isinstance(reported_total, int) or isinstance(reported_total, bool):
        raise _invalid_node_json("metadata.vulnerabilities.total must be an integer")
    if reported_total != len(vulnerabilities):
        raise _invalid_node_json(
            f"metadata.vulnerabilities.total is {reported_total} but the report "
            f"describes {len(vulnerabilities)} vulnerable packages"
        )
    return identities
