#!/bin/bash
# Usage: ./scripts/run_tests_with_cascade_check.sh <module-id>
# HOST-SIDE ONLY. Runs Contracts + Golden + Unit in independent disposable
# workspaces. No test container receives the real Git workspace or host state.
set -uo pipefail
export LC_ALL=C

FEATURE="${1:?Usage: run_tests_with_cascade_check.sh <module-id>}"
[[ "$FEATURE" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || {
  echo "ERROR: invalid Module ID '$FEATURE'." >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="$(dirname "$SCRIPT_DIR")"
cd "$WORKSPACE" || exit 1
# shellcheck source=runtime.sh
source "$SCRIPT_DIR/runtime.sh"
# shellcheck source=test_sandbox.sh
source "$SCRIPT_DIR/test_sandbox.sh"
tenninety_init_runtime "$WORKSPACE" || exit 1
tenninety_reject_ignored_build_controls || exit 1

THRESHOLD_ERRORS="${DOTNET_ERROR_THRESHOLD:-10}"
FEATURE_RUNTIME="$TENNINETY_RUNTIME_DIR/$FEATURE"
mkdir -p -- "$FEATURE_RUNTIME" || exit 1
FAIL_LOG="$FEATURE_RUNTIME/latest-test.log"

LOCK_HASH="$(tenninety_lock_hash)" || {
  echo "ERROR: could not hash NuGet lockfiles." >&2; exit 1;
}
WORKSPACE_ID="$(printf '%s' "$(pwd -P)" | sha256sum | cut -d' ' -f1 | head -c 12)"
NUGET_CACHE_VOLUME="nuget-cache-${WORKSPACE_ID}-${LOCK_HASH:-no-lockfiles}"

RUN_ROOT="$(mktemp -d "$TENNINETY_RUNTIME_DIR/fast-${FEATURE}.XXXXXX")" || exit 1
cleanup() { find "$RUN_ROOT" -depth -delete 2>/dev/null || true; }
trap cleanup EXIT INT TERM

ORIGINAL_HASH="$(tenninety_workspace_hash)" || {
  echo "WORKSPACE INTEGRITY FAILURE: could not hash the source workspace." >&2
  exit 98
}

SEED="$RUN_ROOT/seed"
tenninety_create_snapshot "$SEED" || {
  echo "ERROR: could not create the disposable test snapshot." >&2
  exit 1
}

restore_output="$(tenninety_restore_seed "$SEED" "$NUGET_CACHE_VOLUME" 2>&1)"
restore_rc=$?
if [ "$restore_rc" -ne 0 ]; then
  printf '%s\n' "$restore_output" > "$FAIL_LOG"
  printf '%s\n' "$restore_output"
  echo "Restore failed (exit $restore_rc). Log saved to $FAIL_LOG."
  exit 1
fi

had_nullglob=0
shopt -q nullglob && had_nullglob=1
shopt -s nullglob
CONTRACTS_PROJS=(tests/*.Contracts/*.Contracts.csproj)
GOLDEN_PROJS=(tests/*.Golden/*.Golden.csproj)
UNIT_PROJS=(tests/*.Unit/*.Unit.csproj)
[ "$had_nullglob" -eq 1 ] || shopt -u nullglob

project_mode() {
  local project="$1" directory source
  directory="$(dirname "$project")"
  source="$(find "$directory" \( -path '*/bin' -o -path '*/obj' \) -prune -o \
       -type f -name '*Tests.cs' -print -quit)" || return 1
  if [ -n "$source" ]; then
    echo required
  else
    echo optional-empty
  fi
}

PROJECT_RUN_NUMBER=0
run_project() {
  local project="$1" mode="${2:-required}" sandbox out rc errors
  [ -f "$project" ] || { echo "MISSING TEST PROJECT: $project"; return 1; }
  PROJECT_RUN_NUMBER=$((PROJECT_RUN_NUMBER + 1))
  sandbox="$RUN_ROOT/project-$PROJECT_RUN_NUMBER"
  tenninety_clone_seed "$SEED" "$sandbox" || return 1
  out="$(tenninety_run_project "$sandbox" "$NUGET_CACHE_VOLUME" "$project" 2>&1)"
  rc=$?
  printf '%s\n' "$out"
  printf '%s\n' "$out" >> "$FAIL_LOG"
  if [ "$rc" -ne 0 ]; then
    errors="$(printf '%s\n' "$out" | grep -c 'error ' || true)"
    if [ "$errors" -gt "$THRESHOLD_ERRORS" ]; then
      echo "Cascade threshold exceeded ($errors errors > $THRESHOLD_ERRORS)."
      echo "Escalate deliberately when ready: dev.sh escalate $FEATURE $FAIL_LOG"
    fi
    return 1
  fi
  if printf '%s\n' "$out" | grep -qE 'No test (is available|matches)|Total tests:[[:space:]]*0'; then
    if [ "$mode" = "optional-empty" ]; then
      echo "EMPTY TIER (allowed until its first *Tests.cs exists): $project"
      return 0
    fi
    echo "EMPTY TEST GATE: $project contains test source but discovered no tests."
    return 1
  fi
}

: > "$FAIL_LOG"

[ "${#CONTRACTS_PROJS[@]}" -gt 0 ] || {
  echo "MISSING TEST PROJECT: no tests/*.Contracts/*.Contracts.csproj found."; exit 1; }
[ "${#GOLDEN_PROJS[@]}" -gt 0 ] || {
  echo "MISSING TEST PROJECT: no tests/*.Golden/*.Golden.csproj found."; exit 1; }
[ "${#UNIT_PROJS[@]}" -gt 0 ] || {
  echo "MISSING TEST PROJECT: no tests/*.Unit/*.Unit.csproj found."; exit 1; }

rc=0
for p in "${CONTRACTS_PROJS[@]}"; do run_project "$p" required || rc=1; done
for p in "${GOLDEN_PROJS[@]}"; do
  mode="$(project_mode "$p")" || { rc=1; continue; }
  run_project "$p" "$mode" || rc=1
done
for p in "${UNIT_PROJS[@]}"; do
  mode="$(project_mode "$p")" || { rc=1; continue; }
  run_project "$p" "$mode" || rc=1
done

FINAL_HASH="$(tenninety_workspace_hash)" || exit 98
if [ "$ORIGINAL_HASH" != "$FINAL_HASH" ]; then
  echo "WORKSPACE INTEGRITY FAILURE: the host workspace changed during tests." >&2
  exit 97
fi
exit "$rc"
