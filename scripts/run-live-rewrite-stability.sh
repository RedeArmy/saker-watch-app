#!/usr/bin/env bash
# Checks live whether Twelve Data's adjusted closes move between two calls with no corporate action in between (RewriteTolerance, runbook provider-quota).
# Spends about twenty-two of the 800 daily credits and waits SAKER_LIVE_REWRITE_WAIT_MINUTES (default 10) between the two passes. Running it again on another day also
# compares with the snapshot the earlier run left in the output directory.
# Usage: bash scripts/run-live-rewrite-stability.sh [output-dir]      (needs TwelveData__ApiKey in the environment)
set -u
out="${1:-/tmp/saker-live-twelvedata}"
: "${TwelveData__ApiKey:?TwelveData__ApiKey is not set in this shell}"
export SAKER_LIVE_TWELVEDATA=1
export SAKER_LIVE_REPORT_DIR="$out"
mkdir -p "$out"
dotnet test tests/MarketData.Tests --filter "FullyQualifiedName~RewriteStabilityLiveTests" --logger "console;verbosity=normal" 2>&1 | tee "$out/rewrite-stability-test.log" | tail -20
echo "report: $out/rewrite-stability-report.md"
