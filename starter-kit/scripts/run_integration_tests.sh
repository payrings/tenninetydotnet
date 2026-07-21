#!/bin/bash
# HOST-SIDE ONLY. Slow tier, run once per completed feature via 'dev.sh finalise'.
set -uo pipefail

LOCK_HASH=$(find . -name packages.lock.json -type f -print0 \
  | sort -z | xargs -0 cat 2>/dev/null | sha256sum | cut -d' ' -f1 | head -c 12)
NUGET_CACHE_VOLUME="nuget-cache-${LOCK_HASH:-no-lockfiles}"

# For .NET, integration tests typically use in-memory databases or
# Testcontainers rather than sibling database containers. Adjust to your needs.
# We build here (no --no-build): this script may run independently of the fast
# tier, so we cannot assume valid build outputs already exist.
# Run EVERY integration project, not just the first: 'head -1' silently ignored
# all but one project in a multi-project solution.
INTEGRATION_PROJS=()
while IFS= read -r p; do INTEGRATION_PROJS+=("$p"); done < <(ls tests/*.Integration/*.Integration.csproj 2>/dev/null | sort)
if [ "${#INTEGRATION_PROJS[@]}" -eq 0 ]; then
  echo "No integration test project found – skipping."
  exit 0
fi

TEST_CMDS=""
for p in "${INTEGRATION_PROJS[@]}"; do
  TEST_CMDS="$TEST_CMDS && dotnet test '$p' --no-build --no-restore -v normal"
done

# The test-runner image runs as the non-root 'agent' user (Phase 6).
# Two-phase execution: `dotnet restore` needs the network to fill the cache
# volume, but `dotnet build`/`dotnet test` run arbitrary Coder code and must
# not. Restore with network, then build + test with --network=none (offline,
# --no-restore against the now-populated cache volume).
#
# Integration tests that genuinely need network (e.g. Testcontainers spinning
# up a sibling database, or an out-of-process service) cannot run under
# --network=none. If that is your case, set INTEGRATION_ALLOW_NETWORK=1 to drop
# the isolation for the build/test phase. This widens the arbitrary-code
# surface, so keep it off unless a test provably requires it.
if [ "${INTEGRATION_ALLOW_NETWORK:-0}" = "1" ]; then
  NET_ARGS=()
else
  NET_ARGS=(--network=none)
fi

docker run --rm \
  -v "$PWD":/workspace \
  -v "$NUGET_CACHE_VOLUME":/home/agent/.nuget/packages \
  --entrypoint bash \
  test-runner -lc "dotnet restore --locked-mode" || exit 1

docker run --rm \
  "${NET_ARGS[@]}" \
  -v "$PWD":/workspace \
  -v "$NUGET_CACHE_VOLUME":/home/agent/.nuget/packages \
  --entrypoint bash \
  test-runner -lc "dotnet build --no-restore -warnaserror -clp:NoSummary$TEST_CMDS"
