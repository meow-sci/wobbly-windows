#!/usr/bin/env bash
set -euo pipefail

# PRs and tag refs validate only. Every non-main branch shares the beta channel.
if [[ "$GITHUB_EVENT_NAME" != push && "$GITHUB_EVENT_NAME" != workflow_dispatch ]] ||
   [[ "$GITHUB_REF" != refs/heads/* ]]; then
  echo 'publish=false' >> "$GITHUB_OUTPUT"
  exit 0
fi

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
MAJOR=$(cat "$ROOT/release-major.txt")
if [[ ! "$MAJOR" =~ ^[1-9][0-9]*$ ]] ||
   [[ ! "${GITHUB_RUN_NUMBER:-}" =~ ^[1-9][0-9]*$ ]]; then
  echo '::error::Release major and workflow run number must be positive decimal integers' >&2
  exit 1
fi

CHANNEL=main
PRERELEASE=false
NAME="$MAJOR.$GITHUB_RUN_NUMBER.0"
if [[ "$GITHUB_REF" != refs/heads/main ]]; then
  CHANNEL=beta
  PRERELEASE=true
  NAME+=-beta
fi
{
  echo 'publish=true'
  echo "version=$NAME"
  echo "tag=v$NAME"
  echo "title=wobbly-windows $NAME"
  echo "prerelease=$PRERELEASE"
  echo "channel=$CHANNEL"
} >> "$GITHUB_OUTPUT"
