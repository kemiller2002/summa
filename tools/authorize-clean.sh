#!/usr/bin/env bash
# The pre-step before every Conditor authorize (WI-0052): a Conditor lock
# records structural findings for files on disk, so build outputs present at
# authorize time end up in the lock. This removes them, refuses if any remain,
# and only then runs the given command.
#
#   tools/authorize-clean.sh [--root DIR] -- COMMAND [ARGS...]
#       Remove build outputs under DIR (default: this repository), refuse with
#       exit 3 if any remain, otherwise exec COMMAND.
#   tools/authorize-clean.sh [--root DIR] --verify-only
#       Remove nothing; exit 3 and list them if any build output exists, else 0.
#
# Build outputs: bin and obj directories, build, test-results and
# playwright-report, anywhere outside node_modules and .git.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
verify_only=false

while [ $# -gt 0 ]; do
  case "$1" in
    --root) root="$(cd "$2" && pwd)"; shift 2 ;;
    --verify-only) verify_only=true; shift ;;
    --) shift; break ;;
    *) echo "usage: $0 [--root DIR] (--verify-only | -- COMMAND [ARGS...])" >&2; exit 2 ;;
  esac
done

outputs() {
  find "$root" \( -path "$root/node_modules" -o -path "$root/.git" \) -prune -o \
    -type d \( -name bin -o -name obj -o -name build -o -name test-results -o -name playwright-report \) -print -prune
}

if [ "$verify_only" = false ]; then
  if [ $# -eq 0 ]; then
    echo "usage: $0 [--root DIR] (--verify-only | -- COMMAND [ARGS...])" >&2
    exit 2
  fi
  outputs | while IFS= read -r dir; do rm -rf "$dir"; done
fi

left="$(outputs)"
if [ -n "$left" ]; then
  echo "refusing: build outputs present under $root:" >&2
  echo "$left" >&2
  exit 3
fi

echo "clean: no build outputs under $root"
if [ "$verify_only" = true ]; then
  exit 0
fi
exec "$@"
