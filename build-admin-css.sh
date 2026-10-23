#!/bin/bash
#
# Builds the admin pages' stylesheet with Tailwind CSS: src/Raven.Hosting/Styles/admin.css, and the classes used in
# src/Raven.Hosting/Components/Admin/, into src/Raven.Hosting/wwwroot/assets/admin.css. The output is committed, so
# running and testing the service needs nothing but .NET; run this after changing an admin page's classes.
#
#   ./build-admin-css.sh           rebuild admin.css
#   ./build-admin-css.sh --check   fail if admin.css is not what the sources build (CI)
#
# Tailwind's standalone CLI (no Node), pinned and checked against the release's checksums, is kept in .tools/.
#
set -euo pipefail

VERSION=v4.3.3
ROOT="$(cd "$(dirname "$0")" && pwd)"
INPUT="$ROOT/src/Raven.Hosting/Styles/admin.css"
OUTPUT="$ROOT/src/Raven.Hosting/wwwroot/assets/admin.css"
TOOLS="$ROOT/.tools"

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) asset=tailwindcss-macos-arm64 ;;
  Darwin-x86_64) asset=tailwindcss-macos-x64 ;;
  Linux-x86_64) asset=tailwindcss-linux-x64 ;;
  Linux-aarch64|Linux-arm64) asset=tailwindcss-linux-arm64 ;;
  *) echo "No Tailwind CLI for $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac

cli="$TOOLS/$asset-$VERSION"
if [[ ! -x "$cli" ]]; then
  mkdir -p "$TOOLS"
  base="https://github.com/tailwindlabs/tailwindcss/releases/download/$VERSION"
  curl -fsSL -o "$cli.download" "$base/$asset"
  expected="$(curl -fsSL "$base/sha256sums.txt" | awk -v name="$asset" '$2 == name || $2 == "./" name { print $1 }')"
  if command -v sha256sum >/dev/null; then
    actual="$(sha256sum "$cli.download" | awk '{ print $1 }')"
  else
    actual="$(shasum -a 256 "$cli.download" | awk '{ print $1 }')"
  fi
  if [[ -z "$expected" || "$expected" != "$actual" ]]; then
    rm -f "$cli.download"
    echo "Tailwind CLI $asset $VERSION does not match its published checksum" >&2
    exit 1
  fi
  chmod +x "$cli.download"
  mv "$cli.download" "$cli"
fi

if [[ "${1:-}" == "--check" ]]; then
  built="$(mktemp)"
  trap 'rm -f "$built"' EXIT
  "$cli" -i "$INPUT" -o "$built" --minify 2>/dev/null
  if ! cmp -s "$built" "$OUTPUT"; then
    echo "src/Raven.Hosting/wwwroot/assets/admin.css is out of date: run ./build-admin-css.sh and commit it" >&2
    exit 1
  fi
  echo "admin.css is up to date"
else
  "$cli" -i "$INPUT" -o "$OUTPUT" --minify
fi
