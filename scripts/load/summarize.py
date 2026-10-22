#!/usr/bin/env python3
"""Prints the k6 summaries of scripts/run-k6.sh as one markdown table per run.

  summarize.py LABEL=FILE [LABEL=FILE ...]

Each FILE is a .json file inside the current directory (run it from the directory that holds the summaries); a path that leaves it is refused.
K6_PHASE_SECONDS (default 30) is the length of one scenario, to turn a count of requests into requests per second.
"""
import json
import os
import sys
from pathlib import Path

ENDPOINTS = ["watchlist_by_ticker", "watchlist_by_performance", "sparklines", "portfolio_metrics", "calculator"]


def inside_here(name: str) -> Path:
    """The file of that name below the current directory; anything that resolves outside it is refused."""
    root = Path.cwd().resolve()
    target = (root / name).resolve()
    if not target.is_relative_to(root) or target.suffix != ".json":
        raise SystemExit(f"{name}: only .json files inside the current directory are read")
    return target


def row(metrics: dict, endpoint: str) -> str:
    duration = metrics.get(f"http_req_duration{{endpoint:{endpoint}}}", {})
    failed = metrics.get(f"http_req_failed{{endpoint:{endpoint}}}", {})
    count = metrics.get(f"http_reqs{{endpoint:{endpoint}}}", {})
    values = [duration.get(key) for key in ("avg", "med", "p(95)", "p(99)", "max")]
    cells = ["-" if value is None else f"{value:.1f}" for value in values]
    # The rate k6 exports is over the whole run, and each scenario runs for a fifth of it; the count over the phase is what is wanted.
    seconds = int(os.environ.get("K6_PHASE_SECONDS", "30"))
    rate = count["count"] / seconds if "count" in count else None
    errors = failed.get("value", failed.get("rate"))
    return f"| {endpoint} | " + " | ".join(cells) + f" | {'-' if rate is None else f'{rate:.1f}'} | {'-' if errors is None else f'{errors * 100:.2f} %'} |"


def main(argv: list[str]) -> int:
    for argument in argv[1:]:
        label, _, name = argument.partition("=")
        with inside_here(name).open(encoding="utf-8") as file:
            metrics = json.load(file)["metrics"]
        print(f"### {label}\n")
        print("| endpoint | avg ms | median ms | p95 ms | p99 ms | max ms | requests/s | errors |")
        print("|---|---|---|---|---|---|---|---|")
        for endpoint in ENDPOINTS:
            print(row(metrics, endpoint))
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
