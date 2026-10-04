#!/bin/sh
# 发版脚本：改 csproj <Version> → 提交 → 打 v tag → push，之后 CI
# （.github/workflows/dotnet-desktop.yml）自动测试、打包三平台产物（macOS zip ×2、
# Windows zip + Inno Setup 安装包、Linux zip）并创建 GitHub Release。
#
# 用法: scripts/release.sh <x.y.z> [--skip-test] [--watch]
#   --skip-test  跳过本地 dotnet test（CI 仍会跑，测试不过不会出包）
#   --watch      push 后轮询等待 CI 完成，并核对 Release 产物齐全（需要 gh 且已登录）
#
# 失败恢复：CI 失败时 tag 已推送，修复代码后
#   git push origin :refs/tags/vX.Y.Z && git tag -d vX.Y.Z
# 再重跑本脚本（csproj 已是目标版本时自动跳过 bump 提交）。
# 版本必须与 csproj <Version> 一致，workflow 会在 tag 时强制校验。
#
# 注意：echo 里紧邻全角字符的变量要写 ${VAR}，否则 macOS 的 sh（bash 3.2）会把
# 多字节字符并入变量名导致 unbound variable（UTF-8 locale 下 isalpha 对高位字节返回真）。
set -eu
cd "$(dirname "$0")/.."

usage() { echo "用法: scripts/release.sh <x.y.z> [--skip-test] [--watch]" >&2; exit 1; }

VERSION=""
SKIP_TEST=false
WATCH=false
for arg in "$@"; do
  case "$arg" in
    --skip-test) SKIP_TEST=true ;;
    --watch) WATCH=true ;;
    *) VERSION=$arg ;;
  esac
done
[ -n "$VERSION" ] || usage

# 三段纯数字，与 UpdateChecker 的版本比较逻辑一致
echo "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || { echo "版本号须为 x.y.z 三段数字：$VERSION" >&2; exit 1; }

PROJECT=src/Inpaint.App/Inpaint.App.csproj
TAG=v$VERSION

[ "$(git symbolic-ref --short HEAD)" = main ] || { echo "不在 main 分支" >&2; exit 1; }
git fetch --quiet
BEHIND=$(git rev-list --count HEAD..@{u})
[ "$BEHIND" -eq 0 ] || { echo "main 落后远端 $BEHIND 个提交，先 pull" >&2; exit 1; }
git diff --quiet && git diff --cached --quiet || { echo "工作树有未提交改动，先提交或暂存" >&2; exit 1; }
UNTRACKED=$(git ls-files --others --exclude-standard | wc -l | tr -d ' ')
[ "$UNTRACKED" -eq 0 ] || echo "注意：有 $UNTRACKED 个未跟踪文件，不会进入本次发布" >&2

# 「已发过版」的真正信号是 tag 存在；csproj 版本一致只说明无需 bump
# （首版预写版本号、失败恢复重发时都会走到这条路径）
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && { echo "本地已存在 $TAG" >&2; exit 1; }
[ -z "$(git ls-remote --tags origin "refs/tags/$TAG")" ] || { echo "远端已存在 $TAG" >&2; exit 1; }

CUR=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$PROJECT" | head -1 | tr -d '[:space:]')
[ -n "$CUR" ] || { echo "无法从 $PROJECT 读取 <Version>" >&2; exit 1; }

if [ "$SKIP_TEST" != true ]; then
  echo "本地跑测试（--skip-test 可跳过）..."
  dotnet test Inpaint.slnx --nologo -v q || { echo "本地测试失败，中止" >&2; exit 1; }
fi

if [ "$CUR" != "$VERSION" ]; then
  # 替换 <Version>，经临时文件回写以兼容 BSD/GNU sed
  TMP=$(mktemp)
  sed "s#\(<Version>\)[^<]*\(</Version>\)#\1$VERSION\2#" "$PROJECT" > "$TMP"
  mv "$TMP" "$PROJECT"
  NEW=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$PROJECT" | head -1 | tr -d '[:space:]')
  [ "$NEW" = "$VERSION" ] || { echo "版本替换校验失败：期望 ${VERSION}，实得 $NEW" >&2; exit 1; }

  git add "$PROJECT"
  git commit -m "Bump version to $VERSION"
else
  echo "csproj 已是 ${VERSION}，跳过 bump 提交，直接打 tag"
fi

git tag -a "$TAG" -m "Inpaint $TAG"
git push origin main "$TAG"

SLUG=$(git remote get-url origin | sed -E 's#.*github\.com[:/]##; s#\.git$##')
RUN_URL="https://github.com/$SLUG/actions"
REL_URL="https://github.com/$SLUG/releases/tag/$TAG"

if [ "$WATCH" != true ]; then
  echo "已推送 ${TAG}，CI 会自动打包并发布："
  echo "  Actions: $RUN_URL"
  echo "  Release: ${REL_URL}（流水线跑完后出现）"
  exit 0
fi

command -v gh >/dev/null 2>&1 || { echo "未安装 gh，无法 watch，手动看 $RUN_URL" >&2; exit 1; }

# 找 tag 触发的 run（push 后要几秒才注册得上）
RUN_ID=
i=0
while [ $i -lt 12 ]; do
  RUN_ID=$(gh run list --branch "$TAG" --limit 1 --json databaseId --jq '.[0].databaseId' 2>/dev/null || true)
  [ -n "$RUN_ID" ] && [ "$RUN_ID" != null ] && break
  i=$((i + 1)); sleep 5
done
if [ -z "$RUN_ID" ] || [ "$RUN_ID" = null ]; then
  echo "未找到 $TAG 触发的 CI run，手动看 $RUN_URL" >&2
  exit 1
fi
echo "等待 CI（run ${RUN_ID}，最长 45 分钟）..."

i=0
while [ $i -lt 90 ]; do
  STATE=$(gh run view "$RUN_ID" --json status,conclusion --jq '.status+"/"+(.conclusion // "-")' 2>/dev/null || echo unknown)
  case "$STATE" in
    completed/success) break ;;
    completed/*)
      echo "CI 失败（${STATE}）。修复后删除 tag 重跑：git push origin :refs/tags/$TAG && git tag -d $TAG" >&2
      echo "日志：$RUN_URL/$RUN_ID" >&2
      exit 1
      ;;
  esac
  i=$((i + 1)); sleep 30
done
[ $i -lt 90 ] || { echo "等待超时（45 分钟），手动看 $RUN_URL/$RUN_ID" >&2; exit 1; }

# 核对三平台产物是否齐全
ASSETS=$(gh release view "$TAG" --json assets --jq '[.assets[].name] | join(",")' 2>/dev/null || true)
MISSING=
for want in "Inpaint-$VERSION-macos-arm64.zip" "Inpaint-$VERSION-macos-x64.zip" \
  "Inpaint-$VERSION-win-x64.zip" "Inpaint-$VERSION-win-x64-setup.exe" "Inpaint-$VERSION-linux-x64.zip"; do
  case ",$ASSETS," in
    *",$want,"*) ;;
    *) MISSING="$MISSING $want" ;;
  esac
done
if [ -n "$MISSING" ]; then
  echo "Release 产物缺失：${MISSING}。可在 Actions 页 Re-run release job，或删 tag 重跑脚本。" >&2
  exit 1
fi

echo "发版完成 ${TAG}：$REL_URL"
echo "产物：$ASSETS"
