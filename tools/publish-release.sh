#!/usr/bin/env bash
set -euo pipefail

if gh release view "$TAG" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
  echo "Preserving existing release $TAG"
  exit 0
fi

ZIP="$WOBBLY_WINDOWS_DIST_DIR/wobbly-windows-$VERSION.zip"
test -s "$ZIP"
test -s "$ZIP.sha256"
EXTRA=(--repo "$GITHUB_REPOSITORY")
[[ "$PRERELEASE" == true ]] && EXTRA+=(--prerelease --latest=false)
gh release create "$TAG" "$ZIP" "$ZIP.sha256" \
  --target "$GITHUB_SHA" \
  --title "$TITLE" \
  --notes "**Updating:** back up \`settings.json\` from your existing \`wobbly-windows/\` folder, delete that old mod folder, extract the new \`wobbly-windows/\` folder, then restore your saved settings. Restart KSA after updating." \
  --generate-notes \
  "${EXTRA[@]}"
