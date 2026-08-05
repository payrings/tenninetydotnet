#!/bin/bash
# Shared host-side runtime helpers. Source this file; do not execute it.

tenninety_init_runtime() {
  local workspace="${1:?workspace path required}"
  local workspace_real state_home workspace_id runtime_real

  workspace_real="$(cd "$workspace" && pwd -P)" || {
    echo "ERROR: cannot resolve workspace '$workspace'." >&2
    return 1
  }

  if [ -z "${TENNINETY_RUNTIME_DIR:-}" ]; then
    state_home="${TENNINETY_STATE_HOME:-${XDG_STATE_HOME:-$HOME/.local/state}/tenninetydotnet}"
    workspace_id="$(printf '%s' "$workspace_real" | sha256sum | cut -d' ' -f1 | head -c 24)"
    TENNINETY_RUNTIME_DIR="$state_home/$workspace_id"
  fi

  runtime_real="$(realpath -m -- "$TENNINETY_RUNTIME_DIR")" || return 1

  case "$runtime_real/" in
    "$workspace_real"/*)
      echo "ERROR: TENNINETY_RUNTIME_DIR must be outside the workspace." >&2
      echo "       Refusing unsafe runtime path: $runtime_real" >&2
      return 1 ;;
  esac

  mkdir -p -- "$runtime_real" || {
    echo "ERROR: cannot create runtime directory '$runtime_real'." >&2
    return 1
  }
  chmod 700 -- "$runtime_real" || {
    echo "ERROR: cannot restrict runtime directory '$runtime_real' to mode 700." >&2
    return 1
  }
  runtime_real="$(cd "$runtime_real" && pwd -P)" || return 1
  case "$runtime_real/" in
    "$workspace_real"/*)
      echo "ERROR: resolved runtime directory entered the workspace." >&2
      return 1 ;;
  esac

  TENNINETY_RUNTIME_DIR="$runtime_real"
  export TENNINETY_RUNTIME_DIR
}

tenninety_runtime_path() {
  local relative="${1:?relative runtime path required}"
  case "$relative" in
    /*|*'..'*)
      echo "ERROR: unsafe runtime-relative path '$relative'." >&2
      return 1 ;;
  esac
  printf '%s/%s\n' "$TENNINETY_RUNTIME_DIR" "$relative"
}
