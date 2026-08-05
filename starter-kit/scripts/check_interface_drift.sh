#!/bin/bash
# Usage: check_interface_drift.sh <module-id> [baseline-ref]
# Compares a module's declared public surface against the active implementation
# or repair baseline. If it changed, every queued transitive dependent declared
# by Module ID in architecture.md is marked interface-changed.
set -uo pipefail

MODULE="${1:?Usage: check_interface_drift.sh <module-id> [baseline-ref]}"
BASE_REF="${2:-module-start-$MODULE}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="${WORKSPACE:-$(dirname "$SCRIPT_DIR")}"
SPEC="$WORKSPACE/.agent/rules/architecture.md"

if ! git -C "$WORKSPACE" rev-parse -q --verify "$BASE_REF^{commit}" >/dev/null; then
  echo "ERROR: interface-drift baseline '$BASE_REF' does not exist." >&2
  exit 1
fi
[ -f "$SPEC" ] || { echo "ERROR: architecture spec not found: $SPEC" >&2; exit 1; }
[ -f "$WORKSPACE/REVIEW_QUEUE.md" ] || {
  echo "ERROR: review queue not found: $WORKSPACE/REVIEW_QUEUE.md" >&2
  exit 1
}

# Capture the checker status explicitly. The exit code of a process substitution
# is not propagated by mapfile, so using `mapfile < <(checker)` would fail open.
checker_output="$(mktemp "${TMPDIR:-/tmp}/tenninety-signatures.XXXXXX")" || exit 1
checker_errors="$(mktemp "${TMPDIR:-/tmp}/tenninety-signature-errors.XXXXXX")" || {
  rm -f -- "$checker_output"
  exit 1
}
queue_temp=""
cleanup() { rm -f -- "$checker_output" "$checker_errors" ${queue_temp:+"$queue_temp"}; }
trap cleanup EXIT
if ! (cd "$WORKSPACE" && scripts/check_signatures.sh \
       --since "$BASE_REF" --names-only >"$checker_output" 2>"$checker_errors"); then
  echo "ERROR: signature checker failed:" >&2
  sed 's/^/  /' "$checker_errors" >&2
  sed 's/^/  /' "$checker_output" >&2
  exit 1
fi

CHANGED_SYMBOLS=()
while IFS= read -r line; do
  [ -n "$line" ] || continue
  case "$line" in
    API$'\t'*) CHANGED_SYMBOLS+=("${line#*$'\t'}") ;;
    *)
      echo "ERROR: signature checker produced unexpected stdout: $line" >&2
      exit 1 ;;
  esac
done < "$checker_output"
if [ "${#CHANGED_SYMBOLS[@]}" -eq 0 ]; then
  echo "No public-API changes since $BASE_REF."
  exit 0
fi

echo "Public-API changes detected in module $MODULE:"
printf '  %s\n' "${CHANGED_SYMBOLS[@]}"
echo ""

if git -C "$WORKSPACE" diff --name-only "$BASE_REF" -- . \
     ':(exclude)REVIEW_QUEUE.md' ':(exclude)review-feedback/**' \
     | grep -qx '.agent/rules/architecture.md'; then
  : # spec updated alongside code
else
  echo "ERROR: public API changed but .agent/rules/architecture.md was not updated." >&2
  echo "The interface-change policy requires the specification in the same diff." >&2
  exit 1
fi

manifest_has_module() {
  local wanted="$1"
  awk -v id="$wanted" '
    /\*\*Module ID:\*\*[[:space:]]*`/ {
      line=$0
      sub(/^.*\*\*Module ID:\*\*[[:space:]]*`/, "", line)
      sub(/`.*$/, "", line)
      if (line == id) found=1
    }
    END { exit(found ? 0 : 1) }
  ' "$SPEC"
}

manifest_direct_dependents() {
  local dependency="$1"
  awk -v target="$dependency" '
    /\*\*Module ID:\*\*[[:space:]]*`/ {
      line=$0
      sub(/^.*\*\*Module ID:\*\*[[:space:]]*`/, "", line)
      sub(/`.*$/, "", line)
      current=line
      next
    }
    current != "" && /\*\*Depends on:\*\*/ {
      rest=$0
      while (match(rest, /`[^`]+`/)) {
        dep=substr(rest, RSTART + 1, RLENGTH - 2)
        if (dep == target) {
          print current
          next
        }
        rest=substr(rest, RSTART + RLENGTH)
      }
    }
  ' "$SPEC" | sort -u
}

manifest_module_ids() {
  awk '
    /\*\*Module ID:\*\*[[:space:]]*`/ {
      line=$0
      sub(/^.*\*\*Module ID:\*\*[[:space:]]*`/, "", line)
      sub(/`.*$/, "", line)
      print line
    }
  ' "$SPEC"
}

manifest_dependency_edges() {
  awk '
    /\*\*Module ID:\*\*[[:space:]]*`/ {
      line=$0
      sub(/^.*\*\*Module ID:\*\*[[:space:]]*`/, "", line)
      sub(/`.*$/, "", line)
      current=line
      next
    }
    current != "" && /\*\*Depends on:\*\*/ {
      rest=$0
      while (match(rest, /`[^`]+`/)) {
        dep=substr(rest, RSTART + 1, RLENGTH - 2)
        if (tolower(dep) != "none") print current "\t" dep
        rest=substr(rest, RSTART + RLENGTH)
      }
    }
  ' "$SPEC"
}

module_text="$(manifest_module_ids)" || {
  echo "ERROR: could not parse Module IDs from architecture.md." >&2
  exit 1
}
duplicate_modules="$(printf '%s\n' "$module_text" | sed '/^$/d' | sort | uniq -d)"
if [ -n "$duplicate_modules" ]; then
  echo "ERROR: duplicate Module IDs in architecture.md:" >&2
  printf '%s\n' "$duplicate_modules" | sed 's/^/  - /' >&2
  exit 1
fi
edge_text="$(manifest_dependency_edges)" || {
  echo "ERROR: could not parse module dependencies from architecture.md." >&2
  exit 1
}
while IFS=$'\t' read -r owner dependency; do
  [ -n "$owner" ] || continue
  if ! printf '%s\n' "$module_text" | grep -qxF "$dependency"; then
    echo "ERROR: module '$owner' depends on unknown Module ID '$dependency'." >&2
    exit 1
  fi
done <<EOF
$edge_text
EOF

manifest_has_module "$MODULE" || {
  echo "ERROR: Module ID '$MODULE' is missing from architecture.md." >&2
  exit 1
}

# Traverse the declared dependency graph instead of guessing a module ID from
# a consumer filename. Modules may span files and their IDs need not resemble
# any filename, so filename conversion cannot be authoritative.
declare -A seen=(["$MODULE"]=1)
queue=("$MODULE")
dependents=()
index=0
while [ "$index" -lt "${#queue[@]}" ]; do
  current="${queue[$index]}"
  index=$((index + 1))
  direct=()
  direct_text="$(manifest_direct_dependents "$current")" || {
    echo "ERROR: could not traverse dependencies for '$current'." >&2
    exit 1
  }
  if [ -n "$direct_text" ]; then mapfile -t direct <<< "$direct_text"; fi
  for dependent in "${direct[@]}"; do
    [ -n "$dependent" ] || continue
    if [ -z "${seen[$dependent]+x}" ]; then
      seen["$dependent"]=1
      queue+=("$dependent")
      dependents+=("$dependent")
    fi
  done
done

if [ "${#dependents[@]}" -eq 0 ]; then
  echo "No dependent modules are declared for '$MODULE'."
  exit 0
fi

echo "Declared downstream modules:"
queue_targets=()
for dependent in "${dependents[@]}"; do
  if grep -q "^| $dependent |" "$WORKSPACE/REVIEW_QUEUE.md" 2>/dev/null; then
    row_count="$(grep -c "^| $dependent |" "$WORKSPACE/REVIEW_QUEUE.md")"
    if [ "$row_count" -ne 1 ]; then
      echo "ERROR: REVIEW_QUEUE.md contains $row_count rows for '$dependent'." >&2
      exit 1
    fi
    queue_targets+=("$dependent")
  else
    echo "  $dependent is not queued yet; it will build against the new contract"
  fi
done

if [ "${#queue_targets[@]}" -gt 0 ]; then
  queue_temp="$(mktemp "$WORKSPACE/.review-queue.XXXXXX")" || exit 1
  cp -p -- "$WORKSPACE/REVIEW_QUEUE.md" "$queue_temp" || exit 1
  for dependent in "${queue_targets[@]}"; do
    if ! sed -i "s/| $dependent | [^|]* |/| $dependent | interface-changed |/" "$queue_temp" \
        || ! grep -q "^| $dependent | interface-changed |" "$queue_temp"; then
      echo "ERROR: could not stage interface propagation for '$dependent'." >&2
      exit 1
    fi
  done
  mv -- "$queue_temp" "$WORKSPACE/REVIEW_QUEUE.md" || {
    echo "ERROR: could not atomically update REVIEW_QUEUE.md." >&2
    exit 1
  }
  queue_temp=""
  for dependent in "${queue_targets[@]}"; do
    echo "  Marked $dependent as interface-changed"
  done
fi
