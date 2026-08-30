#!/usr/bin/env bash
# Merges the per-project cobertura reports into one and writes the line rate to the
# job summary. `dotnet test` runs each test project in its own process and so emits
# one report each; neither is the coverage of this repository, because the two cover
# overlapping parts of the same code.
#
# With a floor as the first argument, fails when the rate is under it. Only the nightly
# job passes one: it runs the launcher-bound tests, so its number is the whole suite's.
set -euo pipefail

floor=${1:-}

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
  echo "Line coverage: **${percent}%**${floor:+ (floor ${floor}%)}"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"

if [[ -n $floor ]] && awk -v p="$percent" -v f="$floor" 'BEGIN { exit !(p < f) }'; then
  echo "::error::Line coverage ${percent}% is below the ${floor}% floor"
  exit 1
fi
