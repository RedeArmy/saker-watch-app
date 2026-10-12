#!/usr/bin/env bash
# ADR-0015: the only accepted TODO form is TODO(owner: <name>[, <ref>]): <text>. HACK/FIXME need a ref.
cd "$(dirname "$0")/.." || exit 2
bad="$(git ls-files 'src/*' 'tests/*' 'web/*' 'deploy/*' 'scripts/*' ':!scripts/check-todos.sh' ':!*.md' \
  | xargs grep -nIE '\b(TODO|HACK|FIXME)\b' 2>/dev/null \
  | grep -vE 'TODO\((owner|phase)[-: ]' || true)"
if [[ -n "$bad" ]]; then
  echo "Malformed TODO/HACK/FIXME (use TODO(owner: name, ref): text):" >&2
  echo "$bad" >&2
  exit 1
fi
echo "todo discipline OK"
