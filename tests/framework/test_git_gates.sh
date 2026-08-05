#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
FIXTURE="$(mktemp -d "${TMPDIR:-/tmp}/tenninety-git-gates.XXXXXX")"
STATE="${FIXTURE}-state"
trap 'find "$FIXTURE" "$STATE" -depth -delete 2>/dev/null || true' EXIT

git -C "$FIXTURE" init -q
git -C "$FIXTURE" config user.name "Framework Test"
git -C "$FIXTURE" config user.email "framework-test@example.invalid"
mkdir -p "$FIXTURE/src" "$FIXTURE/scripts" "$FIXTURE/.agent/rules"
cp "$ROOT/starter-kit/scripts/dev.sh" "$ROOT/starter-kit/scripts/runtime.sh" \
  "$ROOT/starter-kit/scripts/check_no_raw_sql.sh" "$FIXTURE/scripts/"
chmod +x "$FIXTURE/scripts/"*.sh

printf '%s\n' 'public class SqlDemo { void Run() { new SqlCommand("select 1"); } }' \
  > "$FIXTURE/src/SqlDemo.cs"
git -C "$FIXTURE" add .
printf '%s\n' 'public class SqlDemo { }' > "$FIXTURE/src/SqlDemo.cs"
if (cd "$FIXTURE" && ./scripts/check_no_raw_sql.sh >"$FIXTURE/raw.out" 2>&1); then
  echo "staged raw SQL incorrectly passed because the working file was clean" >&2
  exit 1
fi
grep -q 'BLOCK: staged raw SQL' "$FIXTURE/raw.out"

git -C "$FIXTURE" add src/SqlDemo.cs
cat > "$FIXTURE/.agent/rules/architecture.md" <<'EOF'
## Demo
**Module ID:** `demo`
### Implementation files
- `src/Allowed.cs` – implementation
### Shared integration files
- `None`
### Protected/generated test artefacts
- `None`
EOF
printf '%s\n' 'public class Outside { }' > "$FIXTURE/src/Outside.cs"
git -C "$FIXTURE" add .
git -C "$FIXTURE" commit -qm baseline
git -C "$FIXTURE" tag module-start-demo
git -C "$FIXTURE" mv src/Outside.cs src/Allowed.cs

if (cd "$FIXTURE" && TENNINETY_STATE_HOME="$STATE" DEV_SKIP_PREFLIGHT=1 \
    ./scripts/dev.sh review demo >"$FIXTURE/scope.out" 2>&1); then
  echo "rename from an out-of-scope source incorrectly passed scope" >&2
  exit 1
fi
grep -q 'src/Outside.cs' "$FIXTURE/scope.out"
