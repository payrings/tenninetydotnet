#!/bin/bash
# Usage: ./scripts/run_tests_with_cascade_check.sh <module-id>
# HOST-SIDE ONLY. Runs the fast tier (Contracts + Golden + Unit) in test-runner.
set -uo pipefail

FEATURE="${1:?Usage: run_tests_with_cascade_check.sh <module-id>}"
[[ "$FEATURE" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || {
  echo "ERROR: invalid Module ID '$FEATURE'." >&2; exit 1; }
THRESHOLD_ERRORS="${DOTNET_ERROR_THRESHOLD:-10}"

mkdir -p ".dev-runtime/$FEATURE"
FAIL_LOG=".dev-runtime/$FEATURE/latest-test.log"

LOCK_HASH=$(find . \
  \( -path './.git' -o -path './.dev-runtime' -o -path '*/bin' -o -path '*/obj' \) -prune -o \
  -name packages.lock.json -type f -print0 \
  | sort -z | xargs -0 -r cat 2>/dev/null | sha256sum | cut -d' ' -f1 | head -c 12)
WORKSPACE_ID="$(printf '%s' "$(pwd -P)" | sha256sum | cut -d' ' -f1 | head -c 12)"
NUGET_CACHE_VOLUME="nuget-cache-${WORKSPACE_ID}-${LOCK_HASH:-no-lockfiles}"

# Hash every workspace file except Git metadata, orchestrator runtime state and
# normal compiler output. A successful restore/build/test must leave this hash
# unchanged; path-only scope checks are not sufficient because an allowed source
# file could otherwise be rewritten after its compiled assembly passed.
workspace_hash() {
  find . \
    \( -path './.git' -o -path './.dev-runtime' -o -path '*/bin' -o -path '*/obj' \) -prune -o \
    \( -type f -o -type l \) -print0 \
    | sort -z \
    | while IFS= read -r -d '' path; do
        printf '%s\0' "$path"
        stat -c '%a %F' -- "$path" 2>/dev/null || return 1
        if [ -L "$path" ]; then readlink -- "$path"; else sha256sum -- "$path"; fi
      done \
    | sha256sum \
    | cut -d' ' -f1
}

# Protect all test definitions, fixtures, Git state, review metadata and MSBuild
# control inputs inside test containers. Directories remain writable so bin/obj
# can be produced, while each authoritative file is an immutable mount point.
PROTECTED_MOUNTS=()
declare -A PROTECTED_SEEN=()
add_ro_file() {
  local path="$1"
  [ -f "$path" ] || return 0
  local relative="${path#./}"
  [ -n "${PROTECTED_SEEN[$relative]+x}" ] && return 0
  PROTECTED_SEEN["$relative"]=1
  PROTECTED_MOUNTS+=(-v "$PWD/$relative:/workspace/$relative:ro")
}

[ -d .git ] && PROTECTED_MOUNTS+=(-v "$PWD/.git:/workspace/.git:ro")
for path in Directory.Packages.props Directory.Build.props Directory.Build.targets \
            global.json REVIEW_QUEUE.md BROADCAST.md \
            .agent/rules/architecture.md .agent/rules/architecture.original.md; do
  add_ro_file "$path"
done
while IFS= read -r -d '' path; do add_ro_file "$path"; done < <(
  find tests \
    \( -path '*/bin' -o -path '*/obj' \) -prune -o \
    -type f -print0 2>/dev/null
)
while IFS= read -r -d '' path; do add_ro_file "$path"; done < <(
  find . \
    \( -path './.git' -o -path './.dev-runtime' -o -path '*/bin' -o -path '*/obj' \) -prune -o \
    -type f \( -name '*.csproj' -o -name '*.fsproj' -o -name '*.vbproj' \
              -o -name '*.sln' -o -name '*.slnx' -o -name '*.props' \
              -o -name '*.targets' -o -name 'NuGet.Config' \) -print0
)

# The framework promises that test containers receive no credentials. Mask a
# project-local .env if one exists; ignored secrets must not become readable to
# repository-controlled build logic.
if [ -f .env ]; then
  EMPTY_ENV=".dev-runtime/$FEATURE/empty.env"
  : > "$EMPTY_ENV"
  chmod 444 "$EMPTY_ENV"
  PROTECTED_MOUNTS+=(-v "$PWD/$EMPTY_ENV:/workspace/.env:ro")
fi

run_restore() {
  # Networked restore evaluates only trusted, read-only scaffold/MSBuild files.
  # Implementation agents cannot change those files, and the workspace hash
  # below rejects any mutation left by the restore process.
  docker run --rm --pull=never \
    -v "$PWD":/workspace \
    -v "$NUGET_CACHE_VOLUME":/home/agent/.nuget/packages \
    "${PROTECTED_MOUNTS[@]}" \
    --entrypoint bash \
    test-runner -lc "$1"
}

run_isolated() {
  # Build/test execute repository code without network. The restored package
  # cache is read-only, preventing a build task or test from poisoning it for a
  # later module or project.
  docker run --rm --pull=never \
    --network=none \
    -v "$PWD":/workspace \
    -v "$NUGET_CACHE_VOLUME":/home/agent/.nuget/packages:ro \
    "${PROTECTED_MOUNTS[@]}" \
    --entrypoint bash \
    test-runner -lc "$1"
}

run_guarded() {
  # run_guarded <restore|isolated> <command>
  local mode="$1" command="$2" before after output rc
  before="$(workspace_hash)" || {
    echo "WORKSPACE INTEGRITY FAILURE: could not hash the workspace before $mode." >&2
    return 98
  }
  if [ "$mode" = "restore" ]; then
    output="$(run_restore "$command" 2>&1)"; rc=$?
  else
    output="$(run_isolated "$command" 2>&1)"; rc=$?
  fi
  after="$(workspace_hash)" || {
    printf '%s\n' "$output"
    echo "WORKSPACE INTEGRITY FAILURE: could not hash the workspace after $mode." >&2
    return 98
  }
  printf '%s\n' "$output"
  if [ "$before" != "$after" ]; then
    echo "WORKSPACE INTEGRITY FAILURE: $mode phase changed protected/project content." >&2
    echo "Only bin/, obj/ and .dev-runtime/ may change during a test-container run." >&2
    git status --short >&2 2>/dev/null || true
    return 97
  fi
  return "$rc"
}

# Restore with network, then build and execute tests without network.
restore_output="$(run_guarded restore "dotnet restore --locked-mode 2>&1")"
restore_rc=$?
if [ "$restore_rc" -ne 0 ]; then
  printf '%s\n' "$restore_output" > "$FAIL_LOG"
  echo "$restore_output"
  echo ""
  echo "Restore failed (exit $restore_rc). Log saved to $FAIL_LOG."
  exit 1
fi

build_output="$(run_guarded isolated "dotnet build --no-restore -warnaserror -clp:NoSummary 2>&1")"
build_rc=$?
printf '%s\n' "$build_output" > "$FAIL_LOG"
build_errors=$(printf '%s\n' "$build_output" | grep -c "error ")

if [ "$build_rc" -ne 0 ]; then
  echo "$build_output"
  echo ""
  if [ "$build_errors" -gt "$THRESHOLD_ERRORS" ]; then
    echo "Cascade threshold exceeded ($build_errors errors > $THRESHOLD_ERRORS)."
    echo "Build error log saved to $FAIL_LOG."
    echo "Escalate deliberately when ready: dev.sh escalate $FEATURE $FAIL_LOG"
  else
    echo "Build failed (exit $build_rc). Log saved to $FAIL_LOG."
  fi
  exit 1
fi

run_project() {
  local proj="$1" mode="${2:-required}" out rc
  [ -f "$proj" ] || { echo "MISSING TEST PROJECT: $proj"; return 1; }
  out="$(run_guarded isolated "dotnet test '$proj' --no-build --no-restore -v normal 2>&1")"
  rc=$?
  printf '%s\n' "$out"
  printf '%s\n' "$out" >> "$FAIL_LOG"
  [ "$rc" -eq 0 ] || return 1

  if printf '%s\n' "$out" | grep -qE "No test (is available|matches)|Total tests:[[:space:]]*0"; then
    if [ "$mode" = "optional-empty" ]; then
      echo "EMPTY TIER (allowed until its first *Tests.cs exists): $proj"
      return 0
    fi
    echo "EMPTY TEST GATE: $proj contains test source but discovered no tests."
    return 1
  fi
  return 0
}

collect() {
  local pattern="$1"; local -n _out="$2"
  _out=()
  local p had_nullglob
  shopt -q nullglob && had_nullglob=1 || had_nullglob=0
  shopt -s nullglob
  for p in $pattern; do _out+=("$p"); done
  [ "$had_nullglob" = "1" ] || shopt -u nullglob
  if [ "${#_out[@]}" -gt 1 ]; then
    mapfile -t _out < <(printf '%s\n' "${_out[@]}" | sort)
  fi
}

project_mode() {
  local project="$1" directory
  directory="$(dirname "$project")"
  if find "$directory" \( -path '*/bin' -o -path '*/obj' \) -prune -o \
       -type f -name '*Tests.cs' -print -quit | grep -q .; then
    echo required
  else
    echo optional-empty
  fi
}

collect "tests/*.Contracts/*.Contracts.csproj" CONTRACTS_PROJS
collect "tests/*.Golden/*.Golden.csproj"       GOLDEN_PROJS
collect "tests/*.Unit/*.Unit.csproj"           UNIT_PROJS
: > "$FAIL_LOG"

if [ "${#CONTRACTS_PROJS[@]}" -eq 0 ]; then
  echo "MISSING TEST PROJECT: no tests/*.Contracts/*.Contracts.csproj found."
  exit 1
fi
if [ "${#GOLDEN_PROJS[@]}" -eq 0 ]; then
  echo "MISSING TEST PROJECT: no tests/*.Golden/*.Golden.csproj found."
  exit 1
fi
if [ "${#UNIT_PROJS[@]}" -eq 0 ]; then
  echo "MISSING TEST PROJECT: no tests/*.Unit/*.Unit.csproj found."
  exit 1
fi

rc=0
for p in "${CONTRACTS_PROJS[@]}"; do run_project "$p" required || rc=1; done
for p in "${GOLDEN_PROJS[@]}"; do run_project "$p" "$(project_mode "$p")" || rc=1; done
for p in "${UNIT_PROJS[@]}"; do run_project "$p" "$(project_mode "$p")" || rc=1; done
exit "$rc"
