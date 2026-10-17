#!/usr/bin/env bash
# Runs the live Twelve Data checks (tests/MarketData.Tests/Live) with the key from the environment and leaves a report in the output directory.
# They call the real API, spend about forty of the 800 daily credits and take several minutes (they keep to 8 calls per minute).
# Usage: bash scripts/run-live-twelvedata.sh [output-dir]      (needs TwelveData__ApiKey in the environment)
set -u
out="${1:-/tmp/saker-live-twelvedata}"
: "${TwelveData__ApiKey:?TwelveData__ApiKey is not set in this shell}"
export SAKER_LIVE_TWELVEDATA=1
export SAKER_LIVE_REPORT_DIR="$out"
mkdir -p "$out"
dotnet test tests/MarketData.Tests --filter "FullyQualifiedName~TwelveDataLiveTests" --logger "console;verbosity=normal" 2>&1 | tee "$out/dotnet-test.log" | tail -20
echo "report: $out/report.md"
