import importlib.util
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))

# The error type is imported normally rather than loaded by path, so that it
# is the SAME class object the scanner raises. Loading errors.py under a
# second name would create a second, distinct class and the assertion
# below would never match.
from errors import DependencyDeltaError

spec = importlib.util.spec_from_file_location(
    "audit_probe", Path(__file__).parents[1] / "scripts/audit_reports.py"
)
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


@pytest.mark.parametrize(
    "bad",
    [
        "{}",
        '{"vulnerabilities":{}}',
        '{"metadata":{}}',
        '{"error":{"code":"EAUDITNOLOCK"}}',
        '{"vulnerabilities":{"p":{}}}',
        '{"vulnerabilities":{"p":{"via":[42]}}}',
        "null",
        "[]",
        "<html>error</html>",
        '{"vulnerabilities":',
        '{"metadata":{"vulnerabilities":{"total":1}},"vulnerabilities":{}}',
        '{"metadata":{"vulnerabilities":{"total":1}},"vulnerabilities":{"p":{"via":[]}}}',
    ],
)
def test_malformed_never_clean(bad):
    with pytest.raises(DependencyDeltaError):
        audit.node_advisory_ids(bad)


def test_missing_total_must_not_claim_clean():
    with pytest.raises(DependencyDeltaError):
        audit.node_advisory_ids('{"metadata":{"vulnerabilities":{}},"vulnerabilities":{}}')
