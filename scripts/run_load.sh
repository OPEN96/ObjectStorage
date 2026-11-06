#!/usr/bin/env bash
# 用 loadgen（C，keep-alive）跑单尺寸的 GET/PUT 并发矩阵
# 用法：bash run_load.sh <size_bytes> <label> [counts_comma]
set -u
SIZE=$1
LABEL=$2
COUNTS=${3:-500,2000,8000,16000}
# 凭据与地址可用环境变量覆盖（默认值为 AWS 文档示例凭据，仅作占位）。
HOST=${S3_HOST:-127.0.0.1:9000}
AK=${S3_ACCESS_KEY:-AKIAIOSFODNN7EXAMPLE}
SK=${S3_SECRET_KEY:-wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY}
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOADGEN=${LOADGEN:-$SCRIPT_DIR/loadgen}
BUCKET=bench
WORK=/tmp/abwork
mkdir -p "$WORK"
BODY="$WORK/body-$SIZE"
if [ ! -f "$BODY" ] || [ "$(stat -c%s "$BODY")" != "$SIZE" ]; then
  head -c "$SIZE" /dev/urandom > "$BODY"
fi

presign() {
  python3 "$SCRIPT_DIR/presign.py" --host "$HOST" --access-key "$AK" --secret "$SK" \
    --bucket "$BUCKET" --key "load-$SIZE.bin" --method "$1"
}

curl -s -o /dev/null -X PUT --data-binary @"$BODY" "$(presign PUT)"
GU=$(presign GET)
PU=$(presign PUT)

i=0
for c in 1 8 32 64; do
  n=$(echo "$COUNTS" | cut -d, -f$((i+1)))
  i=$((i+1))
  per=$(( n / c )); [ "$per" -lt 20 ] && per=20
  printf '%-8s GET ' "$LABEL"
  timeout 300 "$LOADGEN" -u "$GU" -t "$c" -n "$per" || echo "  (超时或失败)"
done

for c in 1 8 32; do
  per=$(( n / c / 2 )); [ "$per" -lt 20 ] && per=20
  printf '%-8s PUT ' "$LABEL"
  timeout 300 "$LOADGEN" -u "$PU" -m PUT -f "$BODY" -t "$c" -n "$per" || echo "  (超时或失败)"
done
