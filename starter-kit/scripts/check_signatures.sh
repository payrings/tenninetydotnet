#!/bin/bash
# Run the semantic API checker in its own application process. dotnet-script
# preloads Microsoft.Build.Framework before user code, which makes the required
# MSBuildLocator registration impossible.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/signature-checker/Tenninety.SignatureChecker.csproj"
CONFIGURATION="Release"
CHECKER_DLL="$SCRIPT_DIR/signature-checker/bin/$CONFIGURATION/net10.0/Tenninety.SignatureChecker.dll"

[ -f "$PROJECT" ] || {
  echo "ERROR: semantic checker project not found: $PROJECT" >&2
  exit 2
}

# Keep compiler output away from stdout: callers treat stdout as the checker's
# machine-readable API-change stream.
dotnet build "$PROJECT" --configuration "$CONFIGURATION" --no-restore \
  --nologo --verbosity quiet -noAutoResponse >&2 || exit $?

[ -f "$CHECKER_DLL" ] || {
  echo "ERROR: semantic checker build produced no executable: $CHECKER_DLL" >&2
  exit 2
}

exec dotnet "$CHECKER_DLL" "$@"
