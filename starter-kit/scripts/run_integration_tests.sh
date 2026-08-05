#!/bin/bash
# HOST-SIDE ONLY. Slow tier, run once per completed module via dev.sh finalise.
set -uo pipefail

LOCK_HASH=$(find . \
  \( -path './.git' -o -path './.dev-runtime' -o -path '*/bin' -o -path '*/obj' \) -prune -o \
  -name packages.lock.json -type f -print0 \
  | sort -z | xargs -0 -r cat 2>/dev/null | sha256sum | cut -d' ' -f1 | head -c 12)
WORKSPACE_ID="$(printf '%s' "$(pwd -P)" | sha256sum | cut -d' ' -f1 | head -c 12)"
NUGET_CACHE_VOLUME="nuget-cache-${WORKSPACE_ID}-${LOCK_HASH:-no-lockfiles}"

INTEGRATION_PROJS=()
while IFS= read -r p; do INTEGRATION_PROJS+=("$p"); done < <(
  find tests -mindepth 2 -maxdepth 2 -type f -name '*.Integration.csproj' -print 2>/dev/null | sort
)
if [ "${#INTEGRATION_PROJS[@]}" -eq 0 ]; then
  echo "No integration test project found – skipping."
  exit 0
fi

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
  find tests \( -path '*/bin' -o -path '*/obj' \) -prune -o -type f -print0 2>/dev/null
)
while IFS= read -r -d '' path; do add_ro_file "$path"; done < <(
  find . \
    \( -path './.git' -o -path './.dev-runtime' -o -path '*/bin' -o -path '*/obj' \) -prune -o \
    -type f \( -name '*.csproj' -o -name '*.fsproj' -o -name '*.vbproj' \
              -o -name '*.sln' -o -name '*.slnx' -o -name '*.props' \
              -o -name '*.targets' -o -name 'NuGet.Config' \) -print0
)

mkdir -p .dev-runtime/integration
if [ -f .env ]; then
  EMPTY_ENV=".dev-runtime/integration/empty.env"
  : > "$EMPTY_ENV"
  chmod 444 "$EMPTY_ENV"
  PROTECTED_MOUNTS+=(-v "$PWD/$EMPTY_ENV:/workspace/.env:ro")
fi

run_restore() {
  docker run --rm --pull=never \
    -v "$PWD":/workspace \
    -v "$NUGET_CACHE_VOLUME":/home/agent/.nuget/packages \
    "${PROTECTED_MOUNTS[@]}" \
    --entrypoint bash \
    test-runner -lc "$1"
}

run_tests() {
  docker run --rm --pull=never \
    --network=none \
    -v "$PWD":/workspace \
    -v "$NUGET_CACHE_VOLUME":/home/agent/.nuget/packages:ro \
    "${PROTECTED_MOUNTS[@]}" \
    --entrypoint bash \
    test-runner -lc "$1"
}

run_guarded() {
  local mode="$1" command="$2" before after output rc
  before="$(workspace_hash)" || {
    echo "WORKSPACE INTEGRITY FAILURE: could not hash before integration $mode." >&2
    return 98
  }
  if [ "$mode" = restore ]; then
    output="$(run_restore "$command" 2>&1)"; rc=$?
  else
    output="$(run_tests "$command" 2>&1)"; rc=$?
  fi
  after="$(workspace_hash)" || {
    printf '%s\n' "$output"
    echo "WORKSPACE INTEGRITY FAILURE: could not hash after integration $mode." >&2
    return 98
  }
  printf '%s\n' "$output"
  if [ "$before" != "$after" ]; then
    echo "WORKSPACE INTEGRITY FAILURE: integration $mode phase changed project content." >&2
    echo "Only bin/, obj/ and .dev-runtime/ may change." >&2
    git status --short >&2 2>/dev/null || true
    return 97
  fi
  return "$rc"
}

run_guarded restore "dotnet restore --locked-mode" || exit 1

for project in "${INTEGRATION_PROJS[@]}"; do
  directory="$(dirname "$project")"
  if ! find "$directory" \( -path '*/bin' -o -path '*/obj' \) -prune -o \
       -type f -name '*Tests.cs' -print -quit | grep -q .; then
    echo "EMPTY TEST GATE: $project has no *Tests.cs source." >&2
    exit 1
  fi
done

run_guarded isolated "dotnet build --no-restore -warnaserror -clp:NoSummary" || exit 1
for project in "${INTEGRATION_PROJS[@]}"; do
  output="$(run_guarded isolated "dotnet test '$project' --no-build --no-restore -v normal 2>&1")"
  rc=$?
  printf '%s\n' "$output"
  [ "$rc" -eq 0 ] || exit 1
  if printf '%s\n' "$output" | grep -qE "No test (is available|matches)|Total tests:[[:space:]]*0"; then
    echo "EMPTY TEST GATE: $project discovered no tests." >&2
    exit 1
  fi
done
