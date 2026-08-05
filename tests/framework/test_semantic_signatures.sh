#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TEMP="$(mktemp -d)"
trap 'find "$TEMP" -depth -delete 2>/dev/null || true' EXIT

FIXTURE="$TEMP/fixture"
mkdir -p "$FIXTURE/src/Demo" "$FIXTURE/scripts/signature-checker" "$FIXTURE/.agent/rules"
cp "$ROOT/starter-kit/scripts/check_signatures.sh" "$FIXTURE/scripts/"
cp -a "$ROOT/starter-kit/scripts/signature-checker/." "$FIXTURE/scripts/signature-checker/"
cp "$ROOT/starter-kit/global.json" "$FIXTURE/"

printf '%s\n' '**/bin/' '**/obj/' > "$FIXTURE/.gitignore"

printf '%s\n' \
  '<Project Sdk="Microsoft.NET.Sdk">' \
  '  <PropertyGroup>' \
  '    <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>' \
  '    <Nullable>enable</Nullable>' \
  '    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>' \
  '  </PropertyGroup>' \
  '</Project>' > "$FIXTURE/src/Demo/Demo.csproj"
printf 'global using CustomerId = System.String;\n' > "$FIXTURE/src/Demo/Aliases.cs"
printf 'namespace Demo; public static class Api { public static CustomerId GetId() => default!; }\n' \
  > "$FIXTURE/src/Demo/Api.cs"
printf '%s\n' \
  'namespace Demo;' \
  'public class Container { public class Nested { } }' \
  'public enum Code : int { Zero = 0 }' \
  > "$FIXTURE/src/Demo/Shapes.cs"
printf '# architecture\n' > "$FIXTURE/.agent/rules/architecture.md"

git -C "$FIXTURE" init -q
git -C "$FIXTURE" config user.email framework@example.invalid
git -C "$FIXTURE" config user.name framework-tests
(cd "$FIXTURE" && dotnet restore scripts/signature-checker/Tenninety.SignatureChecker.csproj -noAutoResponse >/dev/null)
(cd "$FIXTURE" && dotnet restore src/Demo/Demo.csproj --use-lock-file -noAutoResponse >/dev/null)
git -C "$FIXTURE" add .
git -C "$FIXTURE" commit -qm baseline

printf 'global using CustomerId = System.Guid;\n' > "$FIXTURE/src/Demo/Aliases.cs"
printf '%s\n' \
  'namespace Demo;' \
  'public class Container { protected class Nested { } }' \
  'public enum Code : long { Zero = 0 }' \
  > "$FIXTURE/src/Demo/Shapes.cs"
output="$(cd "$FIXTURE" && scripts/check_signatures.sh --since HEAD --names-only)"
printf '%s\n' "$output" | grep -q $'^API\tDemo.Api.GetId$'
printf '%s\n' "$output" | grep -q $'^API\tDemo.Code$'
printf '%s\n' "$output" | grep -q $'^API\tDemo.Container.Nested$'
