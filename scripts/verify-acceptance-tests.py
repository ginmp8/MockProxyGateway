"""Reconcile named acceptance evidence with actual passing VSTest TRX results."""

from collections import Counter
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


REFERENCE = re.compile(r"\b[A-Za-z_]\w*Tests\.[A-Za-z_]\w*\b")


def executed_methods(paths):
    methods = Counter()
    for path in paths:
        root = ET.parse(path).getroot()
        definitions = {
            test.attrib["id"]: test.find("{*}TestMethod")
            for test in root.findall(".//{*}UnitTest")
        }
        results = root.findall(".//{*}UnitTestResult")
        if not results:
            raise ValueError(f"No test results in {path.name}")
        for result in results:
            if result.get("outcome") != "Passed":
                raise ValueError(f"Non-passing test result in {path.name}")
            method = definitions.get(result.get("testId"))
            if method is None:
                raise ValueError(f"Missing test definition in {path.name}")
            class_name = method.get("className", "").split(",", 1)[0].rsplit(".", 1)[-1]
            method_name = method.get("name", "").split("(", 1)[0].rsplit(".", 1)[-1]
            if not class_name or not method_name:
                raise ValueError(f"Missing test method identity in {path.name}")
            methods[f"{class_name}.{method_name}"] += 1
    if not methods:
        raise ValueError("No test results found")
    return methods


def require_methods(references, methods):
    missing = sorted(references - methods.keys())
    if missing:
        raise ValueError("Missing passing acceptance methods: " + ", ".join(missing))


def main():
    root = Path(__file__).resolve().parent.parent
    document = (root / "docs/acceptance-matrix.md").read_text(encoding="utf-8")
    rows = re.findall(r"^\| (AC-\d{2}) \| (.*) \|$", document, re.MULTILINE)
    expected = {f"AC-{number:02d}" for number in range(1, 19)}
    if len(rows) != len(expected) or {criterion for criterion, _ in rows} != expected:
        raise ValueError("Acceptance trace must contain exactly AC-01 through AC-18")
    scenario_evidence = document.split("## Executable scenario coverage\n", 1)[1].split("## AC evidence map\n", 1)[0]
    methods = executed_methods(sorted((root / "artifacts/test-results").glob("*.trx")))
    references = set(REFERENCE.findall(document))
    if not references:
        raise ValueError("Acceptance trace has no named test evidence")
    require_methods(references, methods)
    for criterion, evidence in rows:
        names = set(REFERENCE.findall(evidence))
        if criterion == "AC-04":
            names.update(REFERENCE.findall(scenario_evidence))
        if names:
            count = sum(methods[name] for name in names)
            print(f"{criterion}: {len(names)} named methods, {count} passing cases")
        else:
            print(f"{criterion}: separate workflow/documentation evidence (not a TRX claim)")
    print(f"Verified {len(references)} named acceptance methods against passing TRX results.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, ET.ParseError, IndexError) as error:
        sys.exit(str(error))
