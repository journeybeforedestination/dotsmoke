#!/usr/bin/env bash
# Merges the per-project cobertura reports into one and writes the line rate to the
# job summary. `dotnet test` runs each test project in its own process and so emits
# one report each; neither is the coverage of this repository, because the two cover
# overlapping parts of the same code.
set -euo pipefail

dotnet dotnet-coverage merge \
  --output TestResults/merged.cobertura.xml \
  --output-format cobertura \
  TestResults/*.cobertura.xml

# The rate is the first attribute of the root <coverage> element.
rate=$(grep -o 'line-rate="[0-9.]*"' TestResults/merged.cobertura.xml | head -1 | cut -d'"' -f2)
percent=$(awk -v r="$rate" 'BEGIN { printf "%.1f", r * 100 }')

echo "Line coverage: ${percent}%"

{
  echo "### Coverage"
  echo
  echo "Line coverage: **${percent}%**"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
