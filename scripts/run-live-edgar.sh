#!/usr/bin/env bash
# Runs the live SEC EDGAR check (tests/MarketData.Tests/Live) with the identity from the environment and leaves a log in the output directory.
# The service has no key, but the SEC requires every caller to declare who it is: set Edgar__UserAgent to a name and a contact address you read
# (for example "SakerWatch you@example.com"). The check makes about four requests, spaced to stay far below the SEC's ten a second.
# Usage: bash scripts/run-live-edgar.sh [output-dir]
set -u
out="${1:-/tmp/saker-live-edgar}"
: "${Edgar__UserAgent:?Edgar__UserAgent is not set in this shell (a name and a contact address)}"
export SAKER_LIVE_EDGAR=1
mkdir -p "$out"
dotnet test tests/MarketData.Tests --filter "FullyQualifiedName~EdgarLiveTests" --logger "console;verbosity=normal" 2>&1 | tee "$out/dotnet-test.log" | tail -20
echo "log: $out/dotnet-test.log"
