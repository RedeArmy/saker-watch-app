#!/usr/bin/env bash
# One-off check of the Twelve Data free plan for ADR-0027. Reads the key from the environment (TwelveData__ApiKey), sends it in the
# Authorization header (never in a URL) and writes the raw responses to the output directory. Uses about 13 of the 800 daily credits.
# Usage: bash scripts/verify-twelvedata.sh <output-dir>
set -u
out="${1:?usage: verify-twelvedata.sh <output-dir>}"
mkdir -p "$out"
: "${TwelveData__ApiKey:?TwelveData__ApiKey is not set in this shell}"
base="https://api.twelvedata.com"

get() { # name, path-with-query
  local name="$1" path="$2"
  curl -sS --max-time 30 -D "$out/$name.headers" -o "$out/$name.json" \
    -H "Authorization: apikey ${TwelveData__ApiKey}" "$base$path" \
    -w "%{http_code}\n" > "$out/$name.status" 2>"$out/$name.err" || true
  # headers can carry cookies; keep only the status line and the rate-limit related ones
  grep -iE '^(HTTP|x-ratelimit|retry-after|api-credits|content-type)' "$out/$name.headers" > "$out/$name.h" 2>/dev/null || true
  rm -f "$out/$name.headers"
  printf '%-22s status=%s bytes=%s\n' "$name" "$(cat "$out/$name.status")" "$(wc -c < "$out/$name.json")"
  sleep 8   # stay under 8 credits per minute
  return 0
}

get exchanges_all          "/exchanges"
get ts_aapl_adj_all        "/time_series?symbol=AAPL&interval=1day&outputsize=5000&adjust=all&order=asc"
get ts_aapl_adj_none       "/time_series?symbol=AAPL&interval=1day&outputsize=30&adjust=none&order=asc"
get ts_aapl_adj_splits     "/time_series?symbol=AAPL&interval=1day&outputsize=30&adjust=splits&order=asc"
get ts_nvda_split_2024     "/time_series?symbol=NVDA&interval=1day&start_date=2024-06-01&end_date=2024-06-20&adjust=all&order=asc"
get ts_spy_etf             "/time_series?symbol=SPY&interval=1day&outputsize=10&adjust=all&order=asc"
get ts_shop_tsx            "/time_series?symbol=SHOP&exchange=TSX&interval=1day&outputsize=5"
get ts_toyota_tokyo        "/time_series?symbol=7203&exchange=XJPX&interval=1day&outputsize=5"
get ts_sap_xetra           "/time_series?symbol=SAP&exchange=XETR&interval=1day&outputsize=5"
get ts_invalid             "/time_series?symbol=NOTAREALTICKER123&interval=1day&outputsize=5"
get search_shop            "/symbol_search?symbol=SHOP&outputsize=5"
get stocks_us              "/stocks?exchange=NYSE&outputsize=5"
get usage                  "/api_usage"
echo "done; results in $out"
