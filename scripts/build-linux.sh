#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/../src/PulseTerm.App"
RID="linux-x64"
OUTPUT_DIR="$SCRIPT_DIR/../publish/$RID"

echo "Building PulseTerm for $RID..."
dotnet publish "$PROJECT_DIR" -r "$RID" --self-contained -c Release -o "$OUTPUT_DIR"
echo "Published to $OUTPUT_DIR"
