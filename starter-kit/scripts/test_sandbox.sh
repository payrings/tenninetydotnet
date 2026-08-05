#!/bin/bash
# Shared host-side helpers for disposable restore/build/test workspaces.
# runtime.sh must be sourced and tenninety_init_runtime called first.

tenninety_is_build_control_path() {
  local path="$1" lower
  lower="${path,,}"
  case "$lower" in
    *.csproj|*.fsproj|*.vbproj|*.sln|*.slnx|*.props|*.targets|*.rsp|*.user|*/nuget.config|nuget.config|global.json|.editorconfig|*/.editorconfig)
      return 0 ;;
  esac
  return 1
}

tenninety_reject_ignored_build_controls() {
  local path found=0 listing
  listing="$(mktemp "$TENNINETY_RUNTIME_DIR/ignored-controls.XXXXXX")" || return 1
  if ! git ls-files --others --ignored --exclude-standard -z > "$listing"; then
    rm -f -- "$listing"
    echo "ERROR: could not enumerate ignored build-control inputs." >&2
    return 1
  fi
  while IFS= read -r -d '' path; do
    if tenninety_is_build_control_path "$path"; then
      [ "$found" -eq 1 ] || {
        echo "IGNORED BUILD-CONTROL FILE(S) detected:" >&2
        found=1
      }
      printf '  - %s\n' "$path" >&2
    fi
  done < "$listing"
  rm -f -- "$listing"
  if [ "$found" -eq 1 ]; then
    echo "Remove these files or commit them in a separate reviewed scaffold change." >&2
    echo "Ignored build inputs cannot participate in a mechanically verified build." >&2
    return 1
  fi
}

tenninety_git_visible_files() {
  local path listing
  listing="$(mktemp "$TENNINETY_RUNTIME_DIR/git-visible.XXXXXX")" || return 1
  if ! git ls-files --cached --others --exclude-standard -z > "$listing"; then
    rm -f -- "$listing"
    return 1
  fi
  while IFS= read -r -d '' path; do
    case "$path" in
      .git/*|.dev-runtime/*|*/bin/*|*/obj/*|.env|*/.env|.env.*|*/.env.*) continue ;;
    esac
    [ -e "$path" ] || [ -L "$path" ] || continue
    printf '%s\0' "$path"
  done < "$listing"
  local rc=$?
  rm -f -- "$listing"
  return "$rc"
}

tenninety_lock_hash() {
  # Include lockfile paths as well as contents. Concatenating contents alone
  # lets different lockfile layouts collide into the same persistent cache.
  local paths sorted material path hash
  paths="$(mktemp "$TENNINETY_RUNTIME_DIR/lock-paths.XXXXXX")" || return 1
  sorted="$(mktemp "$TENNINETY_RUNTIME_DIR/lock-paths-sorted.XXXXXX")" || {
    rm -f -- "$paths"; return 1;
  }
  material="$(mktemp "$TENNINETY_RUNTIME_DIR/lock-material.XXXXXX")" || {
    rm -f -- "$paths" "$sorted"; return 1;
  }
  if ! find . \
      \( -path './.git' -o -path './.dev-runtime' -o -path '*/bin' -o -path '*/obj' \) -prune -o \
      -name packages.lock.json -type f -print0 > "$paths"; then
    rm -f -- "$paths" "$sorted" "$material"
    return 1
  fi
  if ! sort -z "$paths" > "$sorted"; then
    rm -f -- "$paths" "$sorted" "$material"
    return 1
  fi
  while IFS= read -r -d '' path; do
    printf '%s\0' "$path" >> "$material" || {
      rm -f -- "$paths" "$sorted" "$material"; return 1;
    }
    if ! cat -- "$path" >> "$material"; then
      rm -f -- "$paths" "$sorted" "$material"
      return 1
    fi
  done < "$sorted"
  hash="$(sha256sum -- "$material")" || {
    rm -f -- "$paths" "$sorted" "$material"; return 1;
  }
  printf '%.12s' "${hash%% *}"
  rm -f -- "$paths" "$sorted" "$material"
}

tenninety_workspace_hash() {
  tenninety_git_visible_files \
    | sort -z \
    | while IFS= read -r -d '' path; do
        printf '%s\0' "$path"
        stat -c '%a %F' -- "$path" 2>/dev/null || return 1
        if [ -L "$path" ]; then
          readlink -- "$path"
        else
          sha256sum -- "$path"
        fi
      done \
    | sha256sum \
    | cut -d' ' -f1
}

tenninety_create_snapshot() {
  local destination="${1:?snapshot destination required}"
  mkdir -p -- "$destination" || return 1
  tenninety_git_visible_files \
    | tar -C "$PWD" --null --verbatim-files-from --files-from=- -cf - \
    | tar -C "$destination" -xf -
}

tenninety_snapshot_hash() {
  local directory="${1:?snapshot directory required}" relative
  find "$directory" \
    \( -path '*/bin' -o -path '*/obj' \) -prune -o \
    \( -type f -o -type l \) -print0 \
    | sort -z \
    | while IFS= read -r -d '' path; do
        relative="${path#"$directory"/}"
        printf '%s\0' "$relative"
        stat -c '%a %F' -- "$path" 2>/dev/null || return 1
        if [ -L "$path" ]; then readlink -- "$path"; else sha256sum -- "$path"; fi
      done \
    | sha256sum \
    | cut -d' ' -f1
}

tenninety_restore_seed() {
  local seed="${1:?seed directory required}"
  local cache_volume="${2:?NuGet cache volume required}"
  local before after output rc
  before="$(tenninety_snapshot_hash "$seed")" || return 98
  output="$(docker run --rm --pull=never \
    -v "$seed:/workspace" \
    -v "$cache_volume:/home/agent/.nuget/packages" \
    --entrypoint bash \
    test-runner -lc 'dotnet restore --locked-mode -noAutoResponse' 2>&1)"
  rc=$?
  after="$(tenninety_snapshot_hash "$seed")" || {
    printf '%s\n' "$output"
    return 98
  }
  printf '%s\n' "$output"
  if [ "$before" != "$after" ]; then
    echo "SNAPSHOT INTEGRITY FAILURE: restore changed source or control inputs." >&2
    return 97
  fi
  return "$rc"
}

tenninety_clone_seed() {
  local seed="${1:?seed directory required}"
  local destination="${2:?destination required}"
  [ ! -e "$destination" ] || {
    echo "ERROR: disposable test destination already exists: $destination" >&2
    return 1
  }
  mkdir -p -- "$destination" || return 1
  cp -a -- "$seed/." "$destination/"
}

tenninety_run_project() {
  local sandbox="${1:?sandbox directory required}"
  local cache_volume="${2:?NuGet cache volume required}"
  local project="${3:?test project required}"
  docker run --rm --pull=never \
    --network=none \
    -e TEST_PROJECT="$project" \
    -v "$sandbox:/workspace" \
    -v "$cache_volume:/home/agent/.nuget/packages:ro" \
    --entrypoint bash \
    test-runner -lc \
      'dotnet build "$TEST_PROJECT" --no-restore -noAutoResponse -warnaserror -clp:NoSummary && dotnet test "$TEST_PROJECT" --no-build --no-restore -noAutoResponse -v normal'
}
