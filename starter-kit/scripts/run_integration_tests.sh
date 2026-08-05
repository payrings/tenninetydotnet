#!/bin/bash
# HOST-SIDE ONLY. Slow tier, run once per completed module via dev.sh finalise.
# Every project runs in its own disposable workspace.
set -uo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="$(dirname "$SCRIPT_DIR")"
cd "$WORKSPACE" || exit 1
# shellcheck source=runtime.sh
source "$SCRIPT_DIR/runtime.sh"
# shellcheck source=test_sandbox.sh
source "$SCRIPT_DIR/test_sandbox.sh"
tenninety_init_runtime "$WORKSPACE" || exit 1
tenninety_reject_ignored_build_controls || exit 1

INTEGRATION_PROJS=()
project_list="$(mktemp "$TENNINETY_RUNTIME_DIR/integration-projects.XXXXXX")" || exit 1
if ! find tests -mindepth 2 -maxdepth 2 -type f -name '*.Integration.csproj' -print0 \
    > "$project_list"; then
  rm -f -- "$project_list"
  echo "ERROR: could not enumerate integration test projects." >&2
  exit 1
fi
sorted_projects="$(mktemp "$TENNINETY_RUNTIME_DIR/integration-projects-sorted.XXXXXX")" || exit 1
sort -z "$project_list" > "$sorted_projects" || exit 1
while IFS= read -r -d '' project; do INTEGRATION_PROJS+=("$project"); done < "$sorted_projects"
rm -f -- "$project_list" "$sorted_projects"
if [ "${#INTEGRATION_PROJS[@]}" -eq 0 ]; then
  echo "No integration test project found – skipping."
  exit 0
fi

project_number=0
for project in "${INTEGRATION_PROJS[@]}"; do
  directory="$(dirname "$project")"
  if ! find "$directory" \( -path '*/bin' -o -path '*/obj' \) -prune -o \
       -type f -name '*Tests.cs' -print -quit | grep -q .; then
    echo "EMPTY TEST GATE: $project has no *Tests.cs source." >&2
    exit 1
  fi
done

LOCK_HASH="$(tenninety_lock_hash)" || {
  echo "ERROR: could not hash NuGet lockfiles." >&2; exit 1;
}
WORKSPACE_ID="$(printf '%s' "$(pwd -P)" | sha256sum | cut -d' ' -f1 | head -c 12)"
NUGET_CACHE_VOLUME="nuget-cache-${WORKSPACE_ID}-${LOCK_HASH:-no-lockfiles}"

RUN_ROOT="$(mktemp -d "$TENNINETY_RUNTIME_DIR/integration.XXXXXX")" || exit 1
cleanup() { find "$RUN_ROOT" -depth -delete 2>/dev/null || true; }
trap cleanup EXIT INT TERM

ORIGINAL_HASH="$(tenninety_workspace_hash)" || exit 98
SEED="$RUN_ROOT/seed"
tenninety_create_snapshot "$SEED" || exit 1
tenninety_restore_seed "$SEED" "$NUGET_CACHE_VOLUME" || exit 1

for project in "${INTEGRATION_PROJS[@]}"; do
  project_number=$((project_number + 1))
  sandbox="$RUN_ROOT/project-$project_number"
  tenninety_clone_seed "$SEED" "$sandbox" || exit 1
  output="$(tenninety_run_project "$sandbox" "$NUGET_CACHE_VOLUME" "$project" 2>&1)"
  rc=$?
  printf '%s\n' "$output"
  [ "$rc" -eq 0 ] || exit 1
  if printf '%s\n' "$output" | grep -qE 'No test (is available|matches)|Total tests:[[:space:]]*0'; then
    echo "EMPTY TEST GATE: $project discovered no tests." >&2
    exit 1
  fi
done

FINAL_HASH="$(tenninety_workspace_hash)" || exit 98
if [ "$ORIGINAL_HASH" != "$FINAL_HASH" ]; then
  echo "WORKSPACE INTEGRITY FAILURE: the host workspace changed during integration tests." >&2
  exit 97
fi
