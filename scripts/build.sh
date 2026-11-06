#!/usr/bin/env bash
# =============================================================================
# S3ObjectStorage 一键构建脚本
#
# 依赖安装（dotnet restore）→ 构建 → 测试（可选）→ 产物发布，一条命令完成。
#
# 用法：
#   bash scripts/build.sh                 # 完整流水线：restore + build + test + publish
#   bash scripts/build.sh --no-test       # 跳过测试（快速出包）
#   bash scripts/build.sh --build-only    # 只 restore + build，不测试不发布
#   bash scripts/build.sh --test-only     # 只跑测试
#   bash scripts/build.sh --clean         # 先清理 bin/obj/artifacts 再执行
#
# 环境变量：
#   CONFIG     构建配置，默认 Release
#   VERSION    覆盖程序集版本，例如 VERSION=0.2.1（默认取 Directory.Build.props 中的版本）
#   RID        运行时标识符，例如 linux-x64 / win-x64 / osx-arm64；
#              设置后产物为自包含单文件，默认不指定（框架依赖发布）
#
# 产物位置：
#   artifacts/publish/   发布产物（可直接运行：dotnet S3ObjectStorage.Api.dll 或容器构建上下文）
#   artifacts/test/      测试结果（如有）
#
# 环境要求：
#   .NET SDK 10.0（见 global.json）；Windows 用户可在 Git Bash 中运行，
#   或参考 README「构建与发布」一节使用等价的 dotnet 命令。
# =============================================================================
set -euo pipefail

cd "$(cd "$(dirname "$0")/.." && pwd)"

CONFIG="${CONFIG:-Release}"
DO_TEST=1
DO_PUBLISH=1
DO_CLEAN=0
MODE="all"

while [ $# -gt 0 ]; do
  case "$1" in
    --no-test)    DO_TEST=0; shift ;;
    --build-only) DO_PUBLISH=0; DO_TEST=0; MODE="build"; shift ;;
    --test-only)  DO_PUBLISH=0; MODE="test"; shift ;;
    --clean)      DO_CLEAN=1; shift ;;
    -h|--help)    sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "未知参数：$1（--help 查看用法）" >&2; exit 2 ;;
  esac
done

if ! command -v dotnet >/dev/null 2>&1; then
  echo "错误：未找到 dotnet CLI，请安装 .NET SDK 10.0：https://dot.net/download" >&2
  exit 1
fi

echo "==> .NET SDK：$(dotnet --version)"
echo "==> 配置：${CONFIG}$( [ -n "${VERSION:-}" ] && printf '  版本：%s' "$VERSION" )$( [ -n "${RID:-}" ] && printf '  RID：%s' "$RID" )"

if [ "$DO_CLEAN" = 1 ]; then
  echo "==> 清理 bin/obj/artifacts"
  dotnet clean -c "$CONFIG" >/dev/null 2>&1 || true
  rm -rf artifacts
  find . -type d \( -name bin -o -name obj \) -not -path "./.git/*" -exec rm -rf {} + 2>/dev/null || true
fi

VERSION_ARGS=()
if [ -n "${VERSION:-}" ]; then
  VERSION_ARGS=(-p:Version="$VERSION")
fi

echo "==> [1/4] 还原依赖"
dotnet restore "${VERSION_ARGS[@]}"

if [ "$MODE" = "test" ]; then
  echo "==> 测试"
  dotnet test -c "$CONFIG" --no-restore --logger "console;verbosity=normal" \
    --results-directory artifacts/test "${VERSION_ARGS[@]}"
  echo "==> 完成：测试通过"
  exit 0
fi

echo "==> [2/4] 构建（${CONFIG}）"
dotnet build -c "$CONFIG" --no-restore "${VERSION_ARGS[@]}"

if [ "$DO_TEST" = 1 ]; then
  echo "==> [3/4] 测试"
  dotnet test -c "$CONFIG" --no-build --results-directory artifacts/test "${VERSION_ARGS[@]}"
else
  echo "==> [3/4] 测试（已跳过）"
fi

if [ "$DO_PUBLISH" = 0 ]; then
  echo "==> 完成：构建通过（未发布产物）"
  exit 0
fi

echo "==> [4/4] 发布产物"
OUT="artifacts/publish"
rm -rf "$OUT"

if [ -n "${RID:-}" ]; then
  # 自包含单文件：目标机器无需安装 .NET 运行时
  dotnet publish src/S3ObjectStorage.Api -c "$CONFIG" -r "$RID" \
    --self-contained true -p:PublishSingleFile=true \
    -o "$OUT/$RID" "${VERSION_ARGS[@]}"
  echo "==> 完成：artifacts/publish/$RID（自包含单文件）"
else
  # 框架依赖：目标机器需 .NET 10 运行时；也是 Dockerfile 使用的形态
  dotnet publish src/S3ObjectStorage.Api -c "$CONFIG" \
    -o "$OUT/framework-dependent" "${VERSION_ARGS[@]}"
  echo "==> 完成：artifacts/publish/framework-dependent"
  echo "    运行：dotnet artifacts/publish/framework-dependent/S3ObjectStorage.Api.dll"
  echo "    容器：docker build -t s3objectstorage ."
fi
