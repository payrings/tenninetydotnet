#!/usr/bin/env bash
# Static configuration checks for continuous verification (runs on the CI runner, bash).
#  1. JSON / JSONC / YAML / XML configuration syntax;
#  2. broken local Markdown links (README.md + docs/*.md, relative targets only).
#
# Exits nonzero on any failure; prints every finding.

set -u

cd "$(dirname "$0")/../.."
exec python3 scripts/ci/static_checks.py
