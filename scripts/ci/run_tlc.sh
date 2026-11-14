#!/usr/bin/env bash
# Checks the TLA+ model of the notch lifecycle (specs/NotchLifecycle.tla) with TLC. Needs Java 11+ and curl.
#
#   bash scripts/ci/run_tlc.sh            every run in the table below, with MaxActive = 2 (PRs, pushes, nightly)
#   bash scripts/ci/run_tlc.sh --wide     only specs/NotchLifecycle.cfg, with MaxActive = 3 (job tlc-wide: PRs that touch the model, pushes, nightly)
#
# What must happen (anything else exits non-zero):
#   main       specs/NotchLifecycle.cfg PASSES (the checks that hold for today's code)
#   redproof   each main check CATCHES its planted bug (Plant = ...): no main check is vacuous
#   witness    each main check's trigger is REACHED (an invariant "the trigger never happens" FAILS)
#   gap        each check in specs/NotchLifecycle_plan_gaps.cfg FAILS with Fix = "none"
#   fix        each gap PASSES with its own Fix on (else its counterexample has another cause)
#   fixwitness each gap's trigger is still REACHED with its Fix on (the fix did not just switch the trigger off)
#
# tla2tools.jar is a pinned release, checked against its SHA-256 BEFORE it is used; a failed download or a
# wrong hash stops the run (fail closed). Every TLC output, with its counterexample trace, is printed to this log;
# a local run also keeps a copy in artifacts/tlc/*.log.
# Never reads secrets.
set -euo pipefail

TLA_VERSION="v1.7.4"
TLA_URL="https://github.com/tlaplus/tlaplus/releases/download/${TLA_VERSION}/tla2tools.jar"
TLA_SHA256="936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88"

# The checks that must hold, and the gaps that must fail. These lists, the cfg files and
# docs/architecture/notch-lifecycle.md (sections 4 and 5) must change together.
EXPECTED_HOLDS="TypeOK INV_PRIV_1 INV_APPLY_1 INV_APPLY_2_FIELD INV_APPLY_3 INV_TALK_1 INV_FOCUS_AT_MOST_ONE INV_TALK_1_RELEASE LIVE_1_RESPONDING LIVE_2"
EXPECTED_GAPS="INV_STOP_1 INV_APPLY_2_FILE INV_FOCUS_1_EXACT INV_PRIV_1_DEVICE INV_PRIV_2 LIVE_1_THINKING INV_TALK_1_DEVICE"
# TypeOK is a model sanity check (every variable stays in its value set), not a behaviour rule: no red proof.
NO_RED_PROOF="TypeOK"

# The run table: role | Plant | Fix | INVARIANT or PROPERTY | check | which main check or gap it covers.
# Expectation by role: redproof, witness, gap, fixwitness must FAIL on the named check; fix must PASS.
RUNS="
redproof   | PinIgnoresCapture       | none | INVARIANT | INV_PRIV_1            | INV_PRIV_1
redproof   | ApplyWithoutApprove     | none | INVARIANT | INV_APPLY_1           | INV_APPLY_1
redproof   | FieldUndoKeepsJournal   | none | INVARIANT | INV_APPLY_2_FIELD     | INV_APPLY_2_FIELD
redproof   | UndoWithoutJournal      | none | INVARIANT | INV_APPLY_2_FIELD     | INV_APPLY_2_FIELD
redproof   | CommitIgnoresGeneration | none | INVARIANT | INV_APPLY_3           | INV_APPLY_3
redproof   | StopKeepsMic            | none | INVARIANT | INV_TALK_1            | INV_TALK_1
redproof   | ReleaseKeepsIndicator   | none | PROPERTY  | INV_TALK_1_RELEASE    | INV_TALK_1_RELEASE
redproof   | EndNotGuarded           | none | INVARIANT | INV_FOCUS_AT_MOST_ONE | INV_FOCUS_AT_MOST_ONE
redproof   | NoRespondingTimer       | none | PROPERTY  | LIVE_1_RESPONDING     | LIVE_1_RESPONDING
redproof   | NoRetractTimer          | none | PROPERTY  | LIVE_2                | LIVE_2
witness    | none | none | INVARIANT | W_PRIV_1_MIC        | INV_PRIV_1
witness    | none | none | INVARIANT | W_PRIV_1_CAPTURE    | INV_PRIV_1
witness    | none | none | INVARIANT | W_APPLY_1           | INV_APPLY_1
witness    | none | none | INVARIANT | W_APPLY_2_FIELD     | INV_APPLY_2_FIELD
witness    | none | none | INVARIANT | W_APPLY_3           | INV_APPLY_3
witness    | none | none | INVARIANT | W_TALK_1_HOLD       | INV_TALK_1
witness    | none | none | PROPERTY  | W_TALK_1_STOP       | INV_TALK_1
witness    | none | none | PROPERTY  | W_TALK_1_RELEASE    | INV_TALK_1_RELEASE
witness    | none | none | INVARIANT | W_FOCUS_END         | INV_FOCUS_AT_MOST_ONE
witness    | none | none | INVARIANT | W_LIVE_1_RESPONDING | LIVE_1_RESPONDING
witness    | none | none | INVARIANT | W_LIVE_2            | LIVE_2
gap        | none | none | INVARIANT | INV_STOP_1          | INV_STOP_1
gap        | none | none | PROPERTY  | INV_PRIV_2          | INV_PRIV_2
gap        | none | none | INVARIANT | INV_APPLY_2_FILE    | INV_APPLY_2_FILE
gap        | none | none | PROPERTY  | LIVE_1_THINKING     | LIVE_1_THINKING
gap        | none | none | INVARIANT | INV_FOCUS_1_EXACT   | INV_FOCUS_1_EXACT
gap        | none | none | INVARIANT | INV_PRIV_1_DEVICE   | INV_PRIV_1_DEVICE
gap        | none | none | PROPERTY  | INV_TALK_1_DEVICE   | INV_TALK_1_DEVICE
fix        | none | F4   | INVARIANT | INV_STOP_1          | INV_STOP_1
fix        | none | F5   | PROPERTY  | INV_PRIV_2          | INV_PRIV_2
fix        | none | F6   | INVARIANT | INV_APPLY_2_FILE    | INV_APPLY_2_FILE
fix        | none | F7   | PROPERTY  | LIVE_1_THINKING     | LIVE_1_THINKING
fix        | none | F9   | INVARIANT | INV_FOCUS_1_EXACT   | INV_FOCUS_1_EXACT
fix        | none | F10  | INVARIANT | INV_PRIV_1_DEVICE   | INV_PRIV_1_DEVICE
fix        | none | F10  | PROPERTY  | INV_TALK_1_DEVICE   | INV_TALK_1_DEVICE
fixwitness | none | F4   | PROPERTY  | W_STOP_1            | INV_STOP_1
fixwitness | none | F5   | PROPERTY  | W_PRIV_2            | INV_PRIV_2
fixwitness | none | F6   | INVARIANT | W_APPLY_2_FILE      | INV_APPLY_2_FILE
fixwitness | none | F7   | INVARIANT | W_THINKING          | LIVE_1_THINKING
fixwitness | none | F9   | INVARIANT | W_FOCUS_1_EXACT     | INV_FOCUS_1_EXACT
fixwitness | none | F10  | PROPERTY  | W_DEVICE            | INV_PRIV_1_DEVICE
fixwitness | none | F10  | PROPERTY  | W_DEVICE            | INV_TALK_1_DEVICE
"

MODE="all"
case "${1:-}" in
  "") ;;
  --wide) MODE="wide" ;;
  *) echo "usage: bash scripts/ci/run_tlc.sh [--wide]"; exit 2 ;;
esac

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

# --- 2. the lists must agree before anything runs ------------------------------------------------------
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
sorted() { echo "$1" | tr ' ' '\n' | sed '/^$/d' | sort | tr '\n' ' ' | sed 's/ $//'; }
has() { case " $1 " in *" $2 "*) return 0 ;; *) return 1 ;; esac; }

[ "$(names_of "$SPECS/NotchLifecycle.cfg")" = "$(sorted "$EXPECTED_HOLDS")" ] \
  || fail "specs/NotchLifecycle.cfg checks [$(names_of "$SPECS/NotchLifecycle.cfg")], expected [$(sorted "$EXPECTED_HOLDS")]. The doc, the cfg and this script must move together."
[ "$(names_of "$SPECS/NotchLifecycle_plan_gaps.cfg")" = "$(sorted "$EXPECTED_GAPS")" ] \
  || fail "specs/NotchLifecycle_plan_gaps.cfg lists [$(names_of "$SPECS/NotchLifecycle_plan_gaps.cfg")], expected [$(sorted "$EXPECTED_GAPS")]. The doc, the cfg and this script must move together."

# The run table, one run per line: role plant fix kind check covers (pipes and blanks squeezed out).
TABLE="$(echo "$RUNS" | sed '/^[[:space:]]*$/d' | tr -s ' |' ' ' | sed 's/^ //; s/ $//')"
rows_for() { echo "$TABLE" | awk -v r="$1" -v c="$2" '$1 == r && $6 == c'; }
while read -r role plant fix kind check covers; do
  case "$role" in redproof|witness|gap|fix|fixwitness) ;; *) fail "run table: unknown role '$role'" ;; esac
  case "$kind" in INVARIANT|PROPERTY) ;; *) fail "run table: '$kind' must be INVARIANT or PROPERTY" ;; esac
  if [ "$role" = redproof ] && [ "$plant" = none ]; then fail "run table: red proof for $covers has no Plant"; fi
  if [ "$role" != redproof ] && [ "$plant" != none ]; then fail "run table: only a red proof may set a Plant"; fi
  if { [ "$role" = fix ] || [ "$role" = fixwitness ]; } && [ "$fix" = none ]; then fail "run table: $role for $covers has no Fix"; fi
  if { [ "$role" = redproof ] || [ "$role" = witness ] || [ "$role" = gap ]; } && [ "$fix" != none ]; then
    fail "run table: a $role run must use Fix none"
  fi
done <<< "$TABLE"
for c in $EXPECTED_HOLDS; do
  if ! has "$NO_RED_PROOF" "$c"; then
    [ -n "$(rows_for redproof "$c")" ] || fail "main check $c has no red proof in the run table: it could be vacuous."
    [ -n "$(rows_for witness "$c")" ] || fail "main check $c has no reachability witness in the run table."
  fi
done
for c in $EXPECTED_GAPS; do
  [ "$(rows_for gap "$c" | wc -l)" -eq 1 ] || fail "gap $c needs exactly one gap row in the run table."
  [ -n "$(rows_for fix "$c")" ] || fail "gap $c has no fix run: nothing shows its counterexample has the cause we think."
  [ -n "$(rows_for fixwitness "$c")" ] || fail "gap $c has no witness under its fix: the fix could just switch the trigger off."
  fixes="$(rows_for fix "$c" | awk '{print $3}' | sort -u)"
  wfixes="$(rows_for fixwitness "$c" | awk '{print $3}' | sort -u)"
  [ "$fixes" = "$wfixes" ] || fail "gap $c: its fix run and its fix witness use different Fix switches."
done

# The table must match the model exactly: every planted bug in the .tla's Plants set has a red-proof row, every
# W_* witness defined in the .tla has a witness or fixwitness row, every Fix value has a fix row, and nothing else.
# The lists are read from the .tla with a strict parse; an empty or malformed list fails closed.
TLA="$SPECS/NotchLifecycle.tla"
tla_set() { # $1 = Plants or Fixes: the quoted names of that set definition, comments stripped, "none" dropped
  awk -v name="$1" '
    $0 ~ "^" name " ==[[:space:]]*$" {inside = 1; next}
    inside {
      line = $0; p = index(line, "\134*"); if (p > 0) line = substr(line, 1, p - 1); text = text " " line
      if (line ~ /}/) {inside = 0; done = 1}
    }
    END {
      if (!done) exit 1
      n = split(text, parts, "\"")
      for (i = 2; i <= n; i += 2) if (parts[i] != "none") print parts[i]
    }' "$TLA" | sort
}
uniq_sorted() { sed '/^$/d' | sort -u; }
PLANTS_TLA="$(tla_set Plants)" || fail "could not parse the Plants set in specs/NotchLifecycle.tla."
FIXES_TLA="$(tla_set Fixes)" || fail "could not parse the Fixes set in specs/NotchLifecycle.tla."
WITNESSES_TLA="$(grep -oE '^W_[A-Z0-9_]+[[:space:]]+==' "$TLA" | awk '{print $1}' | sort)"
[ -n "$PLANTS_TLA" ] && [ -n "$FIXES_TLA" ] && [ -n "$WITNESSES_TLA" ] \
  || fail "the Plants set, the Fixes set or the W_* witnesses in specs/NotchLifecycle.tla came out empty."
for list in "$PLANTS_TLA" "$FIXES_TLA" "$WITNESSES_TLA"; do
  [ "$(echo "$list" | sort -u)" = "$list" ] || fail "specs/NotchLifecycle.tla names one Plant, Fix or witness twice."
done
PLANTS_TABLE="$(echo "$TABLE" | awk '$1 == "redproof" {print $2}' | sort)"
FIXES_TABLE="$(echo "$TABLE" | awk '$1 == "fix" {print $3}' | uniq_sorted)"
WITNESSES_TABLE="$(echo "$TABLE" | awk '$1 == "witness" || $1 == "fixwitness" {print $5}' | uniq_sorted)"
[ "$PLANTS_TABLE" = "$(echo "$PLANTS_TABLE" | sort -u)" ] || fail "run table: a Plant has more than one red-proof row."
[ "$PLANTS_TABLE" = "$PLANTS_TLA" ] \
  || fail "red-proof rows [$(echo $PLANTS_TABLE)] do not match the Plants in the .tla [$(echo $PLANTS_TLA)]. Every planted bug needs exactly one red-proof row."
[ "$FIXES_TABLE" = "$FIXES_TLA" ] \
  || fail "fix rows [$(echo $FIXES_TABLE)] do not match the Fixes in the .tla [$(echo $FIXES_TLA)]. Every Fix needs a fix row."
[ "$WITNESSES_TABLE" = "$WITNESSES_TLA" ] \
  || fail "witness rows [$(echo $WITNESSES_TABLE)] do not match the W_* witnesses in the .tla [$(echo $WITNESSES_TLA)]. Every witness needs a row."
while read -r role plant fix kind check covers; do
  case "$role" in
    redproof)       has "$EXPECTED_HOLDS" "$check" && [ "$check" = "$covers" ] || fail "run table: red proof row for $check must name a main check it covers." ;;
    witness)        case "$check" in W_*) ;; *) fail "run table: witness $check must be a W_* definition." ;; esac
                    has "$EXPECTED_HOLDS" "$covers" || fail "run table: witness $check covers $covers, which is not a main check." ;;
    gap|fix)        has "$EXPECTED_GAPS" "$check" && [ "$check" = "$covers" ] || fail "run table: $role row for $check must name a gap." ;;
    fixwitness)     case "$check" in W_*) ;; *) fail "run table: fix witness $check must be a W_* definition." ;; esac
                    has "$EXPECTED_GAPS" "$covers" || fail "run table: fix witness $check covers $covers, which is not a gap." ;;
  esac
done <<< "$TABLE"

# --- 3. run TLC -----------------------------------------------------------------------------------------
MAX_ACTIVE=2
[ "$MODE" = wide ] && MAX_ACTIVE=3

make_cfg() { # $1 file, $2 plant, $3 fix, $4 kind, $5 check
  printf 'CONSTANTS\n    None = None\n    MaxActive = %s\n    Plant = "%s"\n    Fix = "%s"\n\nSPECIFICATION Spec\n\nCHECK_DEADLOCK FALSE\n\n%s\n    %s\n' \
    "$MAX_ACTIVE" "$2" "$3" "$4" "$5" > "$WORK/$1"
}

run_tlc() { # $1 = run name, $2 = cfg file name in $WORK; prints the log, sets RC
  local log="$OUT/$1.log"
  echo
  echo "=== TLC: $1 ($2) ==="
  set +e
  (cd "$WORK" && java -XX:+UseParallelGC -Xmx"$HEAP" -cp "$JAR" tlc2.TLC \
      -workers auto -cleanup -metadir "$WORK/states_$1" -config "$2" NotchLifecycle.tla < /dev/null) 2>&1 | tee "$log"
  RC=${PIPESTATUS[0]}
  set -e
  rm -rf "$WORK/states_$1"
}

# TLC exit codes (tlc2.output.EC.ExitStatus in 1.7.4): 0 success, 12 safety violation, 13 liveness violation.
passed() { [ "$RC" -eq 0 ] && grep -q "Model checking completed. No error has been found." "$1"; }
failed_on() { # $1 log, $2 kind, $3 check: TLC reports a counterexample for exactly this check
  { [ "$RC" -eq 12 ] || [ "$RC" -eq 13 ]; } || return 1
  if [ "$2" = INVARIANT ]; then
    grep -Eq "Invariant $3 is violated" "$1"
  else
    grep -Eq "Action property $3 is violated|Temporal properties were violated" "$1"
  fi
}

SUMMARY=()
BAD=0
note() { SUMMARY+=("$1"); }

if [ "$MODE" = wide ]; then
  sed -i.bak 's/^    MaxActive = 2$/    MaxActive = 3/' "$WORK/NotchLifecycle.cfg"
  grep -q '^    MaxActive = 3$' "$WORK/NotchLifecycle.cfg" || fail "could not set MaxActive = 3 in the main cfg copy."
fi

run_tlc main NotchLifecycle.cfg
if passed "$OUT/main.log"; then
  note "PASS  main (MaxActive $MAX_ACTIVE): every check that holds today passed"
else
  note "FAIL  main (MaxActive $MAX_ACTIVE): expected a pass, TLC exit $RC. A check that held is now broken, or the model does not parse (trace above in this log; also artifacts/tlc/main.log when run locally)."
  BAD=1
fi

if [ "$MODE" = all ]; then
  run_tlc redproof_file NotchLifecycle_redproof.cfg
  if failed_on "$OUT/redproof_file.log" INVARIANT INV_APPLY_1; then
    note "OK    redproof file: NotchLifecycle_redproof.cfg is caught by INV_APPLY_1"
  else
    note "FAIL  redproof file: NotchLifecycle_redproof.cfg was not caught by INV_APPLY_1 (TLC exit $RC)."
    BAD=1
  fi

  n=0
  while read -r role plant fix kind check covers; do
    n=$((n + 1))
    name="$(printf '%02d' "$n")_${role}_${check}_${plant}_${fix}"
    make_cfg "$name.cfg" "$plant" "$fix" "$kind" "$check"
    run_tlc "$name" "$name.cfg"
    log="$OUT/$name.log"
    if [ "$role" = fix ]; then
      if passed "$log"; then
        note "OK    fix        $check passes with Fix $fix"
      elif failed_on "$log" "$kind" "$check"; then
        note "FAIL  fix        $check still FAILS with Fix $fix: its counterexample has another cause than the finding says. Update the doc and the Fix (trace above in this log; also artifacts/tlc/$name.log when run locally)."
        BAD=1
      else
        note "FAIL  fix        $check with Fix $fix: TLC exit $RC (a parse or model error?) (trace above in this log; also artifacts/tlc/$name.log when run locally)."
        BAD=1
      fi
      continue
    fi
    if failed_on "$log" "$kind" "$check"; then
      case "$role" in
        redproof)   note "OK    redproof   $covers catches Plant $plant" ;;
        witness)    note "OK    witness    $covers: its trigger is reached ($check)" ;;
        gap)        note "OK    gap        $check fails as expected (trace above in this log; also artifacts/tlc/$name.log when run locally)" ;;
        fixwitness) note "OK    fixwitness $covers: its trigger is still reached with Fix $fix ($check)" ;;
      esac
    elif passed "$log"; then
      case "$role" in
        redproof)   note "FAIL  redproof   $covers MISSED Plant $plant: the check is vacuous or too weak." ;;
        witness)    note "FAIL  witness    $covers: its trigger is never reached ($check passed), so the check proves nothing." ;;
        gap)        note "FAIL  gap        $check now PASSES. If the code really closed this gap, update docs/architecture/notch-lifecycle.md, move $check from NotchLifecycle_plan_gaps.cfg to NotchLifecycle.cfg and update the lists in this script, all in the same PR: the doc and the cfg must move together." ;;
        fixwitness) note "FAIL  fixwitness $covers: with Fix $fix the trigger is never reached, so the fix run passes for nothing." ;;
      esac
      BAD=1
    else
      note "FAIL  $role $check (Plant $plant, Fix $fix): TLC exit $RC without the expected counterexample (a parse or model error?) (trace above in this log; also artifacts/tlc/$name.log when run locally)."
      BAD=1
    fi
  done <<< "$TABLE"
fi

echo
echo "=== TLC summary (tla2tools $TLA_VERSION, mode $MODE, MaxActive $MAX_ACTIVE) ==="
for line in "${SUMMARY[@]}"; do echo "  $line"; done
if [ "$BAD" -ne 0 ]; then
  echo "TLC FAILED: at least one expectation above was not met."
  exit 1
fi
if [ "$MODE" = wide ]; then
  echo "TLC OK: the main checks pass with MaxActive 3."
else
  echo "TLC OK: main passed, every red proof is caught, every trigger is reached, every gap fails today and passes with its fix."
fi
