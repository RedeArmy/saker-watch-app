#!/usr/bin/env python3
"""Prints the k6 summaries of scripts/run-k6.sh as one markdown table per run.  summarize.py LABEL=FILE [LABEL=FILE ...]"""
import json
import os
import sys

ENDPOINTS = ["watchlist_by_ticker", "watchlist_by_performance", "sparklines", "portfolio_metrics", "calculator"]


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
        label, _, path = argument.partition("=")
        with open(path, encoding="utf-8") as file:
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
