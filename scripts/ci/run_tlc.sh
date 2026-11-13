#!/usr/bin/env bash
# Checks the TLA+ model of the notch lifecycle (specs/NotchLifecycle.tla) with TLC. Needs Java 11+ and curl.
#
#   bash scripts/ci/run_tlc.sh
#
# What must happen (anything else exits non-zero):
#   1. specs/NotchLifecycle.cfg            PASSES  (the checks that hold for today's code)
#   2. specs/NotchLifecycle_plan_gaps.cfg  each listed check FAILS on its own run (the known gaps)
#   3. specs/NotchLifecycle_redproof.cfg   FAILS on INV_APPLY_1 (planted "Apply without Approve" bug)
#
# tla2tools.jar is a pinned release, checked against its SHA-256 BEFORE it is used; a failed download or a
# wrong hash stops the run (fail closed). Logs, with the counterexample traces: artifacts/tlc/*.log.
# Never reads secrets.
set -euo pipefail

TLA_VERSION="v1.7.4"
TLA_URL="https://github.com/tlaplus/tlaplus/releases/download/${TLA_VERSION}/tla2tools.jar"
TLA_SHA256="936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88"

# The checks that must hold, and the gaps that must fail. These lists, the cfg files and
# docs/architecture/notch-lifecycle.md (sections 4 and 5) must change together.
EXPECTED_HOLDS="TypeOK INV_PRIV_1 INV_APPLY_1 INV_APPLY_2_FIELD INV_APPLY_3 INV_TALK_1 INV_FOCUS_AT_MOST_ONE INV_TALK_1_RELEASE LIVE_1_RESPONDING LIVE_2"
EXPECTED_GAPS="INV_STOP_1 INV_APPLY_2_FILE INV_FOCUS_1_EXACT INV_PRIV_1_DEVICE INV_PRIV_2 LIVE_1_THINKING"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SPECS="$ROOT/specs"
OUT="${TLC_OUT_DIR:-$ROOT/artifacts/tlc}"
CACHE="${TLC_CACHE_DIR:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/tla2tools-${TLA_VERSION}}"
JAR="$CACHE/tla2tools.jar"
HEAP="${TLC_HEAP:-4g}"

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}

fail() { echo "TLC FAILED: $*"; exit 1; }

command -v java >/dev/null 2>&1 || fail "BLOCKED: Java 11 or newer is not installed, so TLC cannot run."
command -v curl >/dev/null 2>&1 || fail "BLOCKED: curl is not installed, so tla2tools.jar cannot be downloaded."

# --- 1. the pinned jar, verified before use -------------------------------------------------------------
mkdir -p "$CACHE"
if [ -f "$JAR" ] && [ "$(sha256_of "$JAR")" = "$TLA_SHA256" ]; then
  echo "tla2tools.jar $TLA_VERSION already cached, SHA-256 matches."
else
  rm -f "$JAR" "$JAR.part"
  curl -fsSL --retry 3 --retry-delay 5 -o "$JAR.part" "$TLA_URL" \
    || { rm -f "$JAR.part"; fail "could not download $TLA_URL"; }
  actual="$(sha256_of "$JAR.part")"
  if [ "$actual" != "$TLA_SHA256" ]; then
    rm -f "$JAR.part"
    fail "tla2tools.jar SHA-256 mismatch: expected $TLA_SHA256, got $actual. Not using it."
  fi
  mv "$JAR.part" "$JAR"
  echo "tla2tools.jar $TLA_VERSION downloaded, SHA-256 matches ($TLA_SHA256)."
fi

# --- 2. a clean work folder: the spec plus every cfg we run ----------------------------------------------
rm -rf "$OUT"
WORK="$OUT/work"
mkdir -p "$WORK"
cp "$SPECS/NotchLifecycle.tla" "$SPECS/NotchLifecycle.cfg" "$SPECS/NotchLifecycle_redproof.cfg" "$WORK/"

# Names listed under INVARIANT(S) / PROPERTY(PROPERTIES) in a cfg, as "INVARIANT name" / "PROPERTY name".
cfg_checks() {
  awk '/^INVARIANTS?([[:space:]]|$)/ {m="INVARIANT"; next}
       /^PROPERT(Y|IES)([[:space:]]|$)/ {m="PROPERTY"; next}
       /^[A-Z_]+([[:space:]]|$)/ {m=""; next}
       m != "" && $1 ~ /^[A-Za-z_][A-Za-z0-9_]*$/ {print m, $1}' "$1"
}
names_of() { cfg_checks "$1" | awk '{print $2}' | sort | tr '\n' ' ' | sed 's/ $//'; }
sorted() { echo "$1" | tr ' ' '\n' | sort | tr '\n' ' ' | sed 's/ $//'; }

[ "$(names_of "$SPECS/NotchLifecycle.cfg")" = "$(sorted "$EXPECTED_HOLDS")" ] \
  || fail "specs/NotchLifecycle.cfg checks [$(names_of "$SPECS/NotchLifecycle.cfg")], expected [$(sorted "$EXPECTED_HOLDS")]. The doc, the cfg and this script must move together."
[ "$(names_of "$SPECS/NotchLifecycle_plan_gaps.cfg")" = "$(sorted "$EXPECTED_GAPS")" ] \
  || fail "specs/NotchLifecycle_plan_gaps.cfg lists [$(names_of "$SPECS/NotchLifecycle_plan_gaps.cfg")], expected [$(sorted "$EXPECTED_GAPS")]. The doc, the cfg and this script must move together."

# One cfg per gap: the gaps file's header (everything above its first INVARIANT/PROPERTY block) + one check.
header="$(awk '/^(INVARIANTS?|PROPERT(Y|IES))([[:space:]]|$)/ {exit} {print}' "$SPECS/NotchLifecycle_plan_gaps.cfg")"
while read -r kind name; do
  printf '%s\n\n%s\n    %s\n' "$header" "$kind" "$name" > "$WORK/gap_${name}.cfg"
done < <(cfg_checks "$SPECS/NotchLifecycle_plan_gaps.cfg")

# --- 3. run TLC -----------------------------------------------------------------------------------------
run_tlc() { # $1 = run name, $2 = cfg file name in $WORK; prints the log, sets RC
  local log="$OUT/$1.log"
  echo
  echo "=== TLC: $1 ($2) ==="
  set +e
  (cd "$WORK" && java -XX:+UseParallelGC -Xmx"$HEAP" -cp "$JAR" tlc2.TLC \
      -workers auto -cleanup -metadir "$WORK/states_$1" -config "$2" NotchLifecycle.tla < /dev/null) 2>&1 | tee "$log"
  RC=${PIPESTATUS[0]}
  set -e
}

SUMMARY=()
BAD=0
note() { SUMMARY+=("$1"); }

# TLC exit codes (tlc2.output.EC.ExitStatus in 1.7.4): 0 success, 12 safety violation, 13 liveness violation.
run_tlc main NotchLifecycle.cfg
if [ "$RC" -eq 0 ] && grep -q "Model checking completed. No error has been found." "$OUT/main.log"; then
  note "PASS  main: every check that holds today passed ($(sorted "$EXPECTED_HOLDS"))"
else
  note "FAIL  main: expected a pass, TLC exit $RC. A check that held is now broken, or the model does not parse. See artifacts/tlc/main.log."
  BAD=1
fi

while read -r kind name; do
  run_tlc "gap_$name" "gap_${name}.cfg"
  log="$OUT/gap_$name.log"
  if [ "$RC" -eq 0 ]; then
    note "FAIL  gap $name: it now PASSES. If the code really closed this gap, update docs/architecture/notch-lifecycle.md, move $name from NotchLifecycle_plan_gaps.cfg to NotchLifecycle.cfg and update the lists in this script, all in the same PR: the doc and the cfg must move together."
    BAD=1
  elif { [ "$RC" -eq 12 ] || [ "$RC" -eq 13 ]; } \
       && grep -Eq "(Invariant|Action property) $name is violated|Temporal properties were violated" "$log"; then
    note "OK    gap $name: fails as expected, counterexample in artifacts/tlc/gap_$name.log"
  else
    note "FAIL  gap $name: TLC exit $RC without the expected counterexample (a parse or model error?). See artifacts/tlc/gap_$name.log."
    BAD=1
  fi
done < <(cfg_checks "$SPECS/NotchLifecycle_plan_gaps.cfg")

run_tlc redproof NotchLifecycle_redproof.cfg
if [ "$RC" -eq 12 ] && grep -q "Invariant INV_APPLY_1 is violated" "$OUT/redproof.log"; then
  note "OK    red proof: the planted 'Apply without Approve' bug is caught by INV_APPLY_1 (trace in artifacts/tlc/redproof.log)"
else
  note "FAIL  red proof: expected INV_APPLY_1 to catch the planted bug, TLC exit $RC. The check would miss a real bug."
  BAD=1
fi

rm -rf "$WORK"/states_*

echo
echo "=== TLC summary (tla2tools $TLA_VERSION) ==="
for line in "${SUMMARY[@]}"; do echo "  $line"; done
if [ "$BAD" -ne 0 ]; then
  echo "TLC FAILED: at least one expectation above was not met."
  exit 1
fi
echo "TLC OK: main passed, every known gap still fails, the red proof is caught."
