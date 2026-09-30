#!/bin/bash
# Builds BigFiles.app next to this script. Needs Apple's Command Line Tools.
set -euo pipefail
cd "$(dirname "$0")"

if ! xcrun --find swiftc >/dev/null 2>&1; then
  echo "Apple's Command Line Tools are needed. Installing them now…"
  echo "Run this script again once that installer finishes."
  xcode-select --install || true
  exit 1
fi

APP="BigFiles.app"
ARCH="$(uname -m)"   # arm64 on Apple silicon, x86_64 on Intel

echo "Building for $ARCH…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

xcrun swiftc -O -swift-version 5 -parse-as-library \
  -target "${ARCH}-apple-macos14.0" \
  main.swift -o "$APP/Contents/MacOS/BigFiles"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>BigFiles</string>
  <key>CFBundleDisplayName</key><string>BigFiles</string>
  <key>CFBundleIdentifier</key><string>com.softsignal.bigfiles</string>
  <key>CFBundleExecutable</key><string>BigFiles</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST

codesign --force --sign - "$APP" >/dev/null 2>&1 || true

echo "Done: $(pwd)/$APP"
echo "Drag it into Applications if you want to keep it. Opening now…"
open "$APP"
