#!/usr/bin/env bash
# Godless law guard. Registry S00.
#
# L1 LAYERING and the mechanical half of L2 DETERMINISM, enforced as a build
# failure rather than as a rule an agent has to remember. The asmdef already
# makes an L1 violation a compile error inside the Editor; this makes it a
# failure everywhere else, including in a session with no Editor running.
#
# Comments are stripped before matching. A guard that fires on the doc comment
# explaining the rule teaches everyone to ignore the guard.
#
# Usage: Tools/check-laws.sh   (exit 0 clean, 1 on any violation)

set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

SIM="Assets/Sim"
fail=0

[ -d "$SIM" ] || { echo "no $SIM directory"; exit 1; }

# Blank out comment bodies while preserving line count and line numbers, so a
# match still reports the line it is really on.
strip_comments() {
  perl -0777 -pe '
    s{/\*.*?\*/}{ $& =~ s/[^\n]/ /gr }gse;
    s{//[^\n]*}{}g;
  ' "$1"
}

scan() {  # scan <pattern>
  local pattern="$1"
  local f
  while IFS= read -r f; do
    strip_comments "$f" | grep -nE "$pattern" | sed "s|^|$f:|"
  done < <(find "$SIM" -name '*.cs' -type f | sort)
}

check() {  # check <law> <message> <pattern>
  local hits
  hits=$(scan "$3")
  if [ -n "$hits" ]; then
    printf '\n  %s  %s\n' "$1" "$2"
    printf '%s\n' "$hits" | sed 's/^/      /'
    fail=1
  fi
}

# ── L1: the sim assembly may never reference Unity ───────────────────────────
check "L1" "Unity referenced from the sim assembly" \
  '(^|[^A-Za-z0-9_.])UnityEngine\.|using[[:space:]]+UnityEngine'

# ── L2: determinism. Seeded streams only; no wall clock; no unordered walks ──
check "L2" "wall-clock read in the sim core" \
  '(^|[^A-Za-z0-9_.])DateTime\.(Now|UtcNow|Today)'

check "L2" "unseeded Random — use StreamRegistry, keyed by hashed string id" \
  'new[[:space:]]+(System\.)?Random[[:space:]]*\('

check "L2" "nondeterministic source in the sim core" \
  '(^|[^A-Za-z0-9_.])(Guid\.NewGuid|Environment\.TickCount|Stopwatch\.GetTimestamp)'

check "L2" "GetHashCode is randomised per process — use StableHash" \
  '\.GetHashCode[[:space:]]*\('

# foreach over a Dictionary/HashSet field: iteration order is not guaranteed.
check "L2" "unordered collection iterated — sort the keys or use an ordered structure" \
  'foreach[[:space:]]*\([^)]*\bin[[:space:]]+_?[A-Za-z0-9_]*(Dict|Dictionary|Map|Set|Lookup)\b'

if [ "$fail" -eq 0 ]; then
  n=$(find "$SIM" -name '*.cs' -type f | wc -l | tr -d ' ')
  echo "laws ok — L1 and L2 clean across $SIM ($n files)"
else
  printf '\n  Laws are not style preferences. Each is cheap now and unrecoverable later.\n  See CLAUDE.md.\n\n'
fi
exit "$fail"
