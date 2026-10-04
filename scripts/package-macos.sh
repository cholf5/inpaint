#!/bin/sh
# 打包 macOS .app bundle：Dock 图标、程序坞名称等都来自 bundle 的 Info.plist，
# 裸可执行文件（dotnet run）没有这些。产物: artifacts/macos/Inpaint.app
#
# 用法: scripts/package-macos.sh [arm64|x64] [--fdd]
#   --fdd  框架依赖发布（目标机需已装 .NET 运行时）；默认自包含，产物可直接分发。
#
# 注意 macOS 会缓存 bundle 图标：换 icns 后若 Dock 仍显示旧图，touch Inpaint.app 或重启 Dock。
set -eu
cd "$(dirname "$0")/.."

ARCH=arm64
SELF_CONTAINED=true
for arg in "$@"; do
  case "$arg" in
    arm64 | x64) ARCH=$arg ;;
    --fdd) SELF_CONTAINED=false ;;
    *) echo "未知参数: $arg（用法见文件头注释）" >&2; exit 1 ;;
  esac
done

PROJECT=src/Inpaint.App/Inpaint.App.csproj
RID=osx-$ARCH
OUT=artifacts/macos

# 版本单一来源是 csproj 的 <Version>（发版改那里）
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$PROJECT" | head -1 | tr -d '[:space:]')
[ -n "$VERSION" ] || { echo "无法从 $PROJECT 读取 <Version>" >&2; exit 1; }

rm -rf "$OUT"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained "$SELF_CONTAINED" -o "$OUT/publish"

APP="$OUT/Inpaint.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/publish/" "$APP/Contents/MacOS/"
cp src/Inpaint.App/Assets/app-icon.icns "$APP/Contents/Resources/app-icon.icns"
rm -rf "$OUT/publish"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
    <key>CFBundleDisplayName</key><string>Inpaint</string>
    <key>CFBundleExecutable</key><string>Inpaint.App</string>
    <key>CFBundleIconFile</key><string>app-icon</string>
    <key>CFBundleIdentifier</key><string>com.cholf5.inpaint</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleName</key><string>Inpaint</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
EOF

# ad-hoc 签名：Apple Silicon 要求可执行文件至少有 ad-hoc 签名；--deep 连同内嵌 dylib 一起签
codesign --force --deep -s - "$APP"

# 注意：echo 里紧邻全角字符的变量要写 ${VAR}，否则 sh 会把全角字符并入变量名
MODE=$([ "$SELF_CONTAINED" = true ] && echo 自包含 || echo 框架依赖)
echo "已生成 ${APP}（${RID}，${MODE}，版本 ${VERSION}）"
echo "运行: open ${APP}"
