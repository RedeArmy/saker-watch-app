#!/usr/bin/env python3
"""Coverage gate: line coverage of the Domain and Application assemblies must reach a minimum (default 85 %).

Reads every Cobertura report under a directory (one per test project), merges them by class and line (a line counts as
covered when any test project hit it) and checks the assemblies that hold business logic: `*.Domain`, `*.Application` and
the Kernel/Application building blocks. Hosts, Infrastructure and generated migrations are reported but not gated, and generated source (under obj/)
is left out of every count.

A gated assembly of src/ that no report mentions fails the gate too (a module whose tests stopped emitting coverage would otherwise be skipped
without a word), unless scripts/coverage-exempt.txt lists it as holding no executable code yet.

Usage: python3 -I scripts/check-coverage.py <results-directory> [minimum-percent]
"""
import collections
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

GATED = re.compile(r"^SakerWatch\.(.+\.(Domain|Application)|BuildingBlocks\.Kernel)$")
REPORT_NAME = "coverage.cobertura.xml"


def find_reports(directory: str) -> list[Path]:
    """Only files named exactly like a Cobertura report, found below an existing directory (symlinks resolved)."""
    root = Path(directory).resolve(strict=True)
    if not root.is_dir():
        raise NotADirectoryError(directory)
    return [path for path in root.rglob(REPORT_NAME) if path.is_file()]


def is_generated(filename: str | None) -> bool:
    """Source generators (regex, logging, EF) write into obj/; their code is not logic the tests are meant to cover."""
    normalized = (filename or "").replace("\\", "/")
    return "/obj/" in normalized or normalized.endswith(".g.cs")


def merge(reports: list[Path]) -> dict[str, dict[tuple[str, int], int]]:
    """Hits per (class, line) for each package; the highest hit count across reports wins."""
    lines: dict[str, dict[tuple[str, int], int]] = collections.defaultdict(dict)
    for report in reports:
        for package in ET.parse(report).getroot().iter("package"):
            target = lines[package.get("name")]
            for cls in package.iter("class"):
                if is_generated(cls.get("filename")):
                    continue
                for line in cls.iter("line"):
                    key = (cls.get("name"), int(line.get("number")))
                    target[key] = max(target.get(key, 0), int(line.get("hits")))
    return lines


def evaluate(lines: dict[str, dict[tuple[str, int], int]], minimum: float) -> tuple[int, bool]:
    """Prints one row per assembly; returns how many are gated and whether any gated one is below the minimum."""
    failed = False
    gated = 0
    print(f"{'assembly':58} {'lines':>7} {'covered':>8}  gate")
    for name, data in sorted(lines.items()):
        if not data:
            continue  # nothing executable (for example only records or interfaces)
        percent = 100.0 * sum(1 for hits in data.values() if hits) / len(data)
        verdict = ""
        if GATED.match(name):
            gated += 1
            failed |= percent < minimum
            verdict = "ok" if percent >= minimum else f"FAIL (< {minimum:g}%)"
        print(f"{name:58} {len(data):7} {percent:7.1f}%  {verdict}")
    return gated, failed


def missing_assemblies(lines: dict[str, dict[tuple[str, int], int]], repository: Path) -> list[str]:
    """Gated assemblies of src/ that the reports do not mention and that are not listed as exempt."""
    exempt_file = repository / "scripts" / "coverage-exempt.txt"
    exempt = {
        line.split("#", 1)[0].strip()
        for line in exempt_file.read_text(encoding="utf-8").splitlines()
    } if exempt_file.is_file() else set()
    expected = {
        project.stem for project in (repository / "src").rglob("*.csproj")
        if GATED.match(project.stem) and "/obj/" not in project.as_posix()
    }
    return sorted(name for name in expected - exempt if not lines.get(name))


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    try:
        minimum = float(sys.argv[2]) if len(sys.argv) > 2 else 85.0
        reports = find_reports(sys.argv[1])
    except (ValueError, OSError) as error:
        print(f"Invalid arguments: {error}")
        return 2
    if not 0 <= minimum <= 100:
        print("The minimum must be between 0 and 100.")
        return 2
    if not reports:
        print(f"No Cobertura reports under {sys.argv[1]}")
        return 2

    merged = merge(reports)
    gated, failed = evaluate(merged, minimum)
    missing = missing_assemblies(merged, Path(__file__).resolve().parent.parent)
    for name in missing:
        print(f"{name}: no coverage in any report; if it has no executable code yet, list it in scripts/coverage-exempt.txt")
    failed = failed or bool(missing)
    if gated == 0:
        print("No gated assembly found in the reports; the gate would be vacuous.")
        return 2
    print("coverage gate:", "FAILED" if failed else f"passed ({gated} assemblies >= {minimum:g}%)")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
