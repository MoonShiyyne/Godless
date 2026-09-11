#!/usr/bin/env bash
# Everything a commit must pass, as one command with one exit code.
#
#   Tools/verify.sh && git commit ...
#
# Written after a commit landed on a failing run: the law guard gated it, the
# tests only printed. Chaining a commit on this makes that impossible.

set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

Tools/check-laws.sh || { echo "verify: law guard failed"; exit 1; }

out=$(dotnet test Sim.Tests/Godless.Sim.Tests.csproj --nologo 2>&1)
status=$?
summary=$(printf '%s\n' "$out" | grep -E 'Passed!|Failed!' | sed 's/ - Godless.*//')
if [ $status -ne 0 ] || ! printf '%s' "$summary" | grep -q 'Failed:     0,'; then
  printf '%s\n' "$out" | grep -E '^\s+Failed |Error Message' -A3 | head -40
  echo "verify: tests failed — $summary"
  exit 1
fi

echo "verify: ok — $summary"
