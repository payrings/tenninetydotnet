#!/bin/bash
#
# scripts/dev.sh – single entry point for the local coding loop (C# / .NET).
# Usage: dev.sh <subcommand> [args]
#
# Agent runtime: aider (https://aider.chat), invoked single-shot inside the
# aider-sandboxed container via `aider --message-file`. Unlike an agentic CLI,
# aider does not run a read/run loop of its own, so this script injects ALL
# context explicitly: the spec and skill files are attached (--read or
# editable), the module's manifest files are attached editable to the Coder,
# and the module diff is inlined into the Reviewer's prompt. Nothing is
# auto-loaded.

# Framework version this orchestrator ships with. Bump on every tagged release
# (keep in step with CHANGELOG.md and the git tag). Because users COPY dev.sh
# into their projects, this is how a project records which framework version it
# is running; `dev.sh version` and `dev.sh help` print it.
DEV_SH_VERSION="0.2.1"

set -uo pipefail

# Self-locate: the workspace is the parent of the scripts/ directory this file
# lives in, so each project's copy operates on its own workspace regardless of
# CWD or how it was invoked (PATH, ./scripts/dev.sh, absolute path). WORKSPACE
# in the environment still overrides.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="${WORKSPACE:-$(dirname "$SCRIPT_DIR")}"
WORKSPACE="$(cd "$WORKSPACE" 2>/dev/null && pwd -P)" || {
  echo "ERROR: cannot resolve workspace '$WORKSPACE'." >&2
  exit 1
}
# Runtime markers, logs, locks and backups are host-owned control state. Keep
# them outside the workspace mounted into agent and test containers.
# shellcheck source=runtime.sh
source "$SCRIPT_DIR/runtime.sh"
tenninety_init_runtime "$WORKSPACE" || exit 1

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
  pwd_real="$(cd "$PWD" 2>/dev/null && pwd -P)" || {
    echo "ERROR: cannot resolve the current directory; refusing a workspace operation." >&2
    return 1
  }
  ws_real="$(cd "$WORKSPACE" 2>/dev/null && pwd -P)" || {
    echo "ERROR: cannot resolve the configured workspace." >&2
    return 1
  }
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

AGENT_IMAGE="aider-sandboxed"

# Host-reachable llama-swap endpoint for preflight checks. After Phase 4 the
# server binds to the Docker bridge gateway; override if yours differs.
LLAMA_SWAP_HOST_URL="${LLAMA_SWAP_HOST_URL:-http://172.17.0.1:8090}"
# Set DEV_SKIP_PREFLIGHT=1 to skip the pre-call model health check.
DEV_SKIP_PREFLIGHT="${DEV_SKIP_PREFLIGHT:-0}"

CODER_PROFILE="$HOME/.aider-coder"
REVIEWER_PROFILE="$HOME/.aider-reviewer"

# The aider profile (aider.conf.yml + model-settings.yml + model-metadata.json)
# is config-only, so it mounts read-only at /conf. Containers run as a baked-in
# non-root user matching the host UID/GID (see Phase 6), so files stay
# host-owned and :ro mounts can't be written through.
CONTAINER_CONF="/conf"

# Endpoint the agent container uses to reach llama-swap on the host, plus the
# dummy key llama-swap ignores. aider routes any `openai/<model>` model through
# OPENAI_API_BASE.
OPENAI_API_BASE="${OPENAI_API_BASE:-http://host.docker.internal:8090/v1}"
OPENAI_API_KEY="${OPENAI_API_KEY:-local-llm}"

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
  if docker network inspect "$AGENT_NETWORK_NAME" >/dev/null 2>&1; then
    local properties
    properties="$(docker network inspect "$AGENT_NETWORK_NAME" \
      --format '{{.Driver}} {{.Internal}}' 2>/dev/null)" || return 1
    if [ "$properties" != "bridge false" ]; then
      echo "ERROR: Docker network '$AGENT_NETWORK_NAME' has unexpected properties: $properties" >&2
      echo "Expected a non-internal bridge; restricted mode fails closed." >&2
      return 1
    fi
    return 0
  fi
  # A plain user-defined bridge (NOT --internal): the agent must still reach the
  # host gateway for llama-swap. Isolation from the wider internet is enforced
  # by the host firewall rule in Phase 5, targeted at this network's subnet.
  docker network create --driver bridge "$AGENT_NETWORK_NAME" >/dev/null 2>&1 || {
    echo "ERROR: could not create the required Docker network '$AGENT_NETWORK_NAME'." >&2
    echo "Restricted mode fails closed; no agent container was started." >&2
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
      ensure_agent_network || return 1
      _net+=(--network "$AGENT_NETWORK_NAME")
      ;;
    default)
      : # default bridge; no extra flag
      ;;
    *)
      echo "ERROR: DEV_AGENT_NETWORK must be 'default' or 'restricted'; got '$DEV_AGENT_NETWORK'." >&2
      return 1
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
  command -v curl >/dev/null 2>&1 || {
    echo "ERROR: curl is required for the llama-swap preflight check." >&2
    echo "Install curl, or set DEV_SKIP_PREFLIGHT=1 for an explicit bypass." >&2
    return 1
  }
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


# Detect every Contracts and Golden test project. Generation commands require
# exactly one project of their tier, while read-only protection covers all of
# them so adding a second project never silently weakens the sandbox.
CONTRACTS_DIRS=()
GOLDEN_DIRS=()
enumerate_test_dirs() {
  local pattern="$1" output_name="$2" listing
  local -n output="$output_name"
  output=()
  [ -d "$WORKSPACE/tests" ] || return 0
  listing="$(mktemp "$TENNINETY_RUNTIME_DIR/test-directories.XXXXXX")" || return 1
  if ! find "$WORKSPACE/tests" -maxdepth 1 -type d -name "$pattern" -printf '%f\0' \
      | sort -z > "$listing"; then
    rm -f -- "$listing"
    return 1
  fi
  mapfile -d '' -t output < "$listing"
  local rc=$?
  rm -f -- "$listing"
  return "$rc"
}
enumerate_test_dirs '*.Contracts' CONTRACTS_DIRS || {
  echo "ERROR: could not enumerate Contracts test directories." >&2; exit 1; }
enumerate_test_dirs '*.Golden' GOLDEN_DIRS || {
  echo "ERROR: could not enumerate Golden test directories." >&2; exit 1; }
CONTRACTS_DIR="${CONTRACTS_DIRS[0]:-}"
GOLDEN_DIR="${GOLDEN_DIRS[0]:-}"

# Always read-only to agents: contract tests, the golden harness project, the
# fixture, the dependency manifest, and the frozen spec originals. Anything an
# agent must not silently rewrite is a real :ro mount, not just a chmod.
RO_MOUNTS=()
for test_dir in "${CONTRACTS_DIRS[@]}" "${GOLDEN_DIRS[@]}"; do
  [ -n "$test_dir" ] && RO_MOUNTS+=(-v "$WORKSPACE/tests/$test_dir:/workspace/tests/$test_dir:ro")
done
[ -d "$WORKSPACE/tests/fixtures" ] && RO_MOUNTS+=(-v "$WORKSPACE/tests/fixtures:/workspace/tests/fixtures:ro")
[ -f "$WORKSPACE/global.json" ] && RO_MOUNTS+=(-v "$WORKSPACE/global.json:/workspace/global.json:ro")
[ -f "$WORKSPACE/REVIEW_QUEUE.md" ] && RO_MOUNTS+=(-v "$WORKSPACE/REVIEW_QUEUE.md:/workspace/REVIEW_QUEUE.md:ro")
[ -d "$WORKSPACE/review-feedback" ] && RO_MOUNTS+=(-v "$WORKSPACE/review-feedback:/workspace/review-feedback:ro")
[ -f "$WORKSPACE/.agent/rules/architecture.md" ] && RO_MOUNTS+=(-v "$WORKSPACE/.agent/rules/architecture.md:/workspace/.agent/rules/architecture.md:ro")
[ -f "$WORKSPACE/.agent/rules/architecture.original.md" ] && RO_MOUNTS+=(-v "$WORKSPACE/.agent/rules/architecture.original.md:/workspace/.agent/rules/architecture.original.md:ro")

# MSBuild project and solution files are trusted scaffold/build-control inputs.
# A networked restore evaluates them, so implementation agents must never be
# able to change an existing one or create a replacement that reaches a gate.
build_control_listing="$(mktemp "$TENNINETY_RUNTIME_DIR/build-controls.XXXXXX")" || exit 1
if ! find "$WORKSPACE" \
    -path "$WORKSPACE/.git" -prune -o \
    -path '*/bin' -prune -o -path '*/obj' -prune -o \
    -type f \( -name '*.csproj' -o -name '*.fsproj' -o -name '*.vbproj' -o \
                 -name '*.sln' -o -name '*.slnx' -o -name '*.props' -o \
                 -name '*.targets' -o -name '*.rsp' -o -name '*.user' -o \
                 -name '.editorconfig' -o -iname 'NuGet.Config' \) \
    -print0 | sort -z > "$build_control_listing"; then
  rm -f -- "$build_control_listing"
  echo "ERROR: could not enumerate trusted build-control files." >&2
  exit 1
fi
while IFS= read -r -d '' build_file; do
  rel="${build_file#"$WORKSPACE/"}"
  RO_MOUNTS+=(-v "$build_file:/workspace/$rel:ro")
done < "$build_control_listing"
rm -f -- "$build_control_listing"

# --- aider invocation ----------------------------------------------------
# run_agent <profile> <mode: code|ask> <writable: rw|ro> <prompt> [file-specs...]
#
# File specs attach workspace files to the aider chat:
#   --edit:<workspace-relative path>   attach editable (code mode only)
#   --read:<workspace-relative path>   attach read-only context
# In ask mode every file is attached read-only regardless of the spec, and
# nonexistent paths are skipped (new manifest files are named in the prompt
# for the Coder to create).
run_agent() {
  local profile="$1" mode="$2" writable="$3" prompt="$4"
  shift 4

  [ -f "$profile/aider.conf.yml" ] || {
    echo "ERROR: aider profile not found at $profile – see SETUP_GUIDE.md Phase 7." >&2
    return 1
  }
  reject_ignored_build_controls || return 1

  local mount_args=()
  if [ "$writable" = "ro" ]; then
    # Reviewer: entire workspace read-only, INCLUDING .git. The orchestrator
    # is the sole owner of Git state.
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

  local file_args=() spec path
  for spec in "$@"; do
    case "$spec" in
      --edit:*|--read:*)
        path="${spec#--*:}"
        [ -f "$WORKSPACE/$path" ] || continue
        if [ "$mode" = "code" ] && [[ "$spec" == --edit:* ]]; then
          file_args+=("/workspace/$path")
        else
          file_args+=(--read "/workspace/$path")
        fi
        ;;
    esac
  done

  local mode_args=()
  [ "$mode" = "ask" ] && mode_args+=(--chat-mode ask)

  # The prompt travels as a mounted file: no quoting limits, no stdin.
  local prompt_file
  prompt_file="$(mktemp "${TMPDIR:-/tmp}/tenninety-prompt.XXXXXX")" || return 1
  if ! printf '%s\n' "$prompt" > "$prompt_file"; then
    rm -f -- "$prompt_file"
    return 1
  fi

  # GIT_OPTIONAL_LOCKS=0 stops even incidental .git writes (e.g. index refresh)
  # from a read-only .git mount. --no-git keeps aider itself away from Git.
  # History files are redirected to /tmp so the workspace stays clean.
  local net_args; agent_net_args net_args || { rm -f "$prompt_file"; return 1; }
  local rc=0
  docker run --rm --pull=never -i \
    -e GIT_OPTIONAL_LOCKS=0 \
    -e OPENAI_API_BASE="$OPENAI_API_BASE" \
    -e OPENAI_API_KEY="$OPENAI_API_KEY" \
    "${mount_args[@]}" \
    "${RO_MOUNTS[@]}" \
    -v "$profile:$CONTAINER_CONF:ro" \
    -v "$prompt_file:/task.md:ro" \
    "${net_args[@]}" \
    "$AGENT_IMAGE" \
      --config "$CONTAINER_CONF/aider.conf.yml" \
      --model-settings-file "$CONTAINER_CONF/model-settings.yml" \
      --model-metadata-file "$CONTAINER_CONF/model-metadata.json" \
      --message-file /task.md \
      --no-git \
      --yes-always \
      --chat-history-file /tmp/aider-chat-history.md \
      --input-history-file /tmp/aider-input-history \
      --llm-history-file /tmp/aider-llm-history.txt \
      "${mode_args[@]}" \
      "${file_args[@]}" \
      </dev/null || rc=$?
  rm -f "$prompt_file"
  return "$rc"
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
  if ! git -C "$WORKSPACE" add -N -- .; then
    echo "ERROR: could not make untracked files visible to the Git gates." >&2
    return 1
  fi
}

# --- Deterministic scope gate -------------------------------------------
# The module manifest in .agent/rules/architecture.md is the authoritative
# scope. Rather than trust the Reviewer model to catch out-of-scope edits,
# the orchestrator parses the manifest itself and hard-fails on any changed
# path not listed under the module's Implementation files / Shared
# integration files. Host-generated protected tests are admitted separately:
# implementation agents cannot write them because every such path is mounted
# read-only, but they must appear in the review diff and module commit.
#
# Manifest format (from the blueprint, rigidly specified):
#   **Module ID:** `invoice-calculator`
#   ### Implementation files
#   - `src/Project/Foo.cs` – ...
#   ### Shared integration files
#   - `src/Project/DependencyInjection.cs` – ...   (or the literal: None)

manifest_allowed_paths() {
  # manifest_allowed_paths <module-id> -> allowed paths, one per line.
  # Optional second argument reads the manifest from that Git ref. Scope and
  # agent attachment selection use the active baseline so an in-progress edit
  # to architecture.md cannot grant itself new writable paths.
  local module_id="$1" manifest_ref="${2:-}"
  local spec="$WORKSPACE/.agent/rules/architecture.md"
  [ -f "$spec" ] || return 0
  local content
  if [ -n "$manifest_ref" ]; then
    content="$(git -C "$WORKSPACE" show "$manifest_ref:.agent/rules/architecture.md" 2>/dev/null)" || return 1
  else
    content="$(cat "$spec")" || return 1
  fi
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
  ' <<< "$content"
}

manifest_protected_paths() {
  # manifest_protected_paths <module-id> -> host-generated protected test
  # artefact paths, one per line. The blueprint uses the first heading below;
  # the alternatives keep existing projects compatible with earlier wording.
  local module_id="$1" manifest_ref="${2:-}"
  local spec="$WORKSPACE/.agent/rules/architecture.md"
  [ -f "$spec" ] || return 0
  local content
  if [ -n "$manifest_ref" ]; then
    content="$(git -C "$WORKSPACE" show "$manifest_ref:.agent/rules/architecture.md" 2>/dev/null)" || return 1
  else
    content="$(cat "$spec")" || return 1
  fi
  awk -v id="$module_id" '
    /\*\*Module ID:\*\*[[:space:]]*`/ {
      line = $0
      sub(/^.*\*\*Module ID:\*\*[[:space:]]*`/, "", line)
      sub(/`.*$/, "", line)
      inblock = (line == id) ? 1 : 0
      grab = 0
      next
    }
    inblock && /^###[[:space:]]+(Protected\/generated test artefacts?|Protected contract-tests?|Protected contract-test paths?)([[:space:]]|$)/ { grab = 1; next }
    inblock && /^###[[:space:]]/ { grab = 0 }
    inblock && /^##[[:space:]]/  { inblock = 0; grab = 0 }
    grab && /^-[[:space:]]+`/ {
      p = $0
      sub(/^-[[:space:]]+`/, "", p)
      sub(/`.*$/, "", p)
      if (p != "" && tolower(p) != "none") print p
    }
  ' <<< "$content"
}

module_baseline_ref() {
  # Repairs run against a fresh baseline at the rejection commit. Initial
  # implementation falls back to the permanent module-start tag.
  local module_id="$1"
  local marker="$TENNINETY_RUNTIME_DIR/$module_id/active-baseline" marker_ref
  if [ -s "$marker" ]; then
    marker_ref="$(head -n 1 "$marker")" || marker_ref=""
    if [ -n "$marker_ref" ] \
        && git -C "$WORKSPACE" rev-parse -q --verify "$marker_ref^{commit}" >/dev/null; then
      echo "$marker_ref"
      return 0
    fi
  fi
  # Runtime state is intentionally disposable. Recover a repair baseline from
  # durable Git tags if the runtime marker was cleaned, corrupt, or moved.
  local repair
  repair="$(git -C "$WORKSPACE" tag --list "module-repair-$module_id-*" \
    --sort=-version:refname | head -n 1)" || return 1
  if [ -z "$repair" ]; then
    # If tag creation failed after a committed rejection, recover the durable
    # baseline from the orchestrator's exact commit subject.
    repair="$(git -C "$WORKSPACE" log -n 1 --format=%H \
      --grep="^review($module_id): reject attempt [0-9][0-9]*$" HEAD 2>/dev/null)" || return 1
  fi
  if [ -n "$repair" ]; then echo "$repair"; else echo "module-start-$module_id"; fi
}

diff_paths_from_ref() {
  # Print both the source and destination of renames/copies. `git diff
  # --name-only` prints only the destination, which can hide an out-of-scope
  # source path from scope and reset checks.
  local reference="${1:?reference required}"
  shift
  local listing status first second rc=0
  listing="$(mktemp "$TENNINETY_RUNTIME_DIR/diff-paths.XXXXXX")" || return 1
  if ! git -C "$WORKSPACE" diff --find-renames --find-copies --name-status -z \
      "$reference" -- "$@" > "$listing"; then
    rm -f -- "$listing"
    return 1
  fi
  while IFS= read -r -d '' status; do
    if ! IFS= read -r -d '' first; then rc=1; break; fi
    case "$status" in
      R*|C*)
        if ! IFS= read -r -d '' second; then rc=1; break; fi
        printf '%s\n%s\n' "$first" "$second"
        ;;
      *) printf '%s\n' "$first" ;;
    esac
  done < "$listing"
  rm -f -- "$listing"
  return "$rc"
}

module_diff_names() {
  local module_id="$1" baseline
  baseline="$(module_baseline_ref "$module_id")" || return 1
  diff_paths_from_ref "$baseline" . \
    ':(exclude)REVIEW_QUEUE.md' \
    ':(exclude)review-feedback/**' | sort -u
}

module_diff_text() {
  local module_id="$1" baseline
  baseline="$(module_baseline_ref "$module_id")" || return 1
  git -C "$WORKSPACE" diff --no-color "$baseline" -- . \
    ':(exclude)REVIEW_QUEUE.md' \
    ':(exclude)review-feedback/**'
}

is_build_control_path() {
  local path="${1,,}"
  case "$path" in
    *.csproj|*.fsproj|*.vbproj|*.sln|*.slnx|*.props|*.targets|*.rsp|*.user|nuget.config|*/nuget.config|global.json|.editorconfig|*/.editorconfig)
      return 0 ;;
  esac
  return 1
}

ignored_build_control_paths() {
  local path listing
  listing="$(mktemp "$TENNINETY_RUNTIME_DIR/ignored-controls.XXXXXX")" || return 1
  if ! git -C "$WORKSPACE" ls-files --others --ignored --exclude-standard -z > "$listing"; then
    rm -f -- "$listing"
    return 1
  fi
  while IFS= read -r -d '' path; do
    is_build_control_path "$path" && printf '%s\n' "$path"
  done < "$listing"
  local rc=$?
  rm -f -- "$listing"
  return "$rc"
}

reject_ignored_build_controls() {
  local ignored
  ignored="$(ignored_build_control_paths)" || return 1
  [ -z "$ignored" ] && return 0
  echo "IGNORED BUILD-CONTROL FILE(S) detected:" >&2
  printf '%s\n' "$ignored" | sed 's/^/  - /' >&2
  echo "Remove them or commit them in a separate reviewed scaffold change." >&2
  echo "Ignored build inputs cannot participate in a verified gate." >&2
  return 1
}

# Returns 0 if all changed paths are in scope, 1 otherwise (printing the
# offending paths). Prints nothing on success.
scope_check() {
  local module_id="$1"
  local baseline
  baseline="$(module_baseline_ref "$module_id")" || {
    echo "SCOPE ERROR: could not resolve the active baseline." >&2
    return 1
  }

  reject_ignored_build_controls || return 1

  if ! git -C "$WORKSPACE" rev-parse -q --verify "$baseline^{commit}" >/dev/null; then
    echo "SCOPE ERROR: baseline '$baseline' for module '$module_id' does not exist." >&2
    return 1
  fi

  # Include untracked new files in the comparison (agents don't commit).
  stage_untracked || return 1

  local allowed implementation_allowed protected
  allowed="$(manifest_allowed_paths "$module_id" "$baseline")" || {
    echo "SCOPE ERROR: could not read the baseline manifest from '$baseline'." >&2
    return 1
  }
  if [ -z "$allowed" ]; then
    echo "SCOPE ERROR: no Implementation/Shared files found for Module ID '$module_id' in .agent/rules/architecture.md." >&2
    echo "  Check the ID exists and its manifest lists paths under those headings." >&2
    return 1
  fi
  implementation_allowed="$allowed"
  protected="$(manifest_protected_paths "$module_id" "$baseline")" || {
    echo "SCOPE ERROR: could not read protected paths from '$baseline'." >&2
    return 1
  }

  # A manifest-declared protected artefact is required before review. This
  # turns contract-test presence into a deterministic gate rather than a model
  # suggestion. Modules without a public entry point may list None.
  local protected_path
  while IFS= read -r protected_path; do
    [ -n "$protected_path" ] || continue
    if [ ! -f "$WORKSPACE/$protected_path" ]; then
      echo "SCOPE ERROR: required protected test artefact is missing: $protected_path" >&2
      echo "Run 'dev.sh write-contract $module_id' before iterate." >&2
      return 1
    fi
  done <<EOF
$protected
EOF

  # architecture.md is the deliberate interface-change exception. Protected
  # test artefacts are host-generated and read-only to every agent invocation.
  allowed="$allowed
$protected
.agent/rules/architecture.md"
  if [ -n "$GOLDEN_DIR" ] && [ -f "$WORKSPACE/tests/$GOLDEN_DIR/CriticalLogicGoldenTests.cs" ]; then
    allowed="$allowed
tests/$GOLDEN_DIR/CriticalLogicGoldenTests.cs"
  fi

  local changed
  if ! changed="$(module_diff_names "$module_id" 2>&1)"; then
    echo "SCOPE ERROR: could not diff module '$module_id' against '$baseline':" >&2
    echo "$changed" >&2
    return 1
  fi
  if [ -z "$changed" ]; then
    echo "SCOPE ERROR: module '$module_id' has no implementation changes." >&2
    return 1
  fi

  local offenders="" build_control="" implementation_changed=0
  local path
  while IFS= read -r path; do
    [ -n "$path" ] || continue
    if is_build_control_path "$path"; then
      build_control="$build_control$path"$'\n'
    elif ! printf '%s\n' "$allowed" | grep -qxF "$path"; then
      offenders="$offenders$path"$'\n'
    elif printf '%s\n' "$implementation_allowed" | grep -qxF "$path"; then
      implementation_changed=1
    fi
  done <<EOF
$changed
EOF

  if [ -n "$build_control" ]; then
    echo "BUILD-CONTROL FILE(S) changed during module '$module_id':" >&2
    printf '%s' "$build_control" | sed 's/^/  - /' >&2
    echo "Project, solution, props and targets files are trusted restore inputs and are never agent-editable." >&2
    echo "Make this change manually in a separate reviewed commit before starting or repairing a module." >&2
    return 1
  fi

  if [ -n "$offenders" ]; then
    echo "OUT-OF-SCOPE FILE(S) for module '$module_id' (not in its manifest):" >&2
    printf '%s' "$offenders" | sed 's/^/  - /' >&2
    echo "  Allowed paths come from Implementation files, Shared integration files, and host-generated protected test artefacts." >&2
    echo "  Scope comes from the active baseline; an in-progress manifest edit cannot add paths." >&2
    echo "  For a deliberate new path, have a human commit the reviewed spec change before starting a new baseline." >&2
    return 1
  fi
  if [ "$implementation_changed" -ne 1 ]; then
    echo "SCOPE ERROR: '$module_id' changes only metadata or protected tests;" >&2
    echo "at least one manifest Implementation/Shared path must change." >&2
    return 1
  fi
  return 0
}

# --- Interface-change (spec drift) gate ---------------------------------
# The pre-commit signature-drift hook only checks that architecture.md was
# edited in the same diff as a signature change. That alone can be satisfied by
# the Coder itself editing both the signature and the spec in one pass, with no
# human in the loop. This gate makes a spec change a HUMAN decision: if a
# module's diff touches .agent/rules/architecture.md, finalise/commit refuse
# unless the human passes --allow-spec-change, and print the change against the
# frozen architecture.original.md so it can be reviewed deliberately.
spec_changed_in_module() {
  # spec_changed_in_module <module-id> -> 0 if architecture.md is in the diff.
  local module_id="$1" baseline
  baseline="$(module_baseline_ref "$module_id")" || return 2
  git -C "$WORKSPACE" rev-parse -q --verify "$baseline^{commit}" >/dev/null || return 2
  stage_untracked || return 2
  local changed
  changed="$(module_diff_names "$module_id")" || return 2
  printf '%s\n' "$changed" | grep -qx '.agent/rules/architecture.md'
}

# Enforce the human gate. Returns 0 to proceed, 1 to refuse.
require_spec_change_ack() {
  # require_spec_change_ack <module-id> <allow-flag: 0|1> <command-name>
  local module_id="$1" allow="$2" cmd="$3"
  spec_changed_in_module "$module_id"
  local spec_status=$?
  case "$spec_status" in
    0) : ;;
    1) return 0 ;;
    *)
      echo "ERROR: could not determine whether the architecture spec changed." >&2
      return 1 ;;
  esac

  if [ "$allow" = "1" ]; then
    echo "==> Interface change acknowledged (--allow-spec-change)."
    echo "    Per the interface change policy, this change requires frontier-model review."
    return 0
  fi

  echo "REFUSING $cmd: this module's diff changes .agent/rules/architecture.md (an interface change)." >&2
  echo "" >&2
  echo "Interface changes must be a deliberate human decision, not slipped in by the Coder." >&2
  local orig="$WORKSPACE/.agent/rules/architecture.original.md"
  if [ -f "$orig" ]; then
    echo "Change vs the frozen original (architecture.original.md):" >&2
    echo "------------------------------------------------------------" >&2
    ( cd "$WORKSPACE" && git --no-pager diff --no-index -- \
        .agent/rules/architecture.original.md \
        .agent/rules/architecture.md 2>/dev/null ) >&2 || true
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
# Gate markers live outside the workspace and record a CONTENT
# fingerprint, not a commit hash. Agents never commit, so HEAD does not move
# while a module is being built: a HEAD-based marker would compare equal to
# itself forever and could never detect that code changed after a gate passed.
# The fingerprint covers tracked modifications, staged content and untracked
# files, so any edit to the module invalidates every gate it already passed.

module_fingerprint() {
  # SHA-256 over the full working state relative to the module baseline.
  local state paths path digest
  state="$(mktemp "$TENNINETY_RUNTIME_DIR/fingerprint-state.XXXXXX")" || return 1
  paths="$(mktemp "$TENNINETY_RUNTIME_DIR/fingerprint-paths.XXXXXX")" || {
    rm -f -- "$state"; return 1;
  }
  if ! git -C "$WORKSPACE" diff --binary HEAD -- . \
      ':(exclude)REVIEW_QUEUE.md' \
      ':(exclude)review-feedback/**' > "$state"; then
    rm -f -- "$state" "$paths"
    return 1
  fi
  if ! git -C "$WORKSPACE" ls-files --others --exclude-standard -z > "$paths"; then
    rm -f -- "$state" "$paths"
    return 1
  fi
  while IFS= read -r -d '' path; do
    case "$path" in review-feedback/*) continue ;; esac
    printf '%s\0' "$path" >> "$state" || {
      rm -f -- "$state" "$paths"; return 1;
    }
    if [ -L "$WORKSPACE/$path" ]; then
      readlink -- "$WORKSPACE/$path" >> "$state" || {
        rm -f -- "$state" "$paths"; return 1;
      }
    elif ! cat -- "$WORKSPACE/$path" >> "$state"; then
      rm -f -- "$state" "$paths"
      return 1
    fi
  done < "$paths"
  digest="$(sha256sum -- "$state")" || {
    rm -f -- "$state" "$paths"; return 1;
  }
  rm -f -- "$state" "$paths"
  printf '%s\n' "${digest%% *}"
}

gate_dir() {
  local module_id="$1"
  mkdir -p "$TENNINETY_RUNTIME_DIR/$module_id/gates" || return 1
  printf '%s\n' "$TENNINETY_RUNTIME_DIR/$module_id/gates"
}

gate_pass() {
  # gate_pass <module-id> <gate-name> – record that this gate passed for the
  # current content fingerprint.
  local module_id="$1" gate="$2" directory marker temporary fingerprint
  reject_ignored_build_controls || return 1
  directory="$(gate_dir "$module_id")" || return 1
  marker="$directory/$gate"
  temporary="$(mktemp "$directory/.${gate}.XXXXXX")" || return 1
  fingerprint="$(module_fingerprint)" || {
    rm -f -- "$temporary"; return 1;
  }
  printf '%s\n' "$fingerprint" > "$temporary" || {
    rm -f -- "$temporary"; return 1;
  }
  mv -- "$temporary" "$marker"
}

gate_check() {
  # gate_check <module-id> <gate-name> – succeed only if the gate passed for
  # the CURRENT content. Any edit since then invalidates it.
  local module_id="$1" gate="$2"
  reject_ignored_build_controls >/dev/null || return 3
  local directory marker current
  directory="$(gate_dir "$module_id")" || return 4
  marker="$directory/$gate"
  [ -f "$marker" ] || return 1
  current="$(module_fingerprint)" || return 4
  [ "$(cat "$marker")" = "$current" ] || return 2
}

require_started() {
  local module_id="$1"
  if ! git -C "$WORKSPACE" rev-parse -q --verify "refs/tags/module-start-$module_id^{commit}" >/dev/null; then
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
    3) reject_ignored_build_controls; return 1 ;;
    *) echo "ERROR: could not verify the $what gate for $module_id."; return 1 ;;
  esac
}

cmd_start() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh start <module-id>"; return 1; }
  [[ "$module_id" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || {
    echo "ERROR: invalid Module ID '$module_id' (expected lowercase kebab-case)."; return 1; }
  [ -n "$(manifest_allowed_paths "$module_id")" ] || {
    echo "ERROR: Module ID '$module_id' has no manifest implementation/shared paths."; return 1; }
  reject_ignored_build_controls || return 1

  # Require an initial commit – the whole review architecture diffs against a
  # committed baseline.
  if ! git -C "$WORKSPACE" rev-parse HEAD >/dev/null 2>&1; then
    echo "ERROR: no commits yet. Commit the scaffold before starting a module."
    return 1
  fi

  # Require a clean tree so the module diff contains only this module's work
  # (this also catches leftover untracked files from a prior aborted run).
  local start_status
  if ! start_status="$(git -C "$WORKSPACE" status --porcelain)"; then
    echo "ERROR: could not inspect the working tree." >&2
    return 1
  fi
  if [ -n "$start_status" ]; then
    echo "ERROR: working tree is not clean. Commit, stash, or reset first:"
    git -C "$WORKSPACE" status --short
    return 1
  fi

  # Fail loudly if this module was already started – never silently reuse a tag.
  if git -C "$WORKSPACE" rev-parse -q --verify "refs/tags/module-start-$module_id" >/dev/null; then
    echo "ERROR: module '$module_id' already started (tag module-start-$module_id exists)."
    echo "Use 'dev.sh reset $module_id' to discard it, or use the correct Module ID from architecture.md."
    return 1
  fi

  # Record the exact base commit both as a tag and as a host-only baseline, so
  # later steps can diff against an unambiguous ref.
  local base
  base="$(git -C "$WORKSPACE" rev-parse HEAD)" || return 1
  git -C "$WORKSPACE" tag "module-start-$module_id" "$base" || return 1
  if ! mkdir -p "$TENNINETY_RUNTIME_DIR/$module_id" \
      || ! printf '%s\n' "$base" > "$TENNINETY_RUNTIME_DIR/$module_id/base-commit" \
      || ! printf '%s\n' "module-start-$module_id" > "$TENNINETY_RUNTIME_DIR/$module_id/active-baseline"; then
    # The tag is the durable source of truth; remove any partial marker so
    # module_baseline_ref falls back to it instead of trusting stale state.
    rm -f -- "$TENNINETY_RUNTIME_DIR/$module_id/active-baseline" 2>/dev/null || true
    echo "WARNING: external baseline markers could not be written; the durable start tag will be used." >&2
  fi
  echo "Started module '$module_id' at base commit ${base:0:12} (tag module-start-$module_id)."
}

# Build the file-spec list for a Coder invocation: the architecture spec and
# coder+tester skills (read-only), and every existing
# file in the module's manifest (editable). Manifest files that don't exist
# yet are skipped here and named in the task for the Coder to create.
coder_file_specs() {
  local module_id="$1" baseline paths
  printf '%s\n' \
    "--read:.agent/rules/architecture.md" \
    "--read:.agent/skills/coder.md" \
    "--read:.agent/skills/tester.md"
  baseline="$(module_baseline_ref "$module_id")" || return 1
  paths="$(manifest_allowed_paths "$module_id" "$baseline")" || return 1
  local p
  while IFS= read -r p; do
    [ -n "$p" ] || continue
    case "$p" in .agent/rules/architecture.md) continue ;; esac
    [ -f "$WORKSPACE/$p" ] && printf '%s\n' "--edit:$p"
  done <<EOF
$paths
EOF
}

reviewer_file_specs() {
  local module_id="$1" baseline allowed protected paths
  baseline="$(module_baseline_ref "$module_id")" || return 1
  printf '%s\n' \
    "--read:.agent/rules/architecture.md" \
    "--read:.agent/skills/reviewer.md" \
    "--read:review-feedback/$module_id.md"
  allowed="$(manifest_allowed_paths "$module_id" "$baseline")" || return 1
  protected="$(manifest_protected_paths "$module_id" "$baseline")" || return 1
  paths="$(printf '%s\n%s\n' "$allowed" "$protected" | sort -u)" || return 1
  local p
  while IFS= read -r p; do
    [ -n "$p" ] && [ -f "$WORKSPACE/$p" ] && printf '%s\n' "--read:$p"
  done <<EOF
$paths
EOF
}

# Run the Reviewer with the module diff inlined. aider runs single-shot and
# cannot run `git diff` itself, so the orchestrator (sole Git owner) generates
# the diff on the host – after stage_untracked, so new files appear in full –
# and embeds it in the prompt.
run_review() {
  # run_review <module-id>
  local module_id="$1"
  local baseline
  baseline="$(module_baseline_ref "$module_id")" || return 1
  local diff_text
  if ! git -C "$WORKSPACE" rev-parse -q --verify "$baseline^{commit}" >/dev/null; then
    echo "ERROR: reviewer baseline '$baseline' does not resolve to a commit." >&2
    return 1
  fi
  diff_text="$(module_diff_text "$module_id")" || return 1
  local prompt
  prompt="$(cat <<EOF
Read the attached .agent/skills/reviewer.md and review the module diff below against its checklist and the attached .agent/rules/architecture.md. The current full module files are also attached read-only. For a repair, the human rejection feedback is attached and the diff starts at that rejection baseline.

The diff was generated on the host against baseline '$baseline' (new files appear in full via intent-to-add). Host-generated protected test artefacts listed in the manifest are valid diff paths and must be reviewed, but implementation agents cannot edit them. You cannot run commands; this diff and the attached files are your complete evidence. End your response with a single verdict line that is exactly 'VERDICT: PASS' or 'VERDICT: FAIL' (list specific issues above it if it fails).

----- BEGIN MODULE DIFF -----
$diff_text
----- END MODULE DIFF -----
EOF
)"
  local specs=() spec_text
  spec_text="$(reviewer_file_specs "$module_id")" || return 1
  mapfile -t specs <<< "$spec_text"
  run_agent "$REVIEWER_PROFILE" ask ro "$prompt" "${specs[@]}"
}

cmd_write() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo 'usage: dev.sh write <module-id> "<task>"'; return 1; }
  shift || true
  local task="$*"
  [ -n "$task" ] || { echo 'usage: dev.sh write <module-id> "<task>"'; return 1; }
  require_started "$module_id" || return 1
  preflight_llama_swap || return 1
  local specs=() spec_text
  spec_text="$(coder_file_specs "$module_id")" || return 1
  mapfile -t specs <<< "$spec_text"
  run_agent "$CODER_PROFILE" code rw "$(broadcast_prefix)$task" "${specs[@]}"
}

cmd_review() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh review <module-id>"; return 1; }
  require_started "$module_id" || return 1
  local scope_out
  if ! scope_out="$(scope_check "$module_id" 2>&1)"; then
    echo "$scope_out"
    echo "VERDICT: FAIL"
    return 1
  fi
  preflight_llama_swap || return 1
  run_review "$module_id"
}

cmd_test() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh test <module-id>"; return 1; }
  require_started "$module_id" || return 1
  # Scope-gate before the test container touches the tree: a bare `dev.sh test`
  # after `dev.sh write` would otherwise restore/build an unvetted tree.
  local scope_out
  if ! scope_out="$(scope_check "$module_id" 2>&1)"; then
    echo "$scope_out" >&2
    return 1
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
  local module_id="${1:-}"
  # The original task is IMMUTABLE. Each retry sends the original task plus
  # ONLY the latest feedback (review findings or test log) – we never nest
  # the previous prompt inside the next one, which would grow the context
  # every iteration and work against the token-saving goal.
  local original_task="${2:-}"
  [ -n "$module_id" ] && [ -n "$original_task" ] || {
    echo 'usage: dev.sh iterate <module-id> "<task>"'; return 1; }
  require_started "$module_id" || return 1
  local feedback=""
  local attempt=0
  local max_attempts=3

  # Keep injected feedback bounded so a huge log can't blow up the prompt.
  local FEEDBACK_MAX_LINES="${DEV_FEEDBACK_MAX_LINES:-120}"

  # Fail fast if the model server is down, rather than hanging on the first
  # Coder call with a silent terminal.
  preflight_llama_swap || return 1

  mkdir -p "$TENNINETY_RUNTIME_DIR/$module_id"
  local test_log="$TENNINETY_RUNTIME_DIR/$module_id/latest-test.log"

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
    # Recompute the attachment set every attempt: files the Coder created in a
    # previous attempt now exist and must be attached editable to be edited.
    local specs=() spec_text
    spec_text="$(coder_file_specs "$module_id")" || return 1
    mapfile -t specs <<< "$spec_text"
    local write_out
    write_out=$(run_agent "$CODER_PROFILE" code rw "$(broadcast_prefix)$task" "${specs[@]}" 2>&1) || true
    echo "$write_out"

    if echo "$write_out" | grep -qiE "spec is ambiguous|architecture\.md doesn't say|I don't know (whether|if)|the specification doesn't say"; then
      echo ""
      echo "WARNING: Spec gap suspected – inspect the Coder output above."
      echo "  Consider revising .agent/rules/architecture.md before the next iteration."
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
      local baseline
      baseline="$(module_baseline_ref "$module_id")" || return 1
      feedback="SCOPE FAILED. You changed files outside this module's manifest. Delete any newly-created out-of-scope files, and for a modified tracked file restore it with 'git show $baseline:<path> > <path>' (your .git is read-only, so 'git checkout' will fail). Only touch the paths listed under this module's Implementation files / Shared integration files:
$(echo "$scope_out" | tail -n "$FEEDBACK_MAX_LINES")"
      continue
    fi

    echo ""
    echo "==> Pass $attempt: REVIEW"
    # Files were already staged by scope_check so the reviewer's diff sees them.
    local review_out
    review_out=$(run_review "$module_id" 2>&1) || true
    echo "$review_out"

    # Fail-closed: only an exactly-once 'VERDICT: PASS' line counts. A pass
    # verdict quoted earlier in prose, in the echoed prompt, or inside a code
    # comment in the diff cannot wave the module through; a missing, duplicated
    # or garbled verdict fails.
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
      gate_pass "$module_id" reviewed || return 1
      gate_pass "$module_id" fast-tests || return 1
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
  echo "  (4) Revise .agent/rules/architecture.md for this module"
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
  local module_id="${1:-}"
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

  # The integration runner is required to leave protected and production
  # content unchanged. Re-check both the fast-tier fingerprint and scope here
  # so a broken/custom runner cannot stamp altered content as finalised.
  require_gate "$module_id" fast-tests "review and the fast test tier" \
    "dev.sh iterate $module_id \"<task>\"" || return 1
  if ! scope_check "$module_id"; then
    echo "Integration tests left the module outside its approved scope." >&2
    return 1
  fi

  echo ""
  echo "==> Downstream interface-drift check"
  # A crash in the drift tool must not be read as "no interface changed".
  local baseline
  baseline="$(module_baseline_ref "$module_id")" || return 1
  if ! (cd "$WORKSPACE" && ./scripts/check_interface_drift.sh "$module_id" "$baseline"); then
    echo "ERROR: the interface-drift check failed to run."
    echo "Fix the drift tooling before finalising – a broken checker cannot be"
    echo "treated as 'no public API changes'."
    return 1
  fi

  gate_pass "$module_id" integration || return 1
  echo "Finalised $module_id. Now run 'dev.sh commit $module_id'."
}

cmd_queue() {
  local module_id="${1:-}" status
  [ -n "$module_id" ] || { echo "usage: dev.sh queue <module-id>"; return 1; }
  require_started "$module_id" || return 1

  status="$(queue_status_for "$module_id")"
  case "$status" in
    ""|needs-fixes|interface-changed) : ;;
    ready-for-review)
      echo "ERROR: module '$module_id' is already ready for review." >&2
      return 1 ;;
    approved)
      echo "ERROR: module '$module_id' is approved and cannot be reopened by queue." >&2
      return 1 ;;
    *)
      echo "ERROR: module '$module_id' has unknown queue status '$status'." >&2
      return 1 ;;
  esac

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
  if ! git -C "$WORKSPACE" add REVIEW_QUEUE.md; then
    git -C "$WORKSPACE" restore --worktree -- REVIEW_QUEUE.md 2>/dev/null || true
    echo "ERROR: could not stage the queue update; REVIEW_QUEUE.md was restored." >&2
    return 1
  fi
  if ! git -C "$WORKSPACE" diff --cached --quiet 2>/dev/null; then
    if ! git -C "$WORKSPACE" commit -q -m "queue($module_id): ready for review"; then
      git -C "$WORKSPACE" restore --staged --worktree -- REVIEW_QUEUE.md 2>/dev/null || true
      echo "ERROR: could not commit the queue update; REVIEW_QUEUE.md was restored." >&2
      return 1
    fi
  fi
  echo "Queued $module_id. Working tree is clean; you can start the next module."
}

cmd_commit() {
  # The orchestrator owns all Git state: agents never commit. This is the step
  # that turns a finalised module into a fixed artefact and returns the tree to
  # a clean state so the NEXT module can start.
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh commit <module-id> [--allow-spec-change]"; return 1; }
  shift || true
  local allow_spec=0
  for arg in "$@"; do
    [ "$arg" = "--allow-spec-change" ] && allow_spec=1
  done
  require_started "$module_id" || return 1
  require_spec_change_ack "$module_id" "$allow_spec" "dev.sh commit" || return 1
  require_gate "$module_id" reviewed "review" \
    "dev.sh iterate $module_id \"<task>\"" || return 1
  require_gate "$module_id" fast-tests "the fast test tier" \
    "dev.sh iterate $module_id \"<task>\"" || return 1
  require_gate "$module_id" integration "the integration tier ('finalise')" \
    "dev.sh finalise $module_id" || return 1
  scope_check "$module_id" || return 1

  # Runtime state is outside the workspace, so every workspace change is part
  # of the candidate commit.
  (cd "$WORKSPACE" && git add -A -- . >/dev/null 2>&1) || {
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
  gate_pass "$module_id" reviewed || return 1
  gate_pass "$module_id" fast-tests || return 1
  gate_pass "$module_id" integration || return 1
  gate_pass "$module_id" committed || return 1
  echo "Now run 'dev.sh queue $module_id'."
}

cmd_fix() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh fix <module-id>"; return 1; }
  [ "$(queue_status_for "$module_id")" = "needs-fixes" ] || {
    echo "ERROR: module '$module_id' is not in needs-fixes state." >&2
    return 1
  }
  (cd "$WORKSPACE" && ./scripts/apply_review_feedback.sh "$module_id")
  local rc=$?
  if [ $rc -ne 0 ]; then
    echo ""
    echo "Repair failed; downstream queue state was not changed."
    echo "Interface propagation runs only after a successful integration finalise."
  fi
  # Propagate the real result: a failed fix must not look like success.
  return $rc
}

cmd_escalate() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh escalate <module-id> [log] [--override] [--write-code]"; return 1; }
  require_started "$module_id" || return 1
  (cd "$WORKSPACE" && python scripts/escalate.py "$@")
}

queue_status_for() {
  local module_id="$1"
  awk -F'|' -v m=" $module_id " '$2==m {gsub(/^ +| +$/, "", $3); print $3; exit}' \
    "$WORKSPACE/REVIEW_QUEUE.md" 2>/dev/null
}

require_review_decision_ready() {
  local module_id="$1" status row_count
  [ -f "$WORKSPACE/REVIEW_QUEUE.md" ] || {
    echo "ERROR: REVIEW_QUEUE.md is missing." >&2
    return 1
  }
  row_count="$(awk -F'|' -v m=" $module_id " '$2==m {count++} END {print count+0}' \
    "$WORKSPACE/REVIEW_QUEUE.md")" || {
      echo "ERROR: could not read REVIEW_QUEUE.md." >&2
      return 1
    }
  [ "$row_count" -eq 1 ] || {
    echo "ERROR: expected exactly one review row for '$module_id'; found $row_count." >&2
    return 1
  }
  status="$(queue_status_for "$module_id")"
  [ "$status" = "ready-for-review" ] || {
    echo "ERROR: module '$module_id' is not ready for review (status: ${status:-missing})." >&2
    return 1
  }
  local dirty
  if ! dirty="$(git -C "$WORKSPACE" status --porcelain)"; then
    echo "ERROR: could not inspect the working tree before review decision." >&2
    return 1
  fi
  [ -z "$dirty" ] || {
    echo "ERROR: the working tree must be clean before recording a review decision:" >&2
    printf '%s\n' "$dirty" >&2
    return 1
  }
}

cmd_reject() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh reject <module-id> \"<feedback>\""; return 1; }
  shift
  local feedback="$*"
  [ -n "$feedback" ] || { echo "usage: dev.sh reject <module-id> \"<feedback>\""; return 1; }
  require_review_decision_ready "$module_id" || return 1
  # Increment the rejection count in the same edit that sets the status, so the
  # documented three-strikes rule can actually fire.
  local count
  count="$(awk -F'|' -v m=" $module_id " '$2==m {gsub(/ /,"",$4); print $4}' \
    "$WORKSPACE/REVIEW_QUEUE.md")" || {
      echo "ERROR: could not read the rejection counter." >&2
      return 1
    }
  [[ "$count" =~ ^[0-9]+$ ]] || {
    echo "ERROR: invalid rejection counter '$count' for '$module_id'." >&2
    return 1
  }
  if [ "$count" -ge 3 ]; then
    echo "ERROR: '$module_id' has already been rejected three times." >&2
    echo "Revise its specification before another implementation attempt." >&2
    return 1
  fi
  count=$((count + 1))
  local repair_tag="module-repair-$module_id-$count"
  if git -C "$WORKSPACE" rev-parse -q --verify "refs/tags/$repair_tag" >/dev/null; then
    echo "ERROR: repair baseline tag '$repair_tag' already exists." >&2
    return 1
  fi

  # Validate every refusal condition before writing either metadata file.
  local feedback_was_tracked=0
  git -C "$WORKSPACE" ls-files --error-unmatch "review-feedback/$module_id.md" >/dev/null 2>&1 \
    && feedback_was_tracked=1
  mkdir -p "$WORKSPACE/review-feedback" || return 1
  if ! printf '%s\n' "$feedback" > "$WORKSPACE/review-feedback/$module_id.md"; then
    if [ "$feedback_was_tracked" = "1" ]; then
      git -C "$WORKSPACE" restore --worktree -- "review-feedback/$module_id.md" 2>/dev/null || true
    else
      rm -f -- "$WORKSPACE/review-feedback/$module_id.md"
    fi
    echo "ERROR: could not write rejection feedback." >&2
    return 1
  fi
  if ! sed -i "s/| $module_id | [^|]* | [^|]* |/| $module_id | needs-fixes | $count |/" \
      "$WORKSPACE/REVIEW_QUEUE.md" \
      || ! grep -q "^| $module_id | needs-fixes | $count |" "$WORKSPACE/REVIEW_QUEUE.md"; then
    git -C "$WORKSPACE" restore --worktree -- REVIEW_QUEUE.md 2>/dev/null || true
    [ "$feedback_was_tracked" = "1" ] \
      && git -C "$WORKSPACE" restore --worktree -- "review-feedback/$module_id.md" 2>/dev/null \
      || rm -f -- "$WORKSPACE/review-feedback/$module_id.md"
    echo "ERROR: could not update rejection metadata." >&2
    return 1
  fi

  # Commit review metadata immediately so the tree stays usable. A fresh repair
  # baseline at this commit isolates the fix from both the original module
  # implementation and every module committed since it was first queued.
  if ! git -C "$WORKSPACE" add REVIEW_QUEUE.md "review-feedback/$module_id.md"; then
    git -C "$WORKSPACE" restore --staged --worktree -- REVIEW_QUEUE.md 2>/dev/null || true
    if [ "$feedback_was_tracked" = "1" ]; then
      git -C "$WORKSPACE" restore --staged --worktree -- "review-feedback/$module_id.md" 2>/dev/null || true
    else
      git -C "$WORKSPACE" restore --staged -- "review-feedback/$module_id.md" 2>/dev/null || true
      rm -f -- "$WORKSPACE/review-feedback/$module_id.md"
    fi
    echo "ERROR: could not stage rejection metadata; review files were restored." >&2
    return 1
  fi
  if ! git -C "$WORKSPACE" commit -q -m "review($module_id): reject attempt $count"; then
    git -C "$WORKSPACE" restore --staged --worktree -- REVIEW_QUEUE.md 2>/dev/null || true
    if [ "$feedback_was_tracked" = "1" ]; then
      git -C "$WORKSPACE" restore --staged --worktree -- "review-feedback/$module_id.md" 2>/dev/null || true
    else
      git -C "$WORKSPACE" restore --staged -- "review-feedback/$module_id.md" 2>/dev/null || true
      rm -f "$WORKSPACE/review-feedback/$module_id.md"
    fi
    echo "ERROR: could not commit rejection metadata; review files were restored." >&2
    return 1
  fi
  # A successfully rejected module must re-earn every gate.
  rm -f "$TENNINETY_RUNTIME_DIR/$module_id/gates/"* 2>/dev/null || true
  local repair_commit
  repair_commit="$(git -C "$WORKSPACE" rev-parse HEAD)" || return 1
  if ! git -C "$WORKSPACE" tag "$repair_tag" "$repair_commit"; then
    echo "WARNING: could not create '$repair_tag'; using commit $repair_commit as the repair baseline." >&2
    repair_tag="$repair_commit"
  fi
  if ! mkdir -p "$TENNINETY_RUNTIME_DIR/$module_id" \
      || ! printf '%s\n' "$repair_tag" > "$TENNINETY_RUNTIME_DIR/$module_id/active-baseline" \
      || ! printf '%s\n' "$repair_commit" > "$TENNINETY_RUNTIME_DIR/$module_id/base-commit"; then
    rm -f -- "$TENNINETY_RUNTIME_DIR/$module_id/active-baseline" 2>/dev/null || true
    echo "WARNING: external baseline markers could not be updated; the durable repair tag/commit will be used." >&2
  fi
  echo "Rejected $module_id (rejections: $count) – feedback in review-feedback/$module_id.md"
  if [ "$count" -ge 3 ]; then
    echo ""
    echo "NOTE: $module_id has now been rejected $count times. Per the working"
    echo "guide, stop asking for another fix – revise this module's section of"
    echo "architecture.md with the frontier model instead."
  fi
}

cmd_approve() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh approve <module-id>"; return 1; }
  require_review_decision_ready "$module_id" || return 1
  if ! sed -i "s/| $1 | [^|]* |/| $1 | approved |/" "$WORKSPACE/REVIEW_QUEUE.md" \
      || ! grep -q "^| $module_id | approved |" "$WORKSPACE/REVIEW_QUEUE.md"; then
    git -C "$WORKSPACE" restore --worktree -- REVIEW_QUEUE.md 2>/dev/null || true
    echo "ERROR: could not update approval metadata." >&2
    return 1
  fi
  if ! git -C "$WORKSPACE" add REVIEW_QUEUE.md; then
    git -C "$WORKSPACE" restore --staged --worktree -- REVIEW_QUEUE.md 2>/dev/null || true
    echo "ERROR: could not stage approval metadata; REVIEW_QUEUE.md was restored." >&2
    return 1
  fi
  if ! git -C "$WORKSPACE" diff --cached --quiet; then
    if ! git -C "$WORKSPACE" commit -q -m "review($module_id): approve"; then
      git -C "$WORKSPACE" restore --staged --worktree -- REVIEW_QUEUE.md 2>/dev/null || true
      echo "ERROR: could not commit approval metadata; REVIEW_QUEUE.md was restored." >&2
      return 1
    fi
  fi
  echo "Approved $module_id"
}

cmd_status() {
  echo "=== HOST RUNTIME ==="
  echo "$TENNINETY_RUNTIME_DIR"
  echo ""
  echo "=== REVIEW QUEUE ==="
  cat "$WORKSPACE/REVIEW_QUEUE.md" 2>/dev/null || echo "(empty)"
  echo ""
  echo "=== ESCALATIONS ==="
  cat "$TENNINETY_RUNTIME_DIR/escalations.json" 2>/dev/null || echo "(none)"
  echo ""
  echo "=== RECENT COMMITS ==="
  git -C "$WORKSPACE" log --oneline -5 2>/dev/null || echo "(no commits)"
  echo ""
  echo "=== BROADCAST ==="
  cat "$WORKSPACE/BROADCAST.md" 2>/dev/null || echo "(none)"
}

cmd_runtime_path() {
  local module_id="${1:-}"
  if [ -z "$module_id" ]; then
    echo "$TENNINETY_RUNTIME_DIR"
    return 0
  fi
  [[ "$module_id" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || {
    echo "ERROR: invalid Module ID '$module_id'." >&2; return 1; }
  echo "$TENNINETY_RUNTIME_DIR/$module_id"
}

# --- Manifest coverage check --------------------------------------------
# Every implementation file under src/ should belong to at least one module
# manifest (Implementation files / Shared integration files). This was a rule
# for the frontier model to uphold; make it a mechanical check so an orphaned
# file — code that no module owns and no scope gate protects — is caught.
# Prints orphaned paths and returns 1 if any exist.
all_manifest_paths() {
  local spec="$WORKSPACE/.agent/rules/architecture.md"
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

  local allowed
  allowed="$(all_manifest_paths)" || {
    echo "ERROR: could not read the architecture manifest." >&2
    return 1
  }
  if [ -z "$allowed" ]; then
    echo "WARNING: no manifest paths found in .agent/rules/architecture.md; cannot check coverage." >&2
    return 0
  fi

  # Every tracked C# source file under src/ (exclude generated/obj/bin).
  local orphans="" listing
  listing="$(mktemp "$TENNINETY_RUNTIME_DIR/coverage-paths.XXXXXX")" || return 1
  if ! git -C "$WORKSPACE" ls-files -z -- 'src/**/*.cs' > "$listing"; then
    rm -f -- "$listing"
    echo "ERROR: could not enumerate tracked C# source files." >&2
    return 1
  fi
  while IFS= read -r -d '' f; do
    [ -n "$f" ] || continue
    case "$f" in */obj/*|*/bin/*) continue ;; esac
    if ! printf '%s\n' "$allowed" | grep -qxF "$f"; then
      orphans="$orphans$f"$'\n'
    fi
  done < "$listing"
  rm -f -- "$listing"

  if [ -n "$orphans" ]; then
    echo "MANIFEST COVERAGE: these src/ files are not listed in any module manifest:" >&2
    printf '%s' "$orphans" | sed 's/^/  - /' >&2
    echo "Add each to a module's Implementation files / Shared integration files, or remove it." >&2
    return 1
  fi
  echo "Manifest coverage OK: every tracked src/*.cs file belongs to a module manifest."
  return 0
}

reset_scope_check() {
  # Reset is intentionally workspace-wide at the Git plumbing level, so first
  # prove that every change it would discard belongs to this module. This keeps
  # an unrelated scratch edit from being swept into a module reset.
  local module_id="$1" baseline="$2" allowed protected changed offenders="" path
  allowed="$(manifest_allowed_paths "$module_id" "$baseline")" || return 1
  protected="$(manifest_protected_paths "$module_id" "$baseline")" || return 1
  allowed="$allowed
$protected
.agent/rules/architecture.md"
  if [ -n "$GOLDEN_DIR" ] && [ -f "$WORKSPACE/tests/$GOLDEN_DIR/CriticalLogicGoldenTests.cs" ]; then
    allowed="$allowed
tests/$GOLDEN_DIR/CriticalLogicGoldenTests.cs"
  fi
  stage_untracked || return 1
  changed="$(diff_paths_from_ref HEAD . | sort -u)" || return 1
  while IFS= read -r path; do
    [ -n "$path" ] || continue
    if ! printf '%s\n' "$allowed" | grep -qxF "$path"; then
      offenders="$offenders$path"$'\n'
    fi
  done <<EOF
$changed
EOF
  if [ -n "$offenders" ]; then
    echo "ERROR: reset would also discard changes outside module '$module_id':" >&2
    printf '%s' "$offenders" | sed 's/^/  - /' >&2
    echo "Commit, stash, or remove those changes before resetting the module." >&2
    return 1
  fi
}

cmd_reset() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh reset <module-id>"; return 1; }
  require_started "$module_id" || return 1
  local baseline
  baseline="$(module_baseline_ref "$module_id")" || return 1

  # A queued/committed module is not an uncommitted workspace operation. Using
  # reset --hard against its historical tag would roll back unrelated later
  # commits. Such modules must go through reject/fix (or an explicit Git revert).
  local start_commit head_commit
  start_commit="$(git -C "$WORKSPACE" rev-parse "module-start-$module_id^{commit}")" || return 1
  head_commit="$(git -C "$WORKSPACE" rev-parse HEAD)" || return 1
  if [[ "$baseline" == module-start-* ]] \
     && { [ "$head_commit" != "$start_commit" ] \
          || gate_check "$module_id" committed 2>/dev/null \
          || grep -q "^| $module_id |" "$WORKSPACE/REVIEW_QUEUE.md" 2>/dev/null; }; then
    echo "ERROR: '$module_id' is already committed or queued; reset is only for uncommitted work." >&2
    echo "HEAD has advanced beyond its start point, so history is left untouched." >&2
    echo "Use 'dev.sh reject' + 'dev.sh fix', or create an explicit Git revert." >&2
    return 1
  fi

  reset_scope_check "$module_id" "$baseline" || return 1

  # Save a recoverable snapshot of current HEAD plus all uncommitted work.
  local backup_dir="$TENNINETY_RUNTIME_DIR/reset-backups"
  mkdir -p "$backup_dir" || return 1
  local stamp; stamp="$(date +%Y%m%d-%H%M%S)"
  local bundle="$backup_dir/${module_id}-${stamp}.bundle"
  local patch="$backup_dir/${module_id}-${stamp}.uncommitted.patch"
  # The patch is against current HEAD, not the historical module-start tag, so
  # it contains only work that this reset will actually discard.
  if ! git -C "$WORKSPACE" bundle create "$bundle" HEAD >/dev/null 2>&1; then
    echo "ERROR: could not create the reset Git bundle; nothing was discarded." >&2
    return 1
  fi
  if ! git -C "$WORKSPACE" diff --binary HEAD > "$patch"; then
    echo "ERROR: could not create the reset patch; nothing was discarded." >&2
    return 1
  fi
  # Also snapshot untracked files as a tar so nothing is silently lost.
  if ! ( set -o pipefail; cd "$WORKSPACE" && git ls-files --others --exclude-standard -z \
      | tar --null --verbatim-files-from -T - \
          -czf "$backup_dir/${module_id}-${stamp}.untracked.tar.gz" ); then
    echo "ERROR: could not archive untracked files; nothing was discarded." >&2
    return 1
  fi
  echo "Safety backup saved under $backup_dir/${module_id}-${stamp}.* (recover with 'git apply' / 'git bundle')."

  # Restore the current committed branch state. This never moves HEAD, so later
  # module and review commits are preserved. Then remove module-created
  # untracked files while retaining ignored build output. Reset backups are
  # host state outside this tree.
  git -C "$WORKSPACE" reset --hard HEAD || return 1
  git -C "$WORKSPACE" clean -fd || return 1

  if [[ "$baseline" == module-start-* ]]; then
    git -C "$WORKSPACE" tag -d "module-start-$module_id" >/dev/null 2>&1 || true
    rm -rf "$TENNINETY_RUNTIME_DIR/$module_id"
    echo "Reset uncommitted module '$module_id' to current HEAD and removed its start tag."
  else
    rm -f "$TENNINETY_RUNTIME_DIR/$module_id/gates/"* 2>/dev/null || true
    echo "Reset repair work for '$module_id' to current HEAD; the rejection and repair baseline remain active."
  fi
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
  # separate writable /staging directory as aider's working directory. During
  # generation the agent cannot write anywhere in the workspace at all – not
  # src/, not other tests, not the existing contract suite. The host then
  # validates what was produced and moves it into place, read-only.
  #
  # Usage: stage_llm_test_files <destination_dir> <task> [exact_name] [expected_names]
  #
  # Name the third argument to require one exact file and nothing else. The
  # fourth argument is a newline-delimited exact filename set from the module
  # manifest; the agent never gets to choose additional names.
  #
  # Every staged file must be named <Type>Tests.cs and must not already exist:
  # the write-once guarantee is enforced per file, so a module can gain a test
  # for a new entry point later without ever overwriting an existing one. The
  # whole batch is validated before anything moves, so a rejected batch leaves
  # the destination untouched.
  local destination_dir="$1" task="$2" exact_name="${3:-}" expected_names="${4:-}"
  preflight_llama_swap || return 1
  [ -f "$CODER_PROFILE/aider.conf.yml" ] || {
    echo "ERROR: aider profile not found at $CODER_PROFILE – see SETUP_GUIDE.md Phase 7." >&2
    return 1
  }
  [ -d "$destination_dir" ] || {
    echo "ERROR: staged-test destination does not exist: $destination_dir" >&2
    return 1
  }
  local staging
  staging="$(mktemp -d "$TENNINETY_RUNTIME_DIR/test-staging.XXXXXX")" || return 1

  local prompt_file
  prompt_file="$(mktemp "${TMPDIR:-/tmp}/tenninety-prompt.XXXXXX")" || {
    rm -rf -- "$staging"
    return 1
  }
  if ! printf '%s\n' "$task" > "$prompt_file"; then
    rm -f -- "$prompt_file"
    rm -rf -- "$staging"
    return 1
  fi

  local net_args
  if ! agent_net_args net_args; then
    rm -f "$prompt_file"
    rm -rf "$staging"
    return 1
  fi
  local rc=0
  docker run --rm --pull=never -i \
    -e OPENAI_API_BASE="$OPENAI_API_BASE" \
    -e OPENAI_API_KEY="$OPENAI_API_KEY" \
    -w /staging \
    -v "$WORKSPACE:/workspace:ro" \
    -v "$staging:/staging:rw" \
    -v "$CODER_PROFILE:$CONTAINER_CONF:ro" \
    -v "$prompt_file:/task.md:ro" \
    "${net_args[@]}" \
    "$AGENT_IMAGE" \
      --config "$CONTAINER_CONF/aider.conf.yml" \
      --model-settings-file "$CONTAINER_CONF/model-settings.yml" \
      --model-metadata-file "$CONTAINER_CONF/model-metadata.json" \
      --message-file /task.md \
      --no-git \
      --yes-always \
      --chat-history-file /tmp/aider-chat-history.md \
      --input-history-file /tmp/aider-input-history \
      --llm-history-file /tmp/aider-llm-history.txt \
      --read /workspace/.agent/rules/architecture.md \
      --read /workspace/.agent/skills/coder.md \
      </dev/null || rc=$?
  rm -f "$prompt_file"
  if [ "$rc" -ne 0 ]; then rm -rf "$staging"; return "$rc"; fi

  local generated=() generated_listing
  generated_listing="$(mktemp "$TENNINETY_RUNTIME_DIR/generated-tests.XXXXXX")" || {
    rm -rf -- "$staging"
    return 1
  }
  if ! find "$staging" -type f -name '*.cs' -print0 | sort -z > "$generated_listing"; then
    rm -f -- "$generated_listing"
    rm -rf -- "$staging"
    return 1
  fi
  while IFS= read -r -d '' f; do generated+=("$f"); done < "$generated_listing"
  rm -f -- "$generated_listing"

  local unexpected
  unexpected="$(find "$staging" -mindepth 1 \
    \( ! -type f -o ! -name '*.cs' \) -printf '%P\n' | sort)" || {
      rm -rf -- "$staging"
      return 1
    }
  if [ -n "$unexpected" ]; then
    echo "ERROR: the agent staged directories, links, or non-C# files:" >&2
    printf '%s\n' "$unexpected" | sed 's/^/  - /' >&2
    rm -rf "$staging"
    return 1
  fi

  if [ "${#generated[@]}" -eq 0 ]; then
    echo "ERROR: the agent staged no .cs files in /staging."
    rm -rf "$staging"
    return 1
  fi

  # Validate every staged file BEFORE moving any of them.
  local f base
  if [ -n "$expected_names" ]; then
    local expected_count
    expected_count="$(printf '%s\n' "$expected_names" | sed '/^$/d' | wc -l)"
    if [ "${#generated[@]}" -ne "$expected_count" ]; then
      echo "ERROR: expected $expected_count manifest-declared contract files; got ${#generated[@]}." >&2
      echo "Expected:" >&2
      printf '%s\n' "$expected_names" | sed '/^$/d; s/^/  - /' >&2
      echo "Generated:" >&2
      find "$staging" -type f -printf '  - %P\n' >&2
      rm -rf "$staging"
      return 1
    fi
  fi
  for f in "${generated[@]}"; do
    base=$(basename "$f")
    if [ "$(dirname "$f")" != "$staging" ]; then
      echo "ERROR: staged files must be directly under /staging, not in subdirectories: ${f#"$staging/"}" >&2
      rm -rf "$staging"
      return 1
    fi
    if [ -n "$exact_name" ] && { [ "${#generated[@]}" -ne 1 ] || [ "$base" != "$exact_name" ]; }; then
      echo "ERROR: expected exactly /staging/$exact_name; got:"
      find "$staging" -type f -printf '  %P\n'
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
    if [ -n "$expected_names" ] && ! printf '%s\n' "$expected_names" | grep -qxF "$base"; then
      echo "ERROR: staged filename is not declared by the module manifest: $base" >&2
      rm -rf "$staging"
      return 1
    fi
    if [ -e "$destination_dir/$base" ]; then
      echo "ERROR: $destination_dir/$base already exists."
      echo "Staged test files are write-once. Delete it by hand to regenerate."
      rm -rf "$staging"
      return 1
    fi
  done

  local installed=()
  for f in "${generated[@]}"; do
    base=$(basename "$f")
    if ! mv "$f" "$destination_dir/$base" \
        || ! chmod 444 "$destination_dir/$base"; then
      rm -f -- "$destination_dir/$base"
      local installed_path
      for installed_path in "${installed[@]}"; do rm -f -- "$installed_path"; done
      rm -rf "$staging"
      echo "ERROR: contract-test installation failed; the batch was rolled back." >&2
      return 1
    fi
    installed+=("$destination_dir/$base")
    echo "Created $destination_dir/$base and set it read-only."
  done
  rm -rf "$staging"
}

cmd_write_contract() {
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh write-contract <module-id>"; return 1; }

  require_started "$module_id" || return 1
  [ "${#CONTRACTS_DIRS[@]}" -eq 1 ] || {
    echo "ERROR: write-contract requires exactly one Contracts project; found ${#CONTRACTS_DIRS[@]}."; return 1; }
  local destination="$WORKSPACE/tests/$CONTRACTS_DIR"

  local protected_paths expected_names="" path base
  local baseline
  baseline="$(module_baseline_ref "$module_id")" || return 1
  protected_paths="$(manifest_protected_paths "$module_id" "$baseline")" || {
    echo "ERROR: could not read protected contract paths from the active baseline." >&2
    return 1
  }
  [ -n "$protected_paths" ] || {
    echo "ERROR: module '$module_id' declares no protected/generated test artefact paths."; return 1; }
  while IFS= read -r path; do
    [ -n "$path" ] || continue
    case "$path" in
      "tests/$CONTRACTS_DIR/"*Tests.cs) ;;
      *)
        echo "ERROR: protected artefact must be a direct *Tests.cs file in tests/$CONTRACTS_DIR: $path" >&2
        return 1 ;;
    esac
    local relative_protected="${path#"tests/$CONTRACTS_DIR/"}"
    case "$relative_protected" in
      */*)
        echo "ERROR: protected artefact must be directly inside tests/$CONTRACTS_DIR: $path" >&2
        return 1 ;;
    esac
    base="${path##*/}"
    if [ ! -e "$WORKSPACE/$path" ]; then
      expected_names="$expected_names$base"$'\n'
    fi
  done <<EOF
$protected_paths
EOF

  expected_names="$(printf '%s' "$expected_names" | sed '/^$/d')"
  if [ -z "$expected_names" ]; then
    echo "All manifest-declared contract tests for '$module_id' already exist; nothing to write."
    return 0
  fi

  # The Module ID is a manifest key, not a class name. The exact protected paths
  # in the manifest define this batch; the write-once guarantee is enforced per
  # file inside stage_llm_test_files.
  local task="Read the attached .agent/rules/architecture.md and .agent/skills/coder.md. Locate the module manifest whose Module ID is exactly '$module_id'. Create exactly these missing manifest-declared contract files in the current directory, with no directories and no other files:
$expected_names

For every documented public entry point represented by those files, check every documented public type, constructor, method overload, generic arity, parameter name/type/order, return type, nullability, property type and relevant static/instance distinction. Use exact reflection lookups with parameter-type arrays; never use GetMethod(name) alone when overloads are possible. Add [Trait(\"Category\", \"Contract\")] to every test. Do not rewrite a contract file that already exists under /workspace/tests. Do not write implementation code. The workspace under /workspace is mounted read-only."

  stage_llm_test_files "$destination" "$(broadcast_prefix)$task" "" "$expected_names"
}

cmd_write_golden_harness() {
  # The frontier authors the golden fixture; the HARNESS that runs it is the
  # strongest correctness gate in the pipeline, so it is NOT written by the
  # local Coder. A subtly loose comparison (double vs decimal, culture-dependent
  # parsing, reference equality) would silently let broken code pass. Instead we
  # instantiate a canonical, pre-tested harness shipped in the starter kit,
  # substituting the project name, and freeze it read-only.
  [ "${#GOLDEN_DIRS[@]}" -eq 1 ] || {
    echo "ERROR: write-golden-harness requires exactly one Golden project; found ${#GOLDEN_DIRS[@]}."; return 1; }
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
  # Escape sed-special characters in the project name for safe substitution.
  local escaped_name temporary_harness
  escaped_name=$(printf '%s' "$project_name" | sed 's/[&/\]/\\&/g')
  temporary_harness="$(mktemp "$destination/.golden-harness.XXXXXX")" || return 1
  if ! sed "s/__PROJECT__/${escaped_name}/g" "$template" > "$temporary_harness" \
      || ! chmod 444 "$temporary_harness" \
      || ! mv -- "$temporary_harness" "$destination/$filename"; then
    rm -f -- "$temporary_harness"
    echo "ERROR: failed to install $destination/$filename" >&2
    return 1
  fi
  echo "Wrote canonical golden harness: $destination/$filename (frozen read-only)."
  echo "It runs tests/fixtures/critical_logic_golden.json against production entry points."
  echo "The harness is not agent-authored; do not let a Coder edit it."
}

cmd_show_frontier_fix() {
  # NOTE: this only DISPLAYS the frontier-written fix for manual application;
  # it deliberately applies nothing (the human stays in the loop for
  # frontier-authored code).
  local module_id="${1:-}"
  [ -n "$module_id" ] || { echo "usage: dev.sh show-frontier-fix <module-id>"; return 1; }
  local fix_file="$TENNINETY_RUNTIME_DIR/$module_id/frontier-fix-${module_id}.md"
  if [ ! -f "$fix_file" ]; then
    echo "No frontier fix file found at $fix_file"
    echo "Run 'dev.sh escalate $module_id <log> --override --write-code' first."
    return 1
  fi
  echo "Frontier fix file: $fix_file"
  echo ""
  cat -- "$fix_file"
  echo ""
  echo "Review it manually, then apply the code blocks to the relevant files."
  echo "After applying, run: dev.sh test $module_id"
}

cmd_help() {
  echo "dev.sh version $DEV_SH_VERSION"
  echo ""
  cat <<'EOF'
dev.sh – orchestrator for the local coding loop (C# / .NET, aider runtime)

Usage: dev.sh <subcommand> [args]

Subcommands:
  start <module-id>                          Create module-start-<module-id> tag (needs clean tree)
  write <module-id> "<task>"                 Run the Coder with a task
  review <module-id>                         Run the Reviewer on the active module diff
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
  runtime-path [module-id]              Print the external host-state directory
  check-coverage                        Verify every tracked src/*.cs file is in a module manifest
  reset <module-id>                          Back up and discard this module's uncommitted work
  broadcast "<note>"                    Add a note all Coders will see
  notes                                 Show current broadcast notes
  write-contract <module-id>                 Write the manifest's contract tests (exact, write-once batch)
  write-golden-harness                  Write the deterministic golden-fixture harness (staged, write-once)
  show-frontier-fix <module-id>              Display frontier-written fix (applies nothing)
  help                                  Show this message
  version                               Print the framework version (DEV_SH_VERSION)

Example workflow for a new module:
  dev.sh start <module-id>
  dev.sh write-contract <module-id>
  dev.sh iterate <module-id> "Implement the complete <ModuleName> module (Module ID: <module-id>) exactly as defined in .agent/rules/architecture.md. Create or edit only the files listed in that module manifest."
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

# Every module-taking command shares one strict identifier grammar. Validate at
# dispatch as well as inside individual helpers so read-only path construction
# (for example show-frontier-fix) cannot be used with traversal-like input.
case "$cmd" in
  start|write|review|test|iterate|finalise|commit|queue|fix|escalate|reject|approve|runtime-path|reset|write-contract|show-frontier-fix)
    module_arg="${2:-}"
    if [ -n "$module_arg" ] && [[ ! "$module_arg" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]]; then
      echo "ERROR: invalid Module ID '$module_arg' (expected lowercase kebab-case)." >&2
      exit 1
    fi
    ;;
esac

# Apply the CWD guard only to commands that act on the workspace (mutate state
# or invoke agents). Read-only/help commands run from anywhere.
case "$cmd" in
  help|--help|-h|version|--version|-v|status|runtime-path|notes|show-frontier-fix)
    : ;;  # exempt from CWD guard
  *)
    require_cwd_in_workspace "$@" || exit 1 ;;
esac

# Serialise mutating commands per workspace. Two concurrent invocations (e.g.
# two terminals running 'iterate' on the same project) could otherwise race the
# content-fingerprint gates and the git working tree. flock gives a single
# writer per workspace; read-only commands are exempt so 'status' never blocks.
# Mutating without a lock would make content gates and Git metadata racy, so a
# missing/broken flock is a hard refusal.
case "$cmd" in
  help|--help|-h|version|--version|-v|status|runtime-path|notes|show-frontier-fix|check-coverage)
    : ;;  # no lock needed (read-only)
  *)
    if [ -z "${DEV_LOCK_HELD:-}" ]; then
      command -v flock >/dev/null 2>&1 || {
        echo "ERROR: flock is required for mutating dev.sh commands (install util-linux)." >&2
        exit 1
      }
      mkdir -p "$TENNINETY_RUNTIME_DIR" || exit 1
      lockfile="$TENNINETY_RUNTIME_DIR/dev.lock"
      # Open the lock on fd 9 and try a non-blocking exclusive grab. If another
      # invocation holds it, fail fast with a clear message instead of queuing.
      if ! { exec 9>"$lockfile"; }; then
        echo "ERROR: could not open the dev.sh lock file: $lockfile" >&2
        exit 1
      fi
      if ! flock -n 9; then
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
  runtime-path)       shift; cmd_runtime_path "$@" ;;
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
