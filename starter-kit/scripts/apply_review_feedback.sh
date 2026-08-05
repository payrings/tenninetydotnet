#!/bin/bash
# Usage: ./apply_review_feedback.sh <module-id>
# HOST-SIDE ONLY. Delegates the actual Write->Review->fast-Test loop to
# 'dev.sh iterate' (which already enforces the VERDICT gate and feeds captured
# logs into the next attempt), then runs the integration gate, commits the
# repair, and returns the module to the queue in a separate metadata commit.
set -uo pipefail

MODULE="${1:?Usage: apply_review_feedback.sh <module-id>}"
[[ "$MODULE" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || {
  echo "ERROR: invalid Module ID '$MODULE'." >&2; exit 1; }
FEEDBACK_FILE="review-feedback/$MODULE.md"

[ -f "$FEEDBACK_FILE" ] || { echo "Feedback file not found: $FEEDBACK_FILE"; exit 1; }
STATUS=$(awk -F'|' -v m=" $MODULE " '$2==m {gsub(/^ +| +$/, "", $3); print $3; exit}' REVIEW_QUEUE.md)
[ "$STATUS" = "needs-fixes" ] || {
  echo "ERROR: $MODULE is not in needs-fixes state (status: ${STATUS:-missing})." >&2
  exit 1
}
FEEDBACK=$(cat "$FEEDBACK_FILE")

if ./scripts/dev.sh iterate "$MODULE" \
  "Fix module $MODULE according to this human feedback: $FEEDBACK"; then

  ./scripts/dev.sh finalise "$MODULE" || exit 1
  ./scripts/dev.sh commit "$MODULE" || exit 1
  ./scripts/dev.sh queue "$MODULE" || exit 1
  echo "$MODULE passed all gates and was committed; returned to review queue."
else
  echo "Three bounded fix attempts failed for $MODULE."
  echo "Inspect .dev-runtime/$MODULE/latest-test.log, then revise the spec,"
  echo "escalate deliberately (first call: no --override), or reset."
  exit 1
fi
