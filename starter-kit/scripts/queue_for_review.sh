#!/bin/bash
# Usage: ./queue_for_review.sh <module-id>
set -uo pipefail
MODULE="${1:?Usage: ./queue_for_review.sh <module-id>}"
[[ "$MODULE" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || {
  echo "ERROR: invalid Module ID '$MODULE'." >&2; exit 1; }
[ -f REVIEW_QUEUE.md ] || { echo "ERROR: REVIEW_QUEUE.md not found." >&2; exit 1; }
if grep -q "^| $MODULE |" REVIEW_QUEUE.md; then
  sed -i "s/| $MODULE | [^|]* |/| $MODULE | ready-for-review |/" REVIEW_QUEUE.md
else
  echo "| $MODULE | ready-for-review | 0 |" >> REVIEW_QUEUE.md
fi
