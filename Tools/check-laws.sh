#!/usr/bin/env bash
# Godless law guard. Registry S00.
#
# L1 LAYERING and the mechanical half of L2 DETERMINISM, enforced as a build
# failure rather than as a rule an agent has to remember. The asmdef already
# makes an L1 violation a compile error inside the Editor; this makes it a
# failure everywhere else, including in a session with no Editor running.
#
# Usage: Tools/check-laws.sh   (exit 0 clean, 1 on any violation)

set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

SIM="Assets/Sim"
fail=0

report() {   # report <law> <message> <matches>
  printf '\n  %s  %s\n' "$1" "$2"
  printf '%s\n' "$3" | sed 's/^/      /'
  fail=1
}

[ -d "$SIM" ] || { echo "no $SIM directory"; exit 1; }

# ── L1: the sim assembly may never reference Unity ───────────────────────────
hits=$(grep -rn --include='*.cs' -E '(^|[^A-Za-z0-9_.])UnityEngine\.|using[[:space:]]+UnityEngine' "$SIM" 2>/dev/null)
[ -n "$hits" ] && report "L1" "Unity referenced from the sim assembly" "$hits"

# ── L2: determinism. Seeded streams only; no wall clock; no unordered walks ──
hits=$(grep -rn --include='*.cs' -E '(^|[^A-Za-z0-9_.])DateTime\.(Now|UtcNow|Today)' "$SIM" 2>/dev/null)
[ -n "$hits" ] && report "L2" "wall-clock read in the sim core" "$hits"

hits=$(grep -rn --include='*.cs' -E 'new[[:space:]]+(System\.)?Random[[:space:]]*\(' "$SIM" 2>/dev/null)
[ -n "$hits" ] && report "L2" "unseeded Random — use the stream registry, keyed by hashed string id" "$hits"

hits=$(grep -rn --include='*.cs' -E '(^|[^A-Za-z0-9_.])(Guid\.NewGuid|Environment\.TickCount|Stopwatch\.GetTimestamp)' "$SIM" 2>/dev/null)
[ -n "$hits" ] && report "L2" "nondeterministic source in the sim core" "$hits"

# foreach over a Dictionary/HashSet field: iteration order is not guaranteed.
hits=$(grep -rn --include='*.cs' -E 'foreach[[:space:]]*\([^)]*\bin[[:space:]]+_?[A-Za-z0-9_]*(Dict|Dictionary|Map|Set|Lookup)\b' "$SIM" 2>/dev/null)
[ -n "$hits" ] && report "L2" "unordered collection iterated — sort the keys or use an ordered structure" "$hits"

if [ "$fail" -eq 0 ]; then
  echo "laws ok — L1 and L2 clean across $SIM"
else
  printf '\n  Laws are not style preferences. Each is cheap now and unrecoverable later.\n  See CLAUDE.md.\n\n'
fi
exit "$fail"
