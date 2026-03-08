#!/bin/bash
# Full clean → deploy → start cycle
# Usage: ./dev-cycle.sh [commit]
#   No args: builds from current working tree
#   With arg: builds from that commit

set -e

DIR="$(cd "$(dirname "$0")" && pwd)"

echo "===== CLEAN ====="
"$DIR/dev-clean.sh"

echo ""
echo "===== DEPLOY ====="
"$DIR/dev-deploy.sh" "$@"

echo ""
echo "===== START ====="
"$DIR/dev-start.sh" --clean
