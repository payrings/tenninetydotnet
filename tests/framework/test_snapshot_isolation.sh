#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TEMP="$(mktemp -d)"
trap 'find "$TEMP" -depth -delete 2>/dev/null || true' EXIT

WORKSPACE="$TEMP/workspace"
mkdir -p "$WORKSPACE/src/config" "$WORKSPACE/tests/One/bin" "$WORKSPACE/tests/Two/bin"
cp "$ROOT/starter-kit/scripts/runtime.sh" "$WORKSPACE/runtime.sh"
cp "$ROOT/starter-kit/scripts/test_sandbox.sh" "$WORKSPACE/test_sandbox.sh"
printf 'bin/\nobj/\n.env\n*.user\n' > "$WORKSPACE/.gitignore"
printf 'source\n' > "$WORKSPACE/src/Program.cs"
printf 'secret\n' > "$WORKSPACE/.env"
printf 'nested secret\n' > "$WORKSPACE/src/config/.env.production"
printf 'artifact one\n' > "$WORKSPACE/tests/One/bin/One.dll"
printf 'artifact two\n' > "$WORKSPACE/tests/Two/bin/Two.dll"

git -C "$WORKSPACE" init -q
git -C "$WORKSPACE" config user.email framework@example.invalid
git -C "$WORKSPACE" config user.name framework-tests
git -C "$WORKSPACE" add .
git -C "$WORKSPACE" commit -qm baseline

cd "$WORKSPACE"
source ./runtime.sh
source ./test_sandbox.sh
TENNINETY_STATE_HOME="$TEMP/state"
tenninety_init_runtime "$WORKSPACE"

SEED="$TEMP/seed"
ONE="$TEMP/one"
TWO="$TEMP/two"
tenninety_create_snapshot "$SEED"
tenninety_clone_seed "$SEED" "$ONE"
tenninety_clone_seed "$SEED" "$TWO"

[ ! -e "$SEED/.env" ]
[ ! -e "$SEED/src/config/.env.production" ]
[ ! -e "$SEED/tests/One/bin/One.dll" ]
printf 'poisoned\n' > "$ONE/src/Program.cs"
grep -qx source "$TWO/src/Program.cs"

printf '<Project />\n' > src/Demo.csproj.user
if tenninety_reject_ignored_build_controls 2>/dev/null; then
  echo "ignored build-control file was accepted" >&2
  exit 1
fi
