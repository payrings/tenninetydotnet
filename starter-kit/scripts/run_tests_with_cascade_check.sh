#!/bin/bash
# Usage: ./scripts/run_tests_with_cascade_check.sh <module-id>
# HOST-SIDE ONLY. Runs the fast tier (Contracts + Unit) inside test-runner.
set -uo pipefail

FEATURE="${1:?Usage: run_tests_with_cascade_check.sh <module-id>}"

# Cascade threshold: how many build errors before we stop trusting an
# incremental fix. Line count is NOT used as a cascade signal – a verbose
# but ordinary failure isn't a dependency cascade.
THRESHOLD_ERRORS="${DOTNET_ERROR_THRESHOLD:-10}"

mkdir -p ".dev-runtime/$FEATURE"
FAIL_LOG=".dev-runtime/$FEATURE/latest-test.log"

# Cache-key the NuGet volume by the hash of ALL lockfiles, so a package change
# anywhere doesn't silently use a stale cache and miss new dependencies.
LOCK_HASH=$(find . -name packages.lock.json -type f -print0 \
  | sort -z | xargs -0 cat 2>/dev/null | sha256sum | cut -d' ' -f1 | head -c 12)
NUGET_CACHE_VOLUME="nuget-cache-${LOCK_HASH:-no-lockfiles}"

# The test-runner image already runs as the non-root 'agent' user (Phase 6),
# so no --user flag is needed here.
#
# Test execution is split into two container invocations with different network
# postures. `dotnet restore --locked-mode` legitimately needs the network (to
# populate the NuGet cache volume), but `dotnet build`/`dotnet test` execute
# arbitrary Coder-written code and must NOT have network access. Running them
# with --network=none closes the one arbitrary-code-execution point in the
# pipeline: a malicious or buggy test can no longer exfiltrate the workspace or
# phone home. Because restore already populated the cache volume, the isolated
# build/test step restores offline from that volume with --no-restore.
run_restore() {
  # Networked. Only ever runs `dotnet restore` (locked-mode). No test code runs.
  docker run --rm \
    -v "$PWD":/workspace \
    -v "$NUGET_CACHE_VOLUME":/home/agent/.nuget/packages \
    --entrypoint bash \
    test-runner -lc "$1"
}

run_isolated() {
  # No network. Runs Coder-authored build + test code. The cache volume is
  # still mounted (read path) so --no-restore resolves everything offline.
  docker run --rm \
    --network=none \
    -v "$PWD":/workspace \
    -v "$NUGET_CACHE_VOLUME":/home/agent/.nuget/packages \
    --entrypoint bash \
    test-runner -lc "$1"
}

# --- Restore (networked, no test code) ---
restore_output=$(run_restore "dotnet restore --locked-mode 2>&1")
restore_rc=$?
if [ "$restore_rc" -ne 0 ]; then
  printf '%s\n' "$restore_output" > "$FAIL_LOG"
  echo "$restore_output"
  echo ""
  echo "Restore failed (exit $restore_rc). Log saved to $FAIL_LOG."
  echo "If a package is missing, run 'dotnet restore' to regenerate packages.lock.json, then commit it."
  exit 1
fi

# --- Build + static analysis (NO network; runs analysers/generators). The
#     container's EXIT CODE is authoritative; the error-string count is only
#     used to distinguish a cascade from an ordinary failure, never to decide
#     pass/fail. A Docker/OOM failure with zero matching strings is still a
#     failure. ---
build_output=$(run_isolated "dotnet build --no-restore -warnaserror -clp:NoSummary 2>&1")
build_rc=$?
printf '%s\n' "$build_output" > "$FAIL_LOG"
build_errors=$(printf '%s\n' "$build_output" | grep -c "error ")

if [ "$build_rc" -ne 0 ]; then
  if [ "$build_errors" -gt "$THRESHOLD_ERRORS" ]; then
    echo "$build_output"
    echo ""
    echo "Cascade threshold exceeded ($build_errors errors > $THRESHOLD_ERRORS)."
    echo "Build error log saved to $FAIL_LOG."
    echo ""
    echo "This is a cascade – too many errors to trust an incremental fix."
    echo "Escalate deliberately when you're ready (one-shot per module):"
    echo "  dev.sh escalate $FEATURE $FAIL_LOG"
    echo ""
    echo "(Not auto-escalating: escalate.py enforces a one-shot-per-module"
    echo " policy and writes escalation-notes.md itself. Auto-running it here"
    echo " would silently consume that one shot and clobber the file.)"
  else
    echo "$build_output"
    echo ""
    echo "Build failed (exit $build_rc). Log saved to $FAIL_LOG."
  fi
  exit 1
fi

# --- Fast-tier tests. Run each project EXPLICITLY rather than filtering by
#     [Trait], so an un-categorised test can never silently drop out of the
#     gate. Each project must also DISCOVER at least one test – an empty gate
#     that reports green is worse than a red one. ---
run_project() {
  local proj="$1" mode="${2:-required}"
  if [ ! -f "$proj" ]; then
    if [ "$mode" = "optional" ]; then
      echo "ABSENT TIER (allowed): $proj"
      return 0
    fi
    echo "MISSING TEST PROJECT: $proj"
    return 1
  fi
  local out
  out=$(run_isolated "dotnet test '$proj' --no-build --no-restore -v normal 2>&1")
  local rc=$?
  printf '%s\n' "$out"
  printf '%s\n' "$out" >> "$FAIL_LOG"
  if [ "$rc" -ne 0 ]; then
    return 1
  fi
  # Guard against a project that ran zero tests (misconfiguration). For a
  # required tier this is fatal; for an optional tier it is reported and
  # tolerated, so an empty Golden/Unit project cannot deadlock module one.
  if printf '%s\n' "$out" | grep -qE "No test (is available|matches)"; then
    if [ "$mode" = "optional" ]; then
      echo "EMPTY TIER (allowed): $proj contains no tests yet."
      return 0
    fi
    echo "EMPTY TEST GATE: $proj discovered no tests."
    return 1
  fi
  return 0
}

# Discover EVERY project of each tier, not just the first ('head -1' silently
# ignored all but one in a multi-project solution). Use a real glob (not
# `ls $pattern`, which word-splits and trips ShellCheck SC2012/SC2086): enable
# nullglob so a no-match pattern expands to nothing instead of a literal.
collect() {
  local pattern="$1"; local -n _out="$2"
  _out=()
  local p had_nullglob
  shopt -q nullglob && had_nullglob=1 || had_nullglob=0
  shopt -s nullglob
  for p in $pattern; do _out+=("$p"); done
  [ "$had_nullglob" = "1" ] || shopt -u nullglob
  # Deterministic order (glob is already sorted, but keep it explicit).
  if [ "${#_out[@]}" -gt 1 ]; then
    mapfile -t _out < <(printf '%s\n' "${_out[@]}" | sort)
  fi
}
collect "tests/*.Contracts/*.Contracts.csproj" CONTRACTS_PROJS
collect "tests/*.Golden/*.Golden.csproj"       GOLDEN_PROJS
collect "tests/*.Unit/*.Unit.csproj"           UNIT_PROJS

: > "$FAIL_LOG"   # reset log now that the build passed

# Contract tests are mandatory: they are the module's API gate.
if [ "${#CONTRACTS_PROJS[@]}" -eq 0 ]; then
  echo "MISSING TEST PROJECT: no tests/*.Contracts/*.Contracts.csproj found."
  exit 1
fi

rc=0
for p in "${CONTRACTS_PROJS[@]}"; do run_project "$p" required || rc=1; done
# Golden and Unit tiers are optional-but-strict: a project that exists must
# contain tests and pass, but a tier that has no tests yet (e.g. before the
# golden harness is written, or a module with no unit tests) must not
# deadlock the very first module. Their absence is reported, not fatal.
for p in "${GOLDEN_PROJS[@]}"; do run_project "$p" optional || rc=1; done
for p in "${UNIT_PROJS[@]}"; do run_project "$p" optional || rc=1; done
exit $rc