#!/bin/bash
# Usage: ./apply_review_feedback.sh <module-id>
# HOST-SIDE ONLY. Delegates the actual Write->Review->fast-Test loop to
# 'dev.sh iterate' (which already enforces the VERDICT gate and feeds captured
# logs into the next attempt), then runs the integration gate and drift check.
set -uo pipefail

MODULE="${1:?Usage: apply_review_feedback.sh <module-id>}"
FEEDBACK_FILE="review-feedback/$MODULE.md"

[ -f "$FEEDBACK_FILE" ] || { echo "Feedback file not found: $FEEDBACK_FILE"; exit 1; }
FEEDBACK=$(cat "$FEEDBACK_FILE")

if ./scripts/dev.sh iterate "$MODULE" \
  "Fix module $MODULE according to this human feedback: $FEEDBACK"; then

  ./scripts/dev.sh finalise "$MODULE" || exit 1
  ./scripts/dev.sh commit "$MODULE" || exit 1

  rejections=$(grep "| $MODULE |" REVIEW_QUEUE.md | awk -F'|' '{print $4}' | tr -d ' ')
  rejections=${rejections:-1}
  sed -i "s/| $MODULE | [^|]* | [^|]* |/| $MODULE | ready-for-review | $rejections |/" \
    REVIEW_QUEUE.md

  echo "$MODULE passed all gates and was committed; returned to review queue."
else
  echo "Three bounded fix attempts failed for $MODULE."
  echo "Inspect .dev-runtime/$MODULE/latest-test.log, then revise the spec,"
  echo "escalate deliberately (first call: no --override), or reset."
  exit 1
fi