#!/usr/bin/env python3
"""Checks that the status checks the ruleset of `main` requires are jobs of the CI workflow (runbook sonarcloud-setup, section D).

A required check that no job reports blocks every merge for ever, and the ruleset is a file that is imported by hand, so a renamed or removed job is
easy to miss. Exit status 1 when the ruleset requires a check that has no job; a job that is not required is only reported.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RULESET = ROOT / "docs" / "runbooks" / "github-ruleset-main.json"
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"


def required_checks() -> list[str]:
    ruleset = json.loads(RULESET.read_text(encoding="utf-8"))
    return [
        check["context"]
        for rule in ruleset["rules"]
        if rule["type"] == "required_status_checks"
        for check in rule["parameters"]["required_status_checks"]
    ]


def workflow_jobs() -> list[str]:
    text = WORKFLOW.read_text(encoding="utf-8")
    jobs = text.split("\njobs:\n", 1)[1]
    # A job is a key at two spaces of indentation; its name: line, when it has one, is what the check is called.
    names = []
    for match in re.finditer(r"^  ([a-z0-9_-]+):\n((?:    .*\n|\n)*)", jobs, re.M):
        named = re.search(r"^    name: (.+)$", match.group(2), re.M)
        names.append(named.group(1).strip() if named else match.group(1))
    return names


def main() -> int:
    required, jobs = required_checks(), workflow_jobs()
    missing = [check for check in required if check not in jobs]
    for check in missing:
        print(f"the ruleset requires '{check}' and no job of ci.yml reports it", file=sys.stderr)
    not_required = [job for job in jobs if job not in required]
    if not_required:
        print(f"jobs the ruleset does not require: {', '.join(not_required)}")
    if not missing:
        print(f"ruleset OK: {len(required)} required checks, all reported by a job")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
