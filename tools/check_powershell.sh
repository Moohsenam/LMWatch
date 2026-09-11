#!/usr/bin/env bash
# Parses every PowerShell script and rejects syntax that Windows PowerShell 5.1
# cannot run, which is what the .cmd launchers fall back to.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PWSH="${PWSH:-pwsh}"

if ! command -v "$PWSH" >/dev/null 2>&1; then
  echo "pwsh not found; set PWSH to its path to run this check."
  exit 0
fi

echo "PowerShell checks"
echo "------------------------------------------------"

fail=0

# ---- 1. does it parse at all
"$PWSH" -NoProfile -Command "
\$bad = 0
Get-ChildItem -Path '$HERE' -Filter *.ps1 | ForEach-Object {
    \$errors = \$null
    \$null = [System.Management.Automation.Language.Parser]::ParseFile(\$_.FullName, [ref]\$null, [ref]\$errors)
    if (\$errors -and \$errors.Count -gt 0) {
        Write-Host ('  XX  ' + \$_.Name + ' does not parse')
        \$errors | ForEach-Object { Write-Host ('      line ' + \$_.Extent.StartLineNumber + ': ' + \$_.Message) }
        \$bad++
    }
    else {
        Write-Host ('  ok  ' + \$_.Name + ' parses')
    }
}
exit \$bad" || fail=1

# ---- 2. constructs that need PowerShell 7
for file in "$HERE"/*.ps1; do
  name="$(basename "$file")"
  hits=""

  # Ternary, null-coalescing, pipeline chain operators, and the parallel foreach.
  grep -nE '\?\?=|\?\?[^=]|\)\s*\?\s*.+\s*:\s*|&&|\|\||-Parallel' "$file" > /tmp/psbad.$$ 2>/dev/null

  if [ -s /tmp/psbad.$$ ]; then
    hits="$(cat /tmp/psbad.$$)"
  fi
  rm -f /tmp/psbad.$$

  if [ -n "$hits" ]; then
    echo "  XX  $name uses syntax Windows PowerShell 5.1 cannot parse:"
    echo "$hits" | sed 's/^/      /'
    fail=1
  else
    echo "  ok  $name is 5.1-compatible"
  fi
done

echo "------------------------------------------------"
if [ "$fail" -eq 0 ]; then
  echo "All PowerShell checks passed."
  exit 0
fi

echo "PowerShell checks FAILED."
exit 1
