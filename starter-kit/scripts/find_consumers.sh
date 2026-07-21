#!/bin/bash
# Usage: ./find_consumers.sh SomeClassName
# Finds every call site of a symbol so a signature-change review isn't
# reasoning blind about what depends on it.
rg -n --type cs -g '!tests/fixtures/*' "$1" src/ tests/
