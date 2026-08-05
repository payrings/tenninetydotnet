#!/bin/bash
# Usage: ./queue_for_review.sh <module-id>
set -uo pipefail

MODULE="${1:?Usage: ./queue_for_review.sh <module-id>}"
[[ "$MODULE" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || {
  echo "ERROR: invalid Module ID '$MODULE'." >&2
  exit 1
}
[ -f REVIEW_QUEUE.md ] || {
  echo "ERROR: REVIEW_QUEUE.md not found." >&2
  exit 1
}

row_count="$(grep -c "^| $MODULE |" REVIEW_QUEUE.md || true)"
if [ "$row_count" -gt 1 ]; then
  echo "ERROR: REVIEW_QUEUE.md contains duplicate rows for '$MODULE'." >&2
  exit 1
fi

if [ "$row_count" -eq 1 ]; then
  STATUS="$(awk -F'|' -v m=" $MODULE " \
    '$2==m {gsub(/^ +| +$/, "", $3); print $3; exit}' REVIEW_QUEUE.md)"
  case "$STATUS" in
    needs-fixes|interface-changed) : ;;
    ready-for-review)
      echo "ERROR: $MODULE is already ready for review." >&2
      exit 1 ;;
    approved)
      echo "ERROR: $MODULE is approved and cannot be reopened by queue." >&2
      exit 1 ;;
    *)
      echo "ERROR: $MODULE has unknown queue status '$STATUS'." >&2
      exit 1 ;;
  esac
fi

temporary="$(mktemp .review-queue.XXXXXX)" || exit 1
cleanup() { [ -z "$temporary" ] || rm -f -- "$temporary"; }
trap cleanup EXIT
cp -p -- REVIEW_QUEUE.md "$temporary" || exit 1
if [ "$row_count" -eq 1 ]; then
  sed -i "s/| $MODULE | [^|]* |/| $MODULE | ready-for-review |/" "$temporary" || exit 1
else
  printf '| %s | ready-for-review | 0 |\n' "$MODULE" >> "$temporary" || exit 1
fi

if [ "$(grep -c "^| $MODULE | ready-for-review |" "$temporary" || true)" -ne 1 ]; then
  echo "ERROR: could not construct one ready-for-review row for '$MODULE'." >&2
  exit 1
fi
mv -- "$temporary" REVIEW_QUEUE.md || exit 1
temporary=""
