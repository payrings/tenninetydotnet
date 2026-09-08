#!/usr/bin/env bash
# Static configuration checks for continuous verification (runs on the CI runner, bash).
#  1. JSON / JSONC / YAML / XML configuration syntax (.csproj/.props/.targets included);
#  2. broken local Markdown links (README.md + docs/*.md, relative targets only).
#
# The Python interpreter can be overridden with $PYTHON (CI points it at the virtualenv
# that installed the pinned scripts/ci/requirements.txt). The check itself never installs
# anything.
#
# Exits nonzero on any failure; prints every finding.

set -u

cd "$(dirname "$0")/../.."
exec "${PYTHON:-python3}" scripts/ci/static_checks.py
