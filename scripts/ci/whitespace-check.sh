#!/usr/bin/env bash
# Whitespace check for the changes under test — never a rolling history scan.
#
# `git log --check` walks OLD commits and fails on whitespace errors that already exist in
# merged history (and cannot be fixed without rewriting history). This script instead checks
# exactly the commits/diff identified by optional push or pull-request environment values,
# plus the index and working tree:
#
#   - pull_request events: the caller provides GITHUB_BASE_REF; the script fetches that
#     base branch, computes the merge base, and checks `git diff --check <merge-base> HEAD`
#     (every commit the PR introduces);
#   - push events: the caller provides GITHUB_PUSH_BEFORE. When that
#     commit exists in the checked-out history, the pushed diff `before..HEAD` is checked.
#     For a NEW branch (event.before is the all-zero SHA) there is no defined base on the
#     remote, so only the tip commit (`git show --check HEAD`) is checked in addition to the
#     working tree — a documented limitation, never a rolling-history scan;
#   - local runs (no GitHub environment): the tip commit, staged changes and working tree are
#     checked, which is the reproducible local equivalent of what CI runs on the pushed tip.
#
# Exits nonzero on the first whitespace error class found; prints every finding.

set -u

event="${GITHUB_EVENT_NAME:-}"
base_ref="${GITHUB_BASE_REF:-}"
push_before="${GITHUB_PUSH_BEFORE:-}"

status=0

check_diff() {
    echo "==> git diff --check $*"
    git diff --check "$@" || status=1
}

case "$event" in
    pull_request)
        if [ -z "$base_ref" ]; then
            echo "ERROR: pull_request event without GITHUB_BASE_REF" >&2
            exit 2
        fi
        git fetch --no-tags --quiet origin "$base_ref" || exit 2
        base="$(git merge-base "origin/$base_ref" HEAD)" || exit 2
        echo "==> checking pull-request diff against merge base $base"
        check_diff "$base" HEAD
        ;;
    push)
        if [ -n "$push_before" ] && git cat-file -e "$push_before" 2>/dev/null; then
            echo "==> checking pushed diff $push_before..HEAD"
            check_diff "$push_before" HEAD
        else
            echo "==> new branch (no resolvable base): checking the tip commit"
            echo "==> git show --check --oneline HEAD"
            git show --check --oneline HEAD || status=1
        fi
        ;;
    *)
        echo "==> local equivalent: checking the tip commit and the working tree"
        echo "==> git show --check --oneline HEAD"
        git show --check --oneline HEAD || status=1
        ;;
esac

# The index and working tree are always checked (both are locally reproducible).
echo "==> git diff --cached --check (staged changes)"
git diff --cached --check || status=1

echo "==> git diff --check (working tree)"
git diff --check || status=1

exit "$status"
