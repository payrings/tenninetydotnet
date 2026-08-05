#!/bin/bash
# Inspect the staged commit, not the mutable working tree. A partially staged
# raw-SQL change must not pass because the unstaged file happens to be clean.
set -uo pipefail

WORKSPACE="$(git rev-parse --show-toplevel 2>/dev/null)" || {
  echo "ERROR: no Git workspace found." >&2
  exit 2
}
paths="$(mktemp "${TMPDIR:-/tmp}/tenninety-raw-sql-paths.XXXXXX")" || exit 2
violations="$(mktemp "${TMPDIR:-/tmp}/tenninety-raw-sql-findings.XXXXXX")" || {
  rm -f -- "$paths"
  exit 2
}
trap 'rm -f -- "$paths" "$violations"' EXIT

if ! git -C "$WORKSPACE" diff --cached --name-only --diff-filter=ACMR -z -- src/ > "$paths"; then
  echo "ERROR: could not enumerate staged C# files." >&2
  exit 2
fi

while IFS= read -r -d '' path; do
  case "${path,,}" in
    *.cs) ;;
    *) continue ;;
  esac
  case "/$path/" in */bin/*|*/obj/*) continue ;; esac

  content="$(mktemp "${TMPDIR:-/tmp}/tenninety-raw-sql-content.XXXXXX")" || exit 2
  if ! git -C "$WORKSPACE" show ":$path" > "$content"; then
    rm -f -- "$content"
    echo "ERROR: could not read staged file '$path'." >&2
    exit 2
  fi
  if ! awk -v file="$path" '
      /new[[:space:]]+Sql(Connection|Command)[[:space:]]*\(|\.CommandText[[:space:]]*=|\.(FromSqlRaw|ExecuteSqlRaw|ExecuteSqlRawAsync|SqlQueryRaw)[[:space:]]*\(/ \
        && $0 !~ /\/\/[[:space:]]*allow-raw-sql([[:space:]]|$)/ {
          printf "%s:%d:%s\n", file, NR, $0
        }
    ' "$content" >> "$violations"; then
    rm -f -- "$content"
    echo "ERROR: could not inspect staged file '$path'." >&2
    exit 2
  fi
  rm -f -- "$content"
done < "$paths"

if [ -s "$violations" ]; then
  echo "BLOCK: staged raw SQL usage requires an explicit // allow-raw-sql marker:" >&2
  sed 's/^/  /' "$violations" >&2
  exit 1
fi
