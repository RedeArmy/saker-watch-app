#!/usr/bin/env bash
# Fail if an indexed file is missing or (for docs/) lacks a header; warn on stubs past target phase.
set -u
cd "$(dirname "$0")/.."
idx=docs/DOCS-INDEX.md
cur="$(tr -dc '0-9' < docs/PHASE 2>/dev/null)"; cur="${cur:-0}"
err=0
while IFS='|' read -r _ path purpose owner status phase _; do
  path="$(echo "$path" | xargs)"; status="$(echo "$status" | xargs)"; phase="$(echo "$phase" | xargs)"
  case "$path" in
    ''|Path|---*) continue ;;
    *) ;;
  esac
  if [[ ! -e "$path" ]]; then echo "ERROR: missing file: $path" >&2; err=1; continue; fi
  case "$path" in
    docs/*.md|docs/*/*.md)
      case "$path" in
        docs/templates/*|docs/DOCS-INDEX.md) ;;
        *)
          grep -qiE '(Purpose|Status):' "$path" || { echo "ERROR: no header (Purpose/Status): $path" >&2; err=1; } ;;
      esac ;;
    *) ;;
  esac
  if [[ "$status" == stub && "$phase" =~ ^[0-9]+$ ]] && (( phase < cur )); then
    echo "WARN: stub past target phase ($phase < $cur): $path" >&2
  fi
done < "$idx"
# Reverse check: scaffolding files must be indexed.
for f in $(git ls-files 'docs/**/*.md' 'docs/*.md' 2>/dev/null; true); do
  grep -qF "| $f |" "$idx" || { echo "ERROR: not in DOCS-INDEX: $f" >&2; err=1; }
done
[[ "$err" == 0 ]] && echo "docs index OK"
exit "$err"
