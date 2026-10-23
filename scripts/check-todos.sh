#!/usr/bin/env bash
# ADR-0015: the only accepted TODO form is TODO(owner: <name>[, <ref>]): <text>; HACK and FIXME need the ref too.
# Every marker is checked, so a line with one valid TODO and a second bare marker fails.
# With file arguments it checks those files only (the pre-commit hooks use that); without, every tracked source file.
cd "$(dirname "$0")/.." || exit 2
files() {
  if [[ $# -gt 0 ]]; then printf '%s\0' "$@"; else git ls-files -z 'src/*' 'tests/*' 'web/*' 'deploy/*' 'scripts/*' ':!scripts/check-todos.sh' ':!*.md'; fi
  return 0
}
files "$@" \
  | python3 -I -c '
import re, sys
TODO = re.compile(r"\(owner: [A-Za-z0-9_.-]+(, [^)\s][^)]*)?\): \S")
REF = re.compile(r"\(owner: [A-Za-z0-9_.-]+, [^)\s][^)]*\): \S")
bad = []
for path in sys.stdin.buffer.read().decode().split("\0"):
    if not path:
        continue
    try:
        text = open(path, encoding="utf-8").read()
    except (UnicodeDecodeError, OSError):
        continue  # binary or unreadable
    for number, line in enumerate(text.splitlines(), 1):
        for found in re.finditer(r"\b(TODO|HACK|FIXME)\b", line):
            rest = line[found.end():found.end() + 160]
            accepted = REF if found.group(1) != "TODO" else TODO
            if accepted.match(rest) is None:
                bad.append(f"{path}:{number}:{line.strip()}")
if bad:
    print("Malformed TODO/HACK/FIXME (use TODO(owner: name, ref): text; HACK and FIXME need the ref):", file=sys.stderr)
    print("\n".join(bad), file=sys.stderr)
    sys.exit(1)
print("todo discipline OK")
'
