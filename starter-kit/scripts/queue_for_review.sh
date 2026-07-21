#!/bin/bash
# Usage: ./queue_for_review.sh <module-id>
MODULE="$1"
if grep -q "| $MODULE |" REVIEW_QUEUE.md; then
  sed -i "s/| $MODULE | [^|]* |/| $MODULE | ready-for-review |/" REVIEW_QUEUE.md
else
  echo "| $MODULE | ready-for-review | 0 |" >> REVIEW_QUEUE.md
fi