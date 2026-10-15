#!/usr/bin/env python3
"""Coverage gate: line coverage of the Domain and Application assemblies must reach a minimum (default 85 %).

Reads every Cobertura report under a directory (one per test project), merges them by class and line (a line counts as
covered when any test project hit it) and checks the assemblies that hold business logic: `*.Domain`, `*.Application` and
the Kernel/Application building blocks. Hosts, Infrastructure and generated migrations are reported but not gated.

Usage: python3 -I scripts/check-coverage.py <results-directory> [minimum-percent]
"""
import collections
import glob
import re
import sys
import xml.etree.ElementTree as ET

GATED = re.compile(r"^SakerWatch\.(.+\.(Domain|Application)|BuildingBlocks\.Kernel)$")


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    minimum = float(sys.argv[2]) if len(sys.argv) > 2 else 85.0

    lines: dict[str, dict[tuple[str, int], int]] = collections.defaultdict(dict)
    reports = glob.glob(sys.argv[1] + "/**/coverage.cobertura.xml", recursive=True)
    if not reports:
        print(f"No Cobertura reports under {sys.argv[1]}")
        return 2

    for report in reports:
        for package in ET.parse(report).getroot().iter("package"):
            target = lines[package.get("name")]
            for cls in package.iter("class"):
                for line in cls.iter("line"):
                    key = (cls.get("name"), int(line.get("number")))
                    target[key] = max(target.get(key, 0), int(line.get("hits")))

    failed = False
    gated = 0
    print(f"{'assembly':58} {'lines':>7} {'covered':>8}  gate")
    for name, data in sorted(lines.items()):
        if not data:
            continue  # nothing executable (for example only records or interfaces)
        covered = sum(1 for hits in data.values() if hits)
        percent = 100.0 * covered / len(data)
        is_gated = bool(GATED.match(name))
        gated += is_gated
        verdict = ""
        if is_gated:
            verdict = "ok" if percent >= minimum else f"FAIL (< {minimum:g}%)"
            failed |= percent < minimum
        print(f"{name:58} {len(data):7} {percent:7.1f}%  {verdict}")

    if gated == 0:
        print("No gated assembly found in the reports; the gate would be vacuous.")
        return 2
    print("coverage gate:", "FAILED" if failed else f"passed ({gated} assemblies >= {minimum:g}%)")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
