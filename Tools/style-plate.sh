#!/usr/bin/env bash
# The style plate (S29): a fixed camera on a fixed seed, at fixed years.
#
# Run it after anything that could change how the world looks — the grammar,
# the tileset, the palette, the mesher, the shader — and look at the diff.
# It catches "the grammar change made every roof flat", which no assertion
# will, because nobody thinks to assert it until after it has happened.
#
# Needs the Editor open on the project. The plate lands in Screenshots/ and
# is committed, so `git diff` shows the change as pictures.
set -euo pipefail

harness=$(unity command find_gameobjects --type Godless.Unity.StyleHarness --json 2>/dev/null \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["result"]["gameObjects"][0]["instanceId"])')

unity command set_serialized_field --target "$harness" --field capture --value true >/dev/null
unity command editor_play >/dev/null

for _ in $(seq 1 60); do
  sleep 5
  state=$(unity command editor_state --json 2>/dev/null | grep -c playing || true)
  [ "$state" = "0" ] && break
done

unity command set_serialized_field --target "$harness" --field capture --value false >/dev/null
unity command save_scene >/dev/null
ls -la Screenshots/
