#!/bin/bash
# Usage: ./find_consumers.sh SomeClassName
# Finds textual call sites for human inspection. Downstream module propagation
# uses the architecture manifest's Module-ID dependency graph instead of trying
# to infer ownership from filenames.
set -uo pipefail
SYMBOL="${1:?Usage: ./find_consumers.sh <literal-symbol>}"
rg -n -F --type cs -g '!tests/fixtures/*' -- "$SYMBOL" src/ tests/
