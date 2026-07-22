#!/bin/bash
# Copyright 2026 G. Paganelli
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
# scripts/dev.sh – single entry point for the local coding loop (C# / .NET).
# Usage: dev.sh <subcommand> [args]

# Framework version this orchestrator ships with. Bump on every tagged release
# (keep in step with CHANGELOG.md and the git tag). Because users COPY dev.sh
# into their projects, this is how a project records which framework version it
# is running; `dev.sh version` and `dev.sh help` print it.
DEV_SH_VERSION="0.1.0"

set -uo pipefail

# Self-locate: the workspace is the parent of the scripts/ directory this file
# lives in, so each project's copy operates on its own workspace regardless of
# CWD or how it was invoked (PATH, ./scripts/dev.sh, absolute path). WORKSPACE
# in the environment still overrides.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="${WORKSPACE:-$(dirname "$SCRIPT_DIR")}"

# Guard against the multi-project PATH footgun. dev.sh always operates on the
# workspace it lives in (WORKSPACE above). If you keep several projects on this
# machine and put each scripts/ dir on $PATH, a bare `dev.sh` resolves to
# whichever came first and would silently act on THAT project's workspace, not
# the one you are standing in. This guard refuses to run a state-changing
# command when your current directory is not inside dev.sh's own workspace, so a
# mistake fails loudly instead of mutating the wrong project.
#
# Escape hatches: set WORKSPACE explicitly, or set DEV_ALLOW_ANY_CWD=1 to opt
# out entirely. Read-only/help commands are exempt (see the dispatch).
require_cwd_in_workspace() {
  [ "${DEV_ALLOW_ANY_CWD:-0}" = "1" ] && return 0
  local pwd_real ws_real
  pwd_real="$(cd "$PWD" 2>/dev/null && pwd -P)" || return 0
  ws_real="$(cd "$WORKSPACE" 2>/dev/null && pwd -P)" || return 0
  # Allow if PWD is the workspace or any subdirectory of it.
  case "$pwd_real/" in
    "$ws_real"/*) return 0 ;;
  esac
  echo "ERROR: you are running dev.sh from '$pwd_real'," >&2
  echo "       but this dev.sh operates on workspace '$ws_real'." >&2
  echo "" >&2
  echo "This usually means a different project's scripts/ came first on \$PATH." >&2
  echo "To act on THIS workspace, cd into it and use its own copy:" >&2
  echo "    cd '$ws_real' && scripts/dev.sh $*" >&2
  echo "Or set DEV_ALLOW_ANY_CWD=1 if you really intend to run it from here." >&2
  return 1
}

AGENT_IMAGE="cline-sandboxed"

# Host-reachable llama-swap endpoint for preflight checks. After Phase 4 the
# server binds to the Docker bridge gateway; override if yours differs.
LLAMA_SWAP_HOST_URL="${LLAMA_SWAP_HOST_URL:-http://172.17.0.1:8090}"
# Set DEV_SKIP_PREFLIGHT=1 to skip the pre-call model health check.
DEV_SKIP_PREFLIGHT="${DEV_SKIP_PREFLIGHT:-0}"

CODER_PROFILE="$HOME/.cline-coder"
REVIEWER_PROFILE="$HOME/.cline-reviewer"

# Containers run as a baked-in non-root user matching the host UID/GID (see
# Phase 6), so files stay host-owned and :ro mounts can't be written through.
# The Cline profile lives at /home/node/.cline inside the image's real HOME.
CONTAINER_CLINE="/home/node/.cline"

# --- Agent egress posture ------------------------------------------------
# The agent container only ever needs to reach llama-swap on the host at
# host.docker.internal:8090. It has no legitimate reason to reach the wider
# internet, and a misbehaving model with the whole workspace mounted could
# otherwise attempt to exfiltrate it. DEV_AGENT_NETWORK controls the posture:
#
#   default    (unchanged) attach to Docker's default bridge with
#              --add-host host.docker.internal:host-gateway. Convenient; the
#              agent can still reach the internet. Host-side egress filtering
#              (see SETUP_GUIDE Phase 5) is the recommended complement.
#
#   restricted attach to a dedicated user-defined bridge ('tenninety-agent')
#              so the agent is isolated from other containers on the default
#              bridge. The host gateway (and thus llama-swap) is still
#              reachable via --add-host. Combine with the host-side allow-only
#              firewall rule documented in Phase 5 to actually block non-8090
#              egress; a user-defined bridge is what makes that rule targetable
#              by network name without catching every other container.
#
# Default is left as 'default' so existing setups keep working; 'restricted'
# is the hardening opt-in.
DEV_AGENT_NETWORK="${DEV_AGENT_NETWORK:-default}"
AGENT_NETWORK_NAME="tenninety-agent"

# Ensure the dedicated bridge exists (idempotent). Only used in restricted mode.
ensure_agent_network() {
  docker network inspect "$AGENT_NETWORK_NAME" >/dev/null 2>&1 && return 0
  # A plain user-defined bridge (NOT --internal): the agent must still reach the
  # host gateway for llama-swap. Isolation from the wider internet is enforced
  # by the host firewall rule in Phase 5, targeted at this network's subnet.
  docker network create --driver bridge "$AGENT_NETWORK_NAME" >/dev/null 2>&1 || {
    echo "WARNING: could not create Docker network '$AGENT_NETWORK_NAME'; falling back to default." >&2
    return 1
  }
}

# Emit the docker network flags for an agent invocation into a named array.
# Usage: agent_net_args NET_ARR   (then expand "${NET_ARR[@]}")
agent_net_args() {
  local -n _net="$1"
  _net=()
  case "$DEV_AGENT_NETWORK" in
    restricted)
      if ensure_agent_network; then
        _net+=(--network "$AGENT_NETWORK_NAME")
      fi
      ;;
    default|*)
      : # default bridge; no extra flag
      ;;
  esac
  # llama-swap lives on the host in both modes; keep the gateway alias.
  _net+=(--add-host host.docker.internal:host-gateway)
}

# Preflight the local model server before a long synchronous agent call. A
# `dev.sh iterate`/`write-contract` blocks while the Coder or Reviewer runs, and
# if llama-swap is down or the endpoint is wrong the call hangs with a quiet
# terminal, indistinguishable from a slow first-load. This bounded check catches
# an unreachable server up front so the operator isn't left guessing. It only
# verifies reachability + model routing (a cheap /v1/models GET), not a full
# completion, so it stays fast. Returns 0 if reachable, 1 otherwise.
preflight_llama_swap() {
  [ "$DEV_SKIP_PREFLIGHT" = "1" ] && return 0
  command -v curl >/dev/null 2>&1 || return 0   # can't check; don't block
  local code
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 \
    "$LLAMA_SWAP_HOST_URL/v1/models" 2>/dev/null || echo "000")"
  if [ "$code" = "200" ]; then
    return 0
  fi
  echo "ERROR: llama-swap is not responding at $LLAMA_SWAP_HOST_URL (HTTP $code)." >&2
  echo "       The Coder/Reviewer call would otherwise hang with a silent terminal." >&2
  echo "" >&2
  echo "Check that the model server is up:" >&2
  echo "    systemctl --user status llama-swap.service" >&2
  echo "    curl -s $LLAMA_SWAP_HOST_URL/v1/models | jq -r '.data[].id'" >&2
  echo "If your bind address differs, set LLAMA_SWAP_HOST_URL. To skip this" >&2
  echo "check, set DEV_SKIP_PREFLIGHT=1." >&2
  return 1
}


# Detect the Contracts and Golden test project directory names.
CONTRACTS_DIR=$(find "$WORKSPACE/tests" -maxdepth 1 -type d -name '*.Contracts' -printf '%f\n' 2>/dev/null | head -1)
GOLDEN_DIR=$(find "$WORKSPACE/tests" -maxdepth 1 -type d -name '*.Golden' -printf '%f\n' 2>/dev/null | head -1)

# Always read-only to agents: contract tests, the golden harness project, the
# fixture, the dependency manifest, and the frozen spec originals. Anything an
# agent must not silently rewrite is a real :ro mount, not just a chmod.
RO_MOUNTS=()
[ -n "$CONTRACTS_DIR" ] && RO_MOUNTS+=(-v "$WORKSPACE/tests/$CONTRACTS_DIR:/workspace/tests/$CONTRACTS_DIR:ro")
[ -n "$GOLDEN_DIR" ] && RO_MOUNTS+=(-v "$WORKSPACE/tests/$GOLDEN_DIR:/workspace/tests/$GOLDEN_DIR:ro")
RO_MOUNTS+=(-v "$WORKSPACE/tests/fixtures:/workspace/tests/fixtures:ro")
RO_MOUNTS+=(-v "$WORKSPACE/Directory.Packages.props:/workspace/Directory.Packages.props:ro")
[ -f "$WORKSPACE/.cline/rules/architecture.original.md" ] && RO_MOUNTS+=(-v "$WORKSPACE/.cline/rules/architecture.original.md:/workspace/.cline/rules/architecture.original.md:ro")

run_agent() {
  local profile="$1" task="$2" writable="${3:-rw}"
  local mount_args=()

  if [ "$writable" = "ro" ]; then
    # Reviewer: entire workspace read-only, INCLUDING .git. The orchestrator
    # is the sole owner of Git state; the reviewer only needs to *read* the
    # diff, and `git diff` against a committed baseline does not write to .git.
    mount_args+=(-v "$WORKSPACE:/workspace:ro")
  else
    # Coder: workspace writable so it can edit src/, but .git is mounted
    # read-only on top so the agent can inspect diffs yet never mutate history,
    # tags, or the index. Only the orchestrator commits, tags, and resets.
    mount_args+=(-v "$WORKSPACE:/workspace:rw")
    if [ -d "$WORKSPACE/.git" ]; then
      mount_args+=(-v "$WORKSPACE/.git:/workspace/.git:ro")
    fi
  fi

  # GIT_OPTIONAL_LOCKS=0 stops even incidental .git writes (e.g. index refresh)
  # from a read-only .git mount.
  local net_args; agent_net_args net_args
  docker run --rm -i \
    -e GIT_OPTIONAL_LOCKS=0 \
    -e CLINE_SESSION_BACKEND_MODE=local \
    -e AI_SDK_LOG_WARNINGS=false \
    "${mount_args[@]}" \
    "${RO_MOUNTS[@]}" \
    -v "$profile:$CONTAINER_CLINE" \
    "${net_args[@]}" \
    "$AGENT_IMAGE" "$task" </dev/null
}

broadcast_prefix() {
  if [ -s "$WORKSPACE/BROADCAST.md" ]; then
    cat <<EOF
Read BROADCAST.md first and follow anything it says for this task:
$(cat "$WORKSPACE/BROADCAST.md")

---
EOF
  fi
}

# `git diff <tag>` omits untracked files, so a brand-new module file would be
# invisible to the reviewer and to escalation. The orchestrator (sole Git
# owner) records intent-to-add for any new file so it shows up in the diff,
# WITHOUT committing. Runs on the host; agents have .git read-only.
stage_untracked() {
  git -C "$WORKSPACE" add -N -- . 2>/dev/null || true
}

# --- Deterministic scope gate -------------------------------------------
# The module manifest in .cline/rules/architecture.md is the authoritative
# scope. Rather than trust the Reviewer model to catch out-of-scope edits,
# the orchestrator parses the manifest itself and hard-fails on any changed
# path not listed under the module's Implementation files / Shared
# integration files. `.cline/rules/architecture.md` is the sole global
# exception (a deliberate interface change).
#
# Manifest format (from the blueprint, rigidly specified):
#   **Module ID:** `invoice-calculator`
#   ### Implementation files
#   - `src/Project/Foo.cs` – ...
#   ### Shared integration files
#   - `src/Project/DependencyInjection.cs` – ...   (or the literal: None)

manifest_allowed_paths() {
  # manifest_allowed_paths <module-id> -> allowed paths, one per line.
  local module_id="$1"
  local spec="$WORKSPACE/.cline/rules/architecture.md"
  [ -f "$spec" ] || return 0
  awk -v id="$module_id" '
    # Enter this module block when its Module ID line matches exactly.
    /\*\*Module ID:\*\*[[:space:]]*`/ {
      line = $0
      sub(/^.*\*\*Module ID:\*\*[[:space:]]*`/, "", line)
      sub(/`.*$/, "", line)
      inblock = (line == id) ? 1 : 0
      grab = 0
      next
    }
    inblock && /^###[[:space:]]+(Implementation files|Shared integration files)([[:space:]]|$)/ { grab = 1; next }
    inblock && /^###[[:space:]]/ { grab = 0 }               # any other subsection ends grabbing
    inblock && /^##[[:space:]]/  { inblock = 0; grab = 0 }   # next module/section ends the block
    grab && /^-[[:space:]]+`/ {
      p = $0
      sub(/^-[[:space:]]+`/, "", p)
      sub(/`.*$/, "", p)
      if (p != "" && tolower(p) != "none") print p
    }
  ' "$spec"
}

# Returns 0 if all changed paths are in scope, 1 otherwise (printing the
# offending paths). Prints nothing on success.
scope_check() {
  local module_id="$1"
  local tag="module-start-$module_id"

  if ! git -C "$WORKSPACE" rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
    echo "SCOPE ERROR: module '$module_id' was never started (no tag $tag)." >&2
    return 1
  fi

  # Include untracked new files in the comparison (agents don't commit).
  stage_untracked

  local allowed
  allowed="$(manifest_allowed_paths "$module_id")"
  if [ -z "$allowed" ]; then
    echo "SCOPE ERROR: no Implementation/Shared files found for Module ID '$module_id' in .cline/rules/architecture.md." >&2
    echo "  Check the ID exists and its manifest lists paths under those headings." >&2
    return 1
  fi
  # The architecture file itself is always allowed (interface-change exception).
  allowed="$allowed
.cline/rules/architecture.md"

  local changed
  changed="$(git -C "$WORKSPACE" diff --name-only "$tag" 2>/dev/null)"
  [ -n "$changed" ] || return 0

  local offenders=""
  local path
  while IFS= read -r path; do
    [ -n "$path" ] || continue
    if ! printf '%s\n' "$allowed" | grep -qxF "$path"; then
      offenders="$offenders$path"$'\n'
    fi
  done <<EOF
$changed
EOF

  if [ -n "$offenders" ]; then
    echo "OUT-OF-SCOPE FILE(S) for module '$module_id' (not in its manifest):" >&2
    printf '%s' "$offenders" | sed 's/^/  - /' >&2
    echo "  Allowed paths come from Implementation files / Shared integration files in the manifest." >&2
    echo "  If this is a deliberate interface change, update the manifest to list the path (and follow the interface change policy)." >&2
    return 1
  fi
  return 0
}

# --- Interface-change (spec drift) gate ---------------------------------
# The pre-commit signature-drift hook only checks that architecture.md was
# edited in the same diff as a signature change. That alone can be satisfied by
# the Coder itself editing both the signature and the spec in one pass, with no
# human in the loop. This gate makes a spec change a HUMAN decision: if a
# module's diff touches .cline/rules/architecture.md, finalise/commit refuse
# unless the human passes --allow-spec-change, and print the change against the
# frozen architecture.original.md so it can be reviewed deliberately.
spec_changed_in_module() {
  # spec_changed_in_module <module-id> -> 0 if architecture.md is in the diff.
  local module_id="$1"
  local tag="module-start-$module_id"
  git -C "$WORKSPACE" rev-parse -q --verify "refs/tags/$tag" >/dev/null || return 1
  stage_untracked
  git -C "$WORKSPACE" diff --name-only "$tag" 2>/dev/null \
    | grep -qx '.cline/rules/architecture.md'
}

# Enforce the human gate. Returns 0 to proceed, 1 to refuse.
require_spec_change_ack() {
  # require_spec_change_ack <module-id> <allow-flag: 0|1> <command-name>
  local module_id="$1" allow="$2" cmd="$3"
  spec_changed_in_module "$module_id" || return 0   # no spec change: nothing to gate

  if [ "$allow" = "1" ]; then
    echo "==> Interface change acknowledged (--allow-spec-change)."
    echo "    Per the interface change policy, this change requires frontier-model review."
    return 0
  fi

  echo "REFUSING $cmd: this module's diff changes .cline/rules/architecture.md (an interface change)." >&2
  echo "" >&2
  echo "Interface changes must be a deliberate human decision, not slipped in by the Coder." >&2
  local orig="$WORKSPACE/.cline/rules/architecture.original.md"
  if [ -f "$orig" ]; then
    echo "Change vs the frozen original (architecture.original.md):" >&2
    echo "------------------------------------------------------------" >&2
    ( cd "$WORKSPACE" && git --no-pager diff --no-index -- \
        .cline/rules/architecture.original.md \
        .cline/rules/architecture.md 2>/dev/null ) >&2 || true
    echo "------------------------------------------------------------" >&2
  else
    echo "(No architecture.original.md found to diff against.)" >&2
  fi
  echo "" >&2
  echo "If this change is intended and has had the required frontier-model review," >&2
  echo "re-run with --allow-spec-change:" >&2
  echo "  $cmd $module_id --allow-spec-change" >&2
  return 1
}

# --- Module state gates -------------------------------------------------
# Gate markers live in .dev-runtime/<module-id>/ and record a CONTENT
# fingerprint, not a commit hash. Agents never commit, so HEAD does not move
# while a module is being built: a HEAD-based marker would compare equal to
# itself forever and could never detect that code changed after a gate passed.
# The fingerprint covers tracked modifications, staged content and untracked
# files, so any edit to the module invalidates every gate it already passed.

module_fingerprint() {
  # SHA-256 over the full working state relative to the module baseline.
  # .dev-runtime/ is excluded: it holds the markers themselves.
  {
    git -C "$WORKSPACE" diff --binary HEAD 2>/dev/null
    git -C "$WORKSPACE" ls-files --others --exclude-standard 2>/dev/null \
      | grep -v '^\.dev-runtime/' \
      | while IFS= read -r f; do
          printf '%s\n' "$f"
          cat "$WORKSPACE/$f" 2>/dev/null
        done
  } | sha256sum | cut -d' ' -f1
}

gate_dir() {
  local module_id="$1"
  mkdir -p "$WORKSPACE/.dev-runtime/$module_id/gates"
  echo "$WORKSPACE/.dev-runtime/$module_id/gates"
}

gate_pass() {
  # gate_pass <module-id> <gate-name> – record that this gate passed for the
  # current content fingerprint.
  local module_id="$1" gate="$2"
  module_fingerprint > "$(gate_dir "$module_id")/$gate"
}

gate_check() {
  # gate_check <module-id> <gate-name> – succeed only if the gate passed for
  # the CURRENT content. Any edit since then invalidates it.
  local module_id="$1" gate="$2"
  local marker; marker="$(gate_dir "$module_id")/$gate"
  [ -f "$marker" ] || return 1
  [ "$(cat "$marker")" = "$(module_fingerprint)" ] || return 2
}

require_started() {
  local module_id="$1"
  if ! git -C "$WORKSPACE" rev-parse -q --verify "refs/tags/module-start-$module_id" >/dev/null; then
    echo "ERROR: module '$module_id' was never started (no tag module-start-$module_id)."
    echo "Run 'dev.sh start $module_id' first, using a Module ID from architecture.md."
    return 1
  fi
}

require_gate() {
  # require_gate <module-id> <gate-name> <human description> <remedy command>
  local module_id="$1" gate="$2" what="$3" remedy="$4"
  gate_check "$module_id" "$gate"
  case $? in
    0) return 0 ;;
    1) echo "ERROR: $module_id has not passed $what."; echo "Run: $remedy"; return 1 ;;
    2) echo "ERROR: $module_id passed $what, but the code has changed since."
       echo "Re-run: $remedy"; return 1 ;;
  esac
}

cmd_start() {
  local module_id="$1"
  [ -n "$module_id" ] || { echo "usage: dev.sh start <module-id>"; return 1; }

  # Require an initial commit – the whole review architecture diffs against a
  # committed baseline.
  if ! git -C "$WORKSPACE" rev-parse HEAD >/dev/null 2>&1; then
    echo "ERROR: no commits yet. Commit the scaffold before starting a module."
    return 1
  fi

  # Require a clean tree so the module diff contains only this module's work
  # (this also catches leftover untracked files from a prior aborted run).
  # Ignore the orchestrator's own .dev-runtime/ scratch dir.
  if [ -n "$(git -C "$WORKSPACE" status --porcelain | grep -v '\.dev-runtime/')" ]; then
    echo "ERROR: working tree is not clean. Commit, stash, or reset first:"
    git -C "$WORKSPACE" status --short | grep -v '\.dev-runtime/'
    return 1
  fi

  # Fail loudly if this module was already started – never silently reuse a tag.
  if git -C "$WORKSPACE" rev-parse -q --verify "refs/tags/module-start-$module_id" >/dev/null; then
    echo "ERROR: module '$module_id' already started (tag module-start-$module_id exists)."
    echo "Use 'dev.sh reset $module_id' to discard it, or use the correct Module ID from architecture.md."
    return 1
  fi

  # Record the exact base commit both as a tag and as a plain-text baseline
  # under .dev-runtime, so later steps can diff against an unambiguous ref.
  local base
  base=$(git -C "$WORKSPACE" rev-parse HEAD)
  git -C "$WORKSPACE" tag "module-start-$module_id" "$base" || return 1
  mkdir -p "$WORKSPACE/.dev-runtime/$module_id"
  echo "$base" > "$WORKSPACE/.dev-runtime/$module_id/base-commit"
  echo "Started module '$module_id' at base commit ${base:0:12} (tag module-start-$module_id)."
}

cmd_write() {
  local task="$*"
  [ -n "$task" ] || { echo 'usage: dev.sh write "<task>"'; return 1; }
  preflight_llama_swap || return 1
  run_agent "$CODER_PROFILE" "$(broadcast_prefix)$task" rw
}

cmd_review() {
  local module_id="${1:-current}"
  # Run the deterministic scope gate first (only when a real module tag exists).
  if [ "$module_id" != "current" ]; then
    local scope_out
    if ! scope_out="$(scope_check "$module_id" 2>&1)"; then
      echo "$scope_out"
      echo "VERDICT: FAIL"
      return 1
    fi
  fi
  local task="Read .cline/skills/reviewer.md. Run 'git diff module-start-$module_id' in the terminal to see the actual changes (fall back to plain 'git diff' if that tag doesn't exist), then review against reviewer.md's checklist. End your response with a single verdict line that is exactly 'VERDICT: PASS' or 'VERDICT: FAIL' (list specific issues above it if it fails)."
  preflight_llama_swap || return 1
  run_agent "$REVIEWER_PROFILE" "$task" ro
}

cmd_test() {
  local module_id="$1"
  [ -n "$module_id" ] || { echo "usage: dev.sh test <module-id>"; return 1; }
  # Scope-gate before the test container touches the tree: a bare `dev.sh test`
  # after `dev.sh write` would otherwise restore/build an unvetted tree.
  if [ -n "$module_id" ] && git -C "$WORKSPACE" rev-parse -q --verify \
       "refs/tags/module-start-$module_id" >/dev/null; then
    local scope_out
    if ! scope_out="$(scope_check "$module_id" 2>&1)"; then
      echo "$scope_out" >&2
      return 1
    fi
  fi
  (cd "$WORKSPACE" && ./scripts/run_tests_with_cascade_check.sh "$module_id")
}

# --- Verdict parsing (fail-closed) --------------------------------------
# PASS only if there is exactly one VERDICT line and it is 'VERDICT: PASS'.
# Any 'VERDICT: FAIL' anywhere forces FAIL; multiple verdict lines fail closed.
parse_verdict() {
  local out="$1"
  local verdicts
  verdicts="$(printf '%s\n' "$out" \
    | grep -E '^[[:space:]]*VERDICT:' \
    | sed -E 's/\r$//; s/[[:space:]]+$//; s/^[[:space:]]+//')"
  if printf '%s\n' "$verdicts" | grep -qx 'VERDICT: FAIL'; then
    echo "FAIL"; return
  fi
  local count
  count="$(printf '%s\n' "$verdicts" | grep -cx 'VERDICT: PASS')"
  if [ "$count" = "1" ] && [ "$(printf '%s\n' "$verdicts" | grep -c 'VERDICT:')" = "1" ]; then
    echo "PASS"
  else
    echo "FAIL"
  fi
}

cmd_iterate() {
  local module_id="$1"
  # The original task is IMMUTABLE. Each retry sends the original task plus
  # ONLY the latest feedback (review findings or test log) – we never nest
  # the previous prompt inside the next one, which would grow the context
  # every iteration and work against the token-saving goal.
  local original_task="$2"
  local feedback=""
  local attempt=0
  local max_attempts=3

  # Keep injected feedback bounded so a huge log can't blow up the prompt.
  local FEEDBACK_MAX_LINES="${DEV_FEEDBACK_MAX_LINES:-120}"

  # Fail fast if the model server is down, rather than hanging on the first
  # Coder call with a silent terminal.
  preflight_llama_swap || return 1

  mkdir -p "$WORKSPACE/.dev-runtime/$module_id"
  local test_log="$WORKSPACE/.dev-runtime/$module_id/latest-test.log"

  while [ "$attempt" -lt "$max_attempts" ]; do
    attempt=$((attempt + 1))
    echo ""
    echo "========================================"
    echo "Iteration $attempt/$max_attempts for: $module_id"
    echo "========================================"

    # Compose this attempt's task: original + latest feedback only.
    local task="$original_task"
    if [ -n "$feedback" ]; then
      task="$original_task

--- FEEDBACK FROM THE PREVIOUS ATTEMPT (fix these, do not repeat them) ---
$feedback"
    fi

    echo ""
    echo "==> Pass $attempt: WRITE"
    local write_out
    write_out=$(run_agent "$CODER_PROFILE" "$(broadcast_prefix)$task" rw 2>&1) || true
    echo "$write_out"

    if echo "$write_out" | grep -qiE "spec is ambiguous|architecture\.md doesn't say|I don't know (whether|if)|the specification doesn't say"; then
      echo ""
      echo "WARNING: Spec gap suspected – inspect the Coder output above."
      echo "  Consider revising .cline/rules/architecture.md before the next iteration."
    fi

    echo ""
    echo "==> Pass $attempt: SCOPE"
    # Deterministic scope gate BEFORE the Reviewer model runs. An out-of-scope
    # edit fails mechanically here rather than relying on the Reviewer to spot
    # it. stage_untracked (called inside scope_check) makes new files visible.
    local scope_out
    if ! scope_out="$(scope_check "$module_id" 2>&1)"; then
      echo "$scope_out"
      echo ""
      echo "==> Scope FAILED – back to Write (Reviewer and Test skipped this iteration)."
      feedback="SCOPE FAILED. You changed files outside this module's manifest. Delete any newly-created out-of-scope files, and for a modified tracked file restore it with 'git show module-start-$module_id:<path> > <path>' (your .git is read-only, so 'git checkout' will fail). Only touch the paths listed under this module's Implementation files / Shared integration files:
$(echo "$scope_out" | tail -n "$FEEDBACK_MAX_LINES")"
      continue
    fi

    echo ""
    echo "==> Pass $attempt: REVIEW"
    # Files were already staged by scope_check so the reviewer's diff sees them.
    local review_out
    review_out=$(run_agent "$REVIEWER_PROFILE" "Read .cline/skills/reviewer.md. Run 'git diff module-start-$module_id' (fall back to 'git diff' if that tag doesn't exist) and review against reviewer.md's checklist. End your response with a single verdict line that is exactly 'VERDICT: PASS' or 'VERDICT: FAIL' (list specific issues above it if it fails)." ro 2>&1) || true
    echo "$review_out"

    # Fail-closed: only the LAST 'VERDICT:' line counts, and it must be exactly
    # 'VERDICT: PASS'. A pass verdict quoted earlier in prose, in the echoed
    # prompt, or inside a code comment in the diff cannot wave the module
    # through; a missing or garbled verdict fails.
    if [ "$(parse_verdict "$review_out")" != "PASS" ]; then
      echo ""
      echo "==> Review FAILED – back to Write (no Test run this iteration)."
      # Feed ONLY the reviewer's findings into the next attempt.
      feedback="REVIEW FAILED. Address these findings:
$(echo "$review_out" | tail -n "$FEEDBACK_MAX_LINES")"
      continue
    fi

    echo ""
    echo "==> Pass $attempt: TEST"
    # Capture the test output so we can (a) show it, (b) persist it as a
    # per-module artefact, and (c) inject it into the next Coder attempt.
    # Without this, the next container cannot see what failed.
    local test_out test_rc
    test_out=$( (cd "$WORKSPACE" && ./scripts/run_tests_with_cascade_check.sh "$module_id") 2>&1 )
    test_rc=$?
    echo "$test_out"
    printf '%s\n' "$test_out" > "$test_log"

    if [ "$test_rc" -eq 0 ]; then
      # Test code ran with the tree writable; re-check scope before stamping so
      # a test that rewrote files can't smuggle post-scope edits into the gate.
      local post_scope
      if ! post_scope="$(scope_check "$module_id" 2>&1)"; then
        echo "$post_scope"
        echo ""
        echo "==> Tests passed but the tree changed out of scope during the run – back to Write."
        feedback="POST-TEST SCOPE FAILED. Test execution left out-of-scope changes:
$(echo "$post_scope" | tail -n "$FEEDBACK_MAX_LINES")"
        continue
      fi
      gate_pass "$module_id" reviewed
      gate_pass "$module_id" fast-tests
      echo ""
      echo "==> PASS – module $module_id cleared review and the fast tier."
      echo "    Run 'dev.sh finalise $module_id' to run integration tests."
      return 0
    fi

    echo ""
    echo "==> Tests FAILED – back to Write (log saved to $test_log)."
    # Feed ONLY the (bounded) failing test output into the next attempt.
    feedback="TESTS FAILED. Fix the underlying cause of this output:
$(echo "$test_out" | tail -n "$FEEDBACK_MAX_LINES")"
  done

  echo ""
  echo "========================================"
  echo "3 iterations failed for: $module_id"
  echo "========================================"
  echo ""
  echo "Latest test log: $test_log"
  echo ""
  echo "Escalation is one-shot per module. Use the tiers in order:"
  echo "  (1) dev.sh escalate $module_id $test_log                          # frontier PLAN (no --override)"
  echo "  (2) dev.sh escalate $module_id $test_log --override               # deliberate 2nd plan after review"
  echo "  (3) dev.sh escalate $module_id $test_log --override --write-code   # frontier writes the fix"
  echo "  (4) Revise .cline/rules/architecture.md for this module"
  echo "  (5) dev.sh reset $module_id"
  echo ""
  echo "The script will not proceed automatically."
  return 1
}

cmd_finalise() {
  # Completion gate for a module: the slow (integration) tier runs here, once,
  # plus downstream interface-drift propagation – BEFORE the module can be
  # queued. The fast tier runs every iterate; integration runs once per
  # completed module. Only a successful finalise enables 'commit'/'queue'.
  local module_id="$1"
  [ -n "$module_id" ] || { echo "usage: dev.sh finalise <module-id> [--allow-spec-change]"; return 1; }
  shift || true
  local allow_spec=0
  for arg in "$@"; do
    [ "$arg" = "--allow-spec-change" ] && allow_spec=1
  done

  # A module that was never started has no baseline to diff or review against;
  # finalising it would let an unimplemented module reach the queue.
  require_started "$module_id" || return 1

  # An interface change (edit to architecture.md) must be a deliberate human
  # decision, not something the Coder slipped in alongside a signature change.
  require_spec_change_ack "$module_id" "$allow_spec" "dev.sh finalise" || return 1

  # Integration tests alone are not a substitute for the loop: require that
  # THIS content already passed review and the fast tier via 'dev.sh iterate'.
  require_gate "$module_id" fast-tests "review and the fast test tier" \
    "dev.sh iterate $module_id \"<task>\"" || return 1

  echo "==> Integration tests (slow tier)"
  if ! (cd "$WORKSPACE" && ./scripts/run_integration_tests.sh); then
    echo "Integration tests failed – not finalising $module_id."
    return 1
  fi

  echo ""
  echo "==> Downstream interface-drift check"
  # A crash in the drift tool must not be read as "no interface changed".
  if ! (cd "$WORKSPACE" && ./scripts/check_interface_drift.sh "$module_id"); then
    echo "ERROR: the interface-drift check failed to run."
    echo "Fix the drift tooling before finalising – a broken checker cannot be"
    echo "treated as 'no public API changes'."
    return 1
  fi

  gate_pass "$module_id" integration
  echo "Finalised $module_id. Now run 'dev.sh commit $module_id'."
}

cmd_queue() {
  local module_id="$1"
  [ -n "$module_id" ] || { echo "usage: dev.sh queue <module-id>"; return 1; }
  require_started "$module_id" || return 1

  # Require a successful finalise for the CURRENT content – not merely for the
  # current commit. Agents never commit, so a HEAD comparison could never
  # detect code edited after finalise.
  require_gate "$module_id" integration "the integration tier ('finalise')" \
    "dev.sh finalise $module_id" || return 1

  # The module's work must be committed before it is queued: the next module
  # cannot start on a dirty tree, and a queued module must be a fixed artefact.
  if ! gate_check "$module_id" committed; then
    echo "ERROR: $module_id has not been committed."
    echo "Run: dev.sh commit $module_id"
    return 1
  fi

  (cd "$WORKSPACE" && ./scripts/queue_for_review.sh "$module_id") || return 1
  # Fold the queue row into the module's commit so the tree stays clean and the
  # next 'dev.sh start' can run.
  git -C "$WORKSPACE" add REVIEW_QUEUE.md >/dev/null 2>&1 || true
  if ! git -C "$WORKSPACE" diff --cached --quiet 2>/dev/null; then
    git -C "$WORKSPACE" commit -q -m "queue($module_id): ready for review" || return 1
  fi
  echo "Queued $module_id. Working tree is clean; you can start the next module."
}

cmd_commit() {
  # The orchestrator owns all Git state: agents never commit. This is the step
  # that turns a finalised module into a fixed artefact and returns the tree to
  # a clean state so the NEXT module can start.
  local module_id="$1"
  [ -n "$module_id" ] || { echo "usage: dev.sh commit <module-id> [--allow-spec-change]"; return 1; }
  shift || true
  local allow_spec=0
  for arg in "$@"; do
    [ "$arg" = "--allow-spec-change" ] && allow_spec=1
  done
  require_started "$module_id" || return 1
  require_spec_change_ack "$module_id" "$allow_spec" "dev.sh commit" || return 1
  require_gate "$module_id" integration "the integration tier ('finalise')" \
    "dev.sh finalise $module_id" || return 1

  # Stage everything except the orchestrator's own scratch directory.
  (cd "$WORKSPACE" && git add -A -- . ':!.dev-runtime' >/dev/null 2>&1) || {
    echo "ERROR: could not stage module changes."; return 1; }

  if git -C "$WORKSPACE" diff --cached --quiet 2>/dev/null; then
    echo "Nothing to commit for $module_id."
  else
    # Pre-commit hooks run here by design: signature drift, raw SQL
    # and formatting all gate the module's own commit.
    if ! git -C "$WORKSPACE" commit -q -m "feat($module_id): implement module"; then
      echo "ERROR: commit rejected (pre-commit hooks failed). Fix and re-run."
      return 1
    fi
    echo "Committed $module_id."
  fi
  # Committing necessarily changes the content fingerprint (the diff against
  # HEAD becomes empty), which would invalidate the gates this module just
  # earned. Re-stamp them at the new fingerprint: the CONTENT is identical –
  # only its Git location moved – so the gates remain truthful.
  gate_pass "$module_id" reviewed
  gate_pass "$module_id" fast-tests
  gate_pass "$module_id" integration
  gate_pass "$module_id" committed
  echo "Now run 'dev.sh queue $module_id'."
}

cmd_fix() {
  (cd "$WORKSPACE" && ./scripts/apply_review_feedback.sh "$1")
  local rc=$?
  if [ $rc -ne 0 ]; then
    echo ""
    echo "After fix failure, check downstream modules:"
    (cd "$WORKSPACE" && ./scripts/check_interface_drift.sh "$1" 2>/dev/null || true)
  fi
  # Propagate the real result: a failed fix must not look like success.
  return $rc
}

cmd_escalate() {
  (cd "$WORKSPACE" && python scripts/escalate.py "$@")
}

cmd_reject() {
  local module_id="$1"
  [ -n "$module_id" ] || { echo "usage: dev.sh reject <module-id> \"<feedback>\""; return 1; }
  shift
  local feedback="$*"
  [ -n "$feedback" ] || { echo "usage: dev.sh reject <module-id> \"<feedback>\""; return 1; }
  mkdir -p "$WORKSPACE/review-feedback"
  echo "$feedback" > "$WORKSPACE/review-feedback/$module_id.md"
  # Increment the rejection count in the same edit that sets the status, so the
  # documented three-strikes rule can actually fire.
  local count
  count=$(awk -F'|' -v m=" $module_id " '$2==m {gsub(/ /,"",$4); print $4}' "$WORKSPACE/REVIEW_QUEUE.md" | head -1)
  [[ "$count" =~ ^[0-9]+$ ]] || count=0
  count=$((count + 1))
  sed -i "s/| $module_id | [^|]* | [^|]* |/| $module_id | needs-fixes | $count |/" "$WORKSPACE/REVIEW_QUEUE.md"
  # A rejected module must re-earn every gate.
  rm -f "$WORKSPACE/.dev-runtime/$module_id/gates/"* 2>/dev/null || true
  echo "Rejected $module_id (rejections: $count) – feedback in review-feedback/$module_id.md"
  if [ "$count" -ge 3 ]; then
    echo ""
    echo "NOTE: $module_id has now been rejected $count times. Per the working"
    echo "guide, stop asking for another fix – revise this module's section of"
    echo "architecture.md with the frontier model instead."
  fi
}

cmd_approve() {
  local module_id="$1"
  [ -n "$module_id" ] || { echo "usage: dev.sh approve <module-id>"; return 1; }
  sed -i "s/| $1 | [^|]* |/| $1 | approved |/" "$WORKSPACE/REVIEW_QUEUE.md"
  echo "Approved $module_id"
}

cmd_status() {
  echo "=== REVIEW QUEUE ==="
  cat "$WORKSPACE/REVIEW_QUEUE.md" 2>/dev/null || echo "(empty)"
  echo ""
  echo "=== ESCALATIONS ==="
  cat "$WORKSPACE/.escalations.json" 2>/dev/null || echo "(none)"
  echo ""
  echo "=== RECENT COMMITS ==="
  git -C "$WORKSPACE" log --oneline -5 2>/dev/null || echo "(no commits)"
  echo ""
  echo "=== BROADCAST ==="
  cat "$WORKSPACE/BROADCAST.md" 2>/dev/null || echo "(none)"
}

# --- Manifest coverage check --------------------------------------------
# Every implementation file under src/ should belong to at least one module
# manifest (Implementation files / Shared integration files). This was a rule
# for the frontier model to uphold; make it a mechanical check so an orphaned
# file — code that no module owns and no scope gate protects — is caught.
# Prints orphaned paths and returns 1 if any exist.
all_manifest_paths() {
  local spec="$WORKSPACE/.cline/rules/architecture.md"
  [ -f "$spec" ] || return 0
  awk '
    /^###[[:space:]]+(Implementation files|Shared integration files)([[:space:]]|$)/ { grab = 1; next }
    /^###[[:space:]]/ { grab = 0 }
    /^##[[:space:]]/  { grab = 0 }
    grab && /^-[[:space:]]+`/ {
      p = $0; sub(/^-[[:space:]]+`/, "", p); sub(/`.*$/, "", p)
      if (p != "" && tolower(p) != "none") print p
    }
  ' "$spec" | sort -u
}

cmd_check_coverage() {
  local src="$WORKSPACE/src"
  [ -d "$src" ] || { echo "No src/ directory to check."; return 0; }

  local allowed; allowed="$(all_manifest_paths)"
  if [ -z "$allowed" ]; then
    echo "WARNING: no manifest paths found in .cline/rules/architecture.md; cannot check coverage." >&2
    return 0
  fi

  # Every tracked C# source file under src/ (exclude generated/obj/bin).
  local orphans=""
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    case "$f" in */obj/*|*/bin/*) continue ;; esac
    if ! printf '%s\n' "$allowed" | grep -qxF "$f"; then
      orphans="$orphans$f"$'\n'
    fi
  done < <(cd "$WORKSPACE" && git ls-files 'src/**/*.cs' 2>/dev/null)

  if [ -n "$orphans" ]; then
    echo "MANIFEST COVERAGE: these src/ files are not listed in any module manifest:" >&2
    printf '%s' "$orphans" | sed 's/^/  - /' >&2
    echo "Add each to a module's Implementation files / Shared integration files, or remove it." >&2
    return 1
  fi
  echo "Manifest coverage OK: every tracked src/*.cs file belongs to a module manifest."
  return 0
}

cmd_reset() {
  local module_id="$1"
  [ -n "$module_id" ] || { echo "usage: dev.sh reset <module-id>"; return 1; }

  # reset --hard + clean -fd is destructive: it discards all module work,
  # including uncommitted and untracked files. Stash a safety backup first so a
  # mistaken reset is recoverable. We use a real stash bundle (tracked +
  # untracked) written to .dev-runtime, which survives the reset because clean
  # excludes .dev-runtime.
  local backup_dir="$WORKSPACE/.dev-runtime/reset-backups"
  mkdir -p "$backup_dir"
  local stamp; stamp="$(date +%Y%m%d-%H%M%S)"
  local bundle="$backup_dir/${module_id}-${stamp}.bundle"
  local patch="$backup_dir/${module_id}-${stamp}.uncommitted.patch"
  # Bundle current HEAD history and save a patch of uncommitted+untracked work.
  git -C "$WORKSPACE" bundle create "$bundle" HEAD >/dev/null 2>&1 || true
  {
    git -C "$WORKSPACE" diff "module-start-$module_id" 2>/dev/null
  } > "$patch" 2>/dev/null || true
  # Also snapshot untracked files as a tar so nothing is silently lost.
  ( cd "$WORKSPACE" && git ls-files --others --exclude-standard -z \
      | grep -zv '^\.dev-runtime/' \
      | tar --null -T - -czf "$backup_dir/${module_id}-${stamp}.untracked.tar.gz" 2>/dev/null ) || true
  echo "Safety backup saved under .dev-runtime/reset-backups/${module_id}-${stamp}.* (recover with 'git apply' / 'git bundle')."

  # reset --hard restores tracked files to the baseline but leaves
  # module-created UNTRACKED files behind. Clean those too (but preserve the
  # per-module runtime dir until after, and never touch ignored build output
  # you might want – we scope the clean to tracked-ignore rules with -d, not -x).
  git -C "$WORKSPACE" reset --hard "module-start-$module_id" || return 1
  git -C "$WORKSPACE" clean -fd -e '.dev-runtime' || return 1
  # Delete the baseline tag too, so the module can be cleanly re-started.
  git -C "$WORKSPACE" tag -d "module-start-$module_id" >/dev/null 2>&1 || true

  # Remove the module's queue row whatever its status (ready-for-review,
  # needs-fixes, interface-changed, ...), so a reset never orphans a row
  # pointing at code that no longer exists.
  [ -f "$WORKSPACE/REVIEW_QUEUE.md" ] && sed -i "/^| $module_id |/d" "$WORKSPACE/REVIEW_QUEUE.md"
  rm -rf "$WORKSPACE/.dev-runtime/$module_id"
  echo "Reset module '$module_id' to module-start-$module_id (tracked + untracked) and removed from queue."
}

cmd_broadcast() {
  local note="$*"
  if [ -z "$note" ]; then
    > "$WORKSPACE/BROADCAST.md"
    echo "Cleared BROADCAST.md"
  else
    echo "## $(date '+%Y-%m-%dT%H:%M%z')" >> "$WORKSPACE/BROADCAST.md"
    echo "$note" >> "$WORKSPACE/BROADCAST.md"
    echo "" >> "$WORKSPACE/BROADCAST.md"
    echo "Added broadcast note."
  fi
}

cmd_notes() {
  cat "$WORKSPACE/BROADCAST.md" 2>/dev/null || echo "(no broadcast notes)"
}

stage_llm_test_files() {
  # Have the Coder author test files with the workspace mounted READ-ONLY and a
  # separate writable /workspace/.cline-output. During generation the agent cannot write anywhere
  # in the workspace at all – not src/, not other tests, not the existing
  # contract suite. The host then validates what was produced and moves it into
  # place, read-only.
  #
  # Usage: stage_llm_test_files <destination_dir> <task> [exact_name]
  #
  # Name the third argument to require that one exact file and nothing else
  # (the golden harness has a single documented path). Omit it and the agent
  # chooses the names from the module manifest – one <Type>Tests.cs per public
  # entry point.
  #
  # Every staged file must be named <Type>Tests.cs and must not already exist:
  # the write-once guarantee is enforced per file, so a module can gain a test
  # for a new entry point later without ever overwriting an existing one. The
  # whole batch is validated before anything moves, so a rejected batch leaves
  # the destination untouched.
  local destination_dir="$1" task="$2" exact_name="${3:-}"
  preflight_llama_swap || return 1
  local staging
  staging=$(mktemp -d)

  mkdir -p "$WORKSPACE/.cline-output"

  local net_args; agent_net_args net_args
  docker run --rm -i \
    -e GIT_OPTIONAL_LOCKS=0 \
    -e CLINE_SESSION_BACKEND_MODE=local \
    -e AI_SDK_LOG_WARNINGS=false \
    -v "$WORKSPACE:/workspace:ro" \
    -v "$staging:/workspace/.cline-output:rw" \
    -v "$CODER_PROFILE:$CONTAINER_CLINE" \
    "${net_args[@]}" \
    "$AGENT_IMAGE" "$task" </dev/null
  local rc=$?
  if [ "$rc" -ne 0 ]; then rm -rf "$staging"; return "$rc"; fi

  local generated=()
  while IFS= read -r f; do generated+=("$f"); done < <(find "$staging" -maxdepth 1 -type f -name '*.cs' | sort)

  if [ "${#generated[@]}" -eq 0 ]; then
    echo "ERROR: the agent staged no .cs files in /workspace/.cline-output."
    rm -rf "$staging"
    return 1
  fi

  # Validate every staged file BEFORE moving any of them.
  local f base
  for f in "${generated[@]}"; do
    base=$(basename "$f")
    if [ -n "$exact_name" ] && { [ "${#generated[@]}" -ne 1 ] || [ "$base" != "$exact_name" ]; }; then
      echo "ERROR: expected exactly /workspace/.cline-output/$exact_name; got:"
      find "$staging" -maxdepth 1 -type f -printf '  %f\n'
      rm -rf "$staging"
      return 1
    fi
    case "$base" in
      *Tests.cs) ;;
      *)
        echo "ERROR: staged file does not follow the <Type>Tests.cs convention: $base"
        rm -rf "$staging"
        return 1
        ;;
    esac
    if [ -e "$destination_dir/$base" ]; then
      echo "ERROR: $destination_dir/$base already exists."
      echo "Staged test files are write-once. Delete it by hand to regenerate."
      rm -rf "$staging"
      return 1
    fi
  done

  for f in "${generated[@]}"; do
    base=$(basename "$f")
    mv "$f" "$destination_dir/$base"
    chmod 444 "$destination_dir/$base"
    echo "Created $destination_dir/$base and set it read-only."
  done
  rm -rf "$staging"
}

cmd_write_contract() {
  local module_id="${1:?Usage: dev.sh write-contract <module-id>}"

  [ -n "$CONTRACTS_DIR" ] || { echo "Contracts project not found."; return 1; }
  local destination="$WORKSPACE/tests/$CONTRACTS_DIR"

  # The Module ID is a manifest key, not a class name. The manifest – not this
  # script and not a filename – defines how many public entry points the module
  # exposes, so the agent derives one contract file per entry point rather than
  # having a single name imposed on it. The write-once guarantee is enforced
  # per file inside stage_llm_test_files.
  local task="Read .cline/rules/architecture.md and .cline/skills/coder.md. Locate the module manifest whose Module ID is exactly '$module_id'; that manifest is the authoritative scope. Identify every public entry point the manifest documents for this module – a type that consumers outside the module call directly. For EACH entry point, write one xUnit contract test file to /workspace/.cline-output/ named <TypeName>Tests.cs (PascalCase type name, no other files). Do not create a file for types that are only reachable through another entry point. In each file, check every documented public type, constructor, method overload, generic arity, parameter name/type/order, return type, nullability, property type and relevant static/instance distinction for that entry point. Use exact reflection lookups with parameter-type arrays; never use GetMethod(name) alone when overloads are possible. Add [Trait(\"Category\", \"Contract\")] to every test. If a contract test for an entry point already exists in the repository, do not write that file again. Do not write implementation code. Do not modify anything under /workspace except /workspace/.cline-output."

  stage_llm_test_files "$destination" "$(broadcast_prefix)$task"
}

cmd_write_golden_harness() {
  # The frontier authors the golden fixture; the HARNESS that runs it is the
  # strongest correctness gate in the pipeline, so it is NOT written by the
  # local Coder. A subtly loose comparison (double vs decimal, culture-dependent
  # parsing, reference equality) would silently let broken code pass. Instead we
  # instantiate a canonical, pre-tested harness shipped in the starter kit,
  # substituting the project name, and freeze it read-only.
  [ -n "$GOLDEN_DIR" ] || { echo "Golden test project not found."; return 1; }
  local destination="$WORKSPACE/tests/$GOLDEN_DIR"
  local filename="CriticalLogicGoldenTests.cs"

  [ ! -e "$destination/$filename" ] || {
    echo "Golden harness already exists: $destination/$filename"
    return 1
  }

  # Derive the PascalCase project name from the Golden project directory name,
  # e.g. "TaskTracker.Golden" -> "TaskTracker".
  local project_name="${GOLDEN_DIR%.Golden}"
  if [ -z "$project_name" ] || [ "$project_name" = "$GOLDEN_DIR" ]; then
    echo "ERROR: could not derive the project name from Golden dir '$GOLDEN_DIR'."
    echo "Expected a directory named '<ProjectName>.Golden'."
    return 1
  fi

  # Locate the canonical template. In a scaffolded project it is copied under
  # scripts/golden-harness/ (Phase 9.3); in the framework clone it lives beside
  # the starter kit's tests/.
  local template=""
  for cand in \
    "$WORKSPACE/scripts/golden-harness/CriticalLogicGoldenTests.cs.template" \
    "$SCRIPT_DIR/golden-harness/CriticalLogicGoldenTests.cs.template" \
    "$SCRIPT_DIR/../tests/golden-harness/CriticalLogicGoldenTests.cs.template"; do
    if [ -f "$cand" ]; then template="$cand"; break; fi
  done
  if [ -z "$template" ]; then
    echo "ERROR: canonical golden-harness template not found."
    echo "Expected scripts/golden-harness/CriticalLogicGoldenTests.cs.template in the workspace."
    return 1
  fi

  # Substitute the single token and write the harness, then freeze it.
  sed "s/__PROJECT__/$project_name/g" "$template" > "$destination/$filename" || {
    echo "ERROR: failed to write $destination/$filename"; return 1; }
  chmod 444 "$destination/$filename"
  echo "Wrote canonical golden harness: $destination/$filename (frozen read-only)."
  echo "It runs tests/fixtures/critical_logic_golden.json against production entry points."
  echo "The harness is not agent-authored; do not let a Coder edit it."
}

cmd_show_frontier_fix() {
  # NOTE: this only DISPLAYS the frontier-written fix for manual application;
  # it deliberately applies nothing (the human stays in the loop for
  # frontier-authored code).
  local module_id="$1"
  local fix_file="$WORKSPACE/frontier-fix-${module_id}.md"
  if [ ! -f "$fix_file" ]; then
    echo "No frontier fix file found at $fix_file"
    echo "Run 'dev.sh escalate $module_id <log> --override --write-code' first."
    return 1
  fi
  echo "Frontier fix file: $fix_file"
  echo "Review it manually, then apply the code blocks to the relevant files."
  echo "After applying, run: dev.sh test $module_id"
}

cmd_help() {
  echo "dev.sh version $DEV_SH_VERSION"
  echo ""
  cat <<'EOF'
dev.sh – orchestrator for the local coding loop (C# / .NET)

Usage: dev.sh <subcommand> [args]

Subcommands:
  start <module-id>                          Create module-start-<module-id> tag (needs clean tree)
  write "<task>"                        Run the Coder with a task
  review [module-id]                         Run the Reviewer on the current diff
  test <module-id>                           Run the fast test tier
  iterate <module-id> "<task>"               Full loop: write->review->test (3 attempts)
  finalise <module-id> [--allow-spec-change] Integration tests + drift check (once per feature)
  commit <module-id> [--allow-spec-change]   Commit the finalised module (orchestrator owns Git)
  queue <module-id>                          Queue a module (requires finalise first)
  fix <module-id>                            Apply human review feedback (3 attempts)
  escalate <module-id> [log] [--override]    Frontier escalation (first call: no --override)
    [--write-code]                        (with --override: frontier writes code)
  reject <module-id> "<feedback>"            Reject a module with feedback
  approve <module-id>                        Approve a module
  status                                Show queue, escalations, recent commits
  check-coverage                        Verify every tracked src/*.cs file is in a module manifest
  reset <module-id>                          Reset module (tracked + untracked) to baseline
  broadcast "<note>"                    Add a note all Coders will see
  notes                                 Show current broadcast notes
  write-contract <module-id>                 Write a contract test for a module (staged, write-once)
  write-golden-harness                  Write the deterministic golden-fixture harness (staged, write-once)
  show-frontier-fix <module-id>              Display frontier-written fix (applies nothing)
  help                                  Show this message
  version                               Print the framework version (DEV_SH_VERSION)

Example workflow for a new module:
  dev.sh start <module-id>
  dev.sh write-contract <module-id>
  dev.sh iterate <module-id> "Implement the complete <ModuleName> module (Module ID: <module-id>) exactly as defined in .cline/rules/architecture.md. Create or edit only the files listed in that module manifest."
  dev.sh finalise <module-id>
  dev.sh queue <module-id>

Other:
  dev.sh status
  dev.sh reject <module-id> "<feedback>"
  dev.sh fix <module-id>
  dev.sh approve <module-id>
EOF
}

cmd="${1:-help}"

# Apply the CWD guard only to commands that act on the workspace (mutate state
# or invoke agents). Read-only/help commands run from anywhere.
case "$cmd" in
  help|--help|-h|version|--version|-v|status|notes|show-frontier-fix)
    : ;;  # exempt from CWD guard
  *)
    require_cwd_in_workspace "$@" || exit 1 ;;
esac

# Serialise mutating commands per workspace. Two concurrent invocations (e.g.
# two terminals running 'iterate' on the same project) could otherwise race the
# content-fingerprint gates and the git working tree. flock gives a single
# writer per workspace; read-only commands are exempt so 'status' never blocks.
# If flock isn't installed we proceed without it (best effort) rather than fail.
case "$cmd" in
  help|--help|-h|version|--version|-v|status|notes|show-frontier-fix|check-coverage)
    : ;;  # no lock needed (read-only)
  *)
    if [ -z "${DEV_LOCK_HELD:-}" ] && command -v flock >/dev/null 2>&1; then
      mkdir -p "$WORKSPACE/.dev-runtime" 2>/dev/null || true
      lockfile="$WORKSPACE/.dev-runtime/dev.lock"
      # Open the lock on fd 9 and try a non-blocking exclusive grab. If another
      # invocation holds it, fail fast with a clear message instead of queuing.
      exec 9>"$lockfile" || true
      if command -v flock >/dev/null 2>&1 && ! flock -n 9; then
        echo "ERROR: another dev.sh command is already running in this workspace." >&2
        echo "       Wait for it to finish, or work in a separate workspace." >&2
        exit 1
      fi
      # Lock held on fd 9 for the lifetime of this process; children inherit it.
      export DEV_LOCK_HELD=1
    fi
    ;;
esac

case "$cmd" in
  start)              shift; cmd_start "$@" ;;
  write)              shift; cmd_write "$@" ;;
  review)             shift; cmd_review "$@" ;;
  test)               shift; cmd_test "$@" ;;
  iterate)            shift; cmd_iterate "$@" ;;
  finalise)           shift; cmd_finalise "$@" ;;
  commit)             shift; cmd_commit "$@" ;;
  queue)              shift; cmd_queue "$@" ;;
  fix)                shift; cmd_fix "$@" ;;
  escalate)           shift; cmd_escalate "$@" ;;
  reject)             shift; cmd_reject "$@" ;;
  approve)            shift; cmd_approve "$@" ;;
  status)             cmd_status ;;
  check-coverage)     cmd_check_coverage ;;
  reset)              shift; cmd_reset "$@" ;;
  broadcast)          shift; cmd_broadcast "$@" ;;
  notes)              cmd_notes ;;
  write-contract)     shift; cmd_write_contract "$@" ;;
  write-golden-harness) shift; cmd_write_golden_harness "$@" ;;
  show-frontier-fix)  shift; cmd_show_frontier_fix "$@" ;;
  help|--help|-h)     cmd_help ;;
  version|--version|-v) echo "dev.sh version $DEV_SH_VERSION" ;;
  *)                  echo "Unknown subcommand: $1"; cmd_help; exit 1 ;;
esac
