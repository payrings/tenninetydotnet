#!/bin/bash
# Usage: check_interface_drift.sh <module-id>
# Compares the module's whole public surface against its start tag and, if it
# changed, marks downstream consumers in REVIEW_QUEUE.md as interface-changed.
set -uo pipefail

MODULE="${1:?Usage: check_interface_drift.sh <module-id>}"
# Self-locate: the workspace is the parent of the scripts/ directory this file
# lives in, so each project's copy operates on its own workspace regardless of
# CWD or how it was invoked (PATH, ./scripts/dev.sh, absolute path). WORKSPACE
# in the environment still overrides.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="${WORKSPACE:-$(dirname "$SCRIPT_DIR")}"

# Compare against the module's baseline tag (the entire module diff), NOT the
# last commit – "last commit" is not necessarily this module's change set.
BASE_TAG="module-start-$MODULE"
if ! git -C "$WORKSPACE" rev-parse -q --verify "refs/tags/$BASE_TAG" >/dev/null; then
  echo "No baseline tag $BASE_TAG – skipping drift check."
  exit 0
fi

# --names-only emits bare changed symbol names, one per line (no signature
# text to parse).
mapfile -t CHANGED_SYMBOLS < <(
  cd "$WORKSPACE" && dotnet script scripts/check_signatures.csx -- \
    --since "$BASE_TAG" --names-only 2>/dev/null
)

if [ "${#CHANGED_SYMBOLS[@]}" -eq 0 ]; then
  echo "No public-API changes since $BASE_TAG."
  exit 0
fi

echo "Public-API changes detected in module $MODULE:"
printf '  %s\n' "${CHANGED_SYMBOLS[@]}"
echo ""

# Was .cline/rules/architecture.md also updated in the same module diff? Use grep -q in an
# if-statement – never `grep -c ... || echo 0`, which prints two lines (grep's
# "0" plus the echo) and then breaks the numeric test.
if git -C "$WORKSPACE" diff --name-only "$BASE_TAG" 2>/dev/null \
     | grep -qE '(^|/)architecture\.md$'; then
  : # spec updated alongside the code – good
else
  echo "WARNING: public API changed but .cline/rules/architecture.md was not updated."
  echo "This may be an interface-change policy violation."
  echo ""
fi

for symbol in "${CHANGED_SYMBOLS[@]}"; do
  [ -n "$symbol" ] || continue
  short_symbol="${symbol##*.}"   # last dotted segment
  echo "Checking consumers of: $short_symbol"

  while IFS= read -r consumer_file; do
    [ -z "$consumer_file" ] && continue
    # Convert filename to a candidate Module ID (src/[ProjectName]/SomeModule.cs -> some-module).
    # NOTE: only round-trips with cmd_write_contract's PascalCase conversion for
    # single-hump names. If your modules use acronyms, name them with a single
    # leading cap per word (HttpClient, not HTTPClient) to keep the two in sync.
    consumer_module=$(basename "$consumer_file" .cs \
      | sed -E 's/([a-z0-9])([A-Z])/\1-\2/g' \
      | tr '[:upper:]' '[:lower:]')

    if grep -q "| $consumer_module |" "$WORKSPACE/REVIEW_QUEUE.md" 2>/dev/null; then
      sed -i "s/| $consumer_module | [^|]* |/| $consumer_module | interface-changed |/" \
        "$WORKSPACE/REVIEW_QUEUE.md"
      echo "  Marked $consumer_module as 'interface-changed' in REVIEW_QUEUE.md"
    fi
  done < <(
    cd "$WORKSPACE" \
      && ./scripts/find_consumers.sh "$short_symbol" 2>/dev/null \
      | cut -d: -f1 \
      | grep -E '^src/.*\.cs$' \
      | sort -u
  )
done