#!/usr/bin/env bash
# 用 MinIO 官方 mc CLI 做端到端兼容性验证（真实 Go 客户端）。
#
# 前置条件：
#   1. 服务端已在 9000 端口运行（dotnet run）；
#   2. 已安装 mc：https://min.io/docs/minio/linux/reference/minio-mc.html
#      或者设置 MC_BIN=/path/to/mc。
#
# 用法：bash scripts/mc_compat.sh [endpoint] [access-key] [secret-key]
# 默认值对应 appsettings.Development.json 里的本地开发凭证。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MC="${MC_BIN:-$(command -v mc || true)}"
ENDPOINT="${1:-http://127.0.0.1:9000}"
ACCESS_KEY="${2:-AKIAEXAMPLELOCAL001}"
SECRET_KEY="${3:-local-development-secret-please-change-me}"
ALIAS="s3objstore-smoke"
BUCKET="mc-smoke-$(date +%Y%m%d%H%M%S)"

if [ -z "$MC" ] || [ ! -x "$MC" ]; then
  echo "找不到 mc 可执行文件。请安装 mc，或设置 MC_BIN 指向它。" >&2
  exit 127
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/s3objstore-mc-XXXXXX")"
CONFIG="$WORK/.mc-config"
mkdir -p "$CONFIG"

# mc 在 Windows 上不认 MC_CONFIG_DIR 环境变量，只会回落到 %USERPROFILE%\mc；
# 那个文件常被其它 mc 进程占用，导致 `alias set` 直接失败。改用 `--config-dir`
# 全局参数（跨平台可靠），Windows 下再用 cygpath 转成原生路径。
export MC_CONFIG_DIR="$CONFIG"
if command -v cygpath >/dev/null 2>&1; then
  CONFIG_ARG="$(cygpath -w "$CONFIG")"
else
  CONFIG_ARG="$CONFIG"
fi

# 所有 mc 调用都走这个包装函数，确保共用同一份临时配置目录。
mc() { "$MC" --config-dir "$CONFIG_ARG" --no-color "$@"; }

cleanup() {
  mc alias remove "$ALIAS" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

TOTAL=0
FAILED=0
check() {
  local name="$1"
  shift
  TOTAL=$((TOTAL + 1))
  if "$@" >"$WORK/last.log" 2>&1; then
    echo "  [PASS] $name"
  else
    FAILED=$((FAILED + 1))
    echo "  [FAIL] $name"
    sed -n '1,12p' "$WORK/last.log"
  fi
}


echo "mc CLI 兼容性验证 -> $ENDPOINT  bucket=$BUCKET"
echo

check "alias set (GetBucketLocation / ListBuckets 探测)" \
  mc alias set "$ALIAS" "$ENDPOINT" "$ACCESS_KEY" "$SECRET_KEY" --api S3v4

# 守卫：alias 没设成 S3 端点时，mc 会把 "$ALIAS/$BUCKET" 当成相对路径写到本地磁盘，
# 后续用例会"全部通过"却完全没测到服务端。宁可中止，也不要自欺欺人的绿灯。
if ! mc alias list "$ALIAS" --json 2>/dev/null | grep -qi '"api":"s3v4"'; then
  echo
  echo "中止：alias '$ALIAS' 不是 S3v4 端点（mc 会退化为本地文件系统），后续结果没有意义。" >&2
  echo "      常见原因是 mc 配置目录不可写或已被其它进程占用。" >&2
  exit 1
fi
check "mc ls / (ListBuckets)" mc ls "$ALIAS"
check "mc mb (PutBucket)" mc mb "$ALIAS/$BUCKET"

# 小文件，文件名含空格、中文、'+'、'%' —— 覆盖 encoding-type=url
SMALL="2026 报告 (final)+100%.txt"
printf 'hello from mc cli\n' >"$WORK/$SMALL"
check "mc cp 小文件（特殊字符 key）" mc cp "$WORK/$SMALL" "$ALIAS/$BUCKET/$SMALL"
check "mc ls 桶内对象" mc ls "$ALIAS/$BUCKET"

# 元数据校验用 `mc ls --json` 而不是 `mc stat`：mc RELEASE.2022-12 在 Windows 上
# 对 IPv4 endpoint 的 `mc stat` 一律报 not found（连桶都 stat 不出来），是 mc 自身的
# 缺陷，与服务端无关；`ls --json` 能完整验证 key 往返编码、size 与 etag。
LSJSON="$(mc ls --json "$ALIAS/$BUCKET/$SMALL" 2>"$WORK/lsjson.log")"
TOTAL=$((TOTAL + 1))
if printf '%s' "$LSJSON" | grep -qF "\"key\":\"$SMALL\"" \
    && printf '%s' "$LSJSON" | grep -q '"size":18' \
    && printf '%s' "$LSJSON" | grep -q '"etag":'; then
  echo "  [PASS] mc ls --json 元数据（特殊字符 key 往返）"
else
  FAILED=$((FAILED + 1)); echo "  [FAIL] mc ls --json 元数据"; echo "$LSJSON"; cat "$WORK/lsjson.log"
fi

# 大文件：触发 minio-go 的流式分块上传 / 自动分片
python3 - "$WORK/big.bin" <<'PY'
import os, sys
with open(sys.argv[1], "wb") as f:
    f.write(os.urandom(20 * 1024 * 1024 + 321))
PY
check "mc cp 大文件（20 MiB，分片上传）" mc cp "$WORK/big.bin" "$ALIAS/$BUCKET/big.bin"

# 下载回来校验内容一致
mc cat "$ALIAS/$BUCKET/$SMALL" >"$WORK/small.download" 2>"$WORK/cat-small.log"
TOTAL=$((TOTAL + 1))
if cmp -s "$WORK/$SMALL" "$WORK/small.download"; then
  echo "  [PASS] mc cat 小文件内容一致"
else
  FAILED=$((FAILED + 1)); echo "  [FAIL] mc cat 小文件内容不一致"; cat "$WORK/cat-small.log"
fi

mc cat "$ALIAS/$BUCKET/big.bin" >"$WORK/big.download" 2>"$WORK/cat-big.log"
TOTAL=$((TOTAL + 1))
if cmp -s "$WORK/big.bin" "$WORK/big.download"; then
  echo "  [PASS] mc cat 大文件内容一致"
else
  FAILED=$((FAILED + 1)); echo "  [FAIL] mc cat 大文件内容不一致"; cat "$WORK/cat-big.log"
fi

# 预签名 URL（匿名 GET）
# mc 的预签名命令是 `share download`，输出为 "Share: <url>"，不是 `presign`。
PRESIGN="$(mc share download --expire 10m "$ALIAS/$BUCKET/$SMALL" \
  2>"$WORK/presign.log" | sed -n 's/^Share: //p')"
TOTAL=$((TOTAL + 1))
if [ -n "$PRESIGN" ] && curl -fsS --max-time 30 "$PRESIGN" -o "$WORK/presigned.download" 2>>"$WORK/presign.log" \
    && cmp -s "$WORK/$SMALL" "$WORK/presigned.download"; then
  echo "  [PASS] mc presign get（匿名下载）"
else
  FAILED=$((FAILED + 1)); echo "  [FAIL] mc presign get"; cat "$WORK/presign.log"
fi

check "mc rm --recursive --force" mc rm --recursive --force "$ALIAS/$BUCKET"
check "mc rb（删除空桶）" mc rb "$ALIAS/$BUCKET"

echo
echo "结果：$((TOTAL - FAILED))/$TOTAL 通过"
exit $((FAILED > 0 ? 1 : 0))
