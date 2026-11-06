#!/usr/bin/env bash
# 基于 ab 的并发压测（预签名 URL，客户端不做签名运算）
# 用法：bash ab_matrix.sh <size_bytes> <label>
set -u
# 凭据与地址可用环境变量覆盖（默认值为 AWS 文档示例凭据，仅作占位）。
HOST=${S3_HOST:-127.0.0.1:9000}
AK=${S3_ACCESS_KEY:-AKIAIOSFODNN7EXAMPLE}
SK=${S3_SECRET_KEY:-wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY}
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUCKET=bench
WORK=/tmp/abwork
mkdir -p "$WORK"
size=$1
name=$2
body_file="$WORK/body-$size"
if [ ! -f "$body_file" ] || [ "$(stat -c%s "$body_file")" != "$size" ]; then
  head -c "$size" /dev/urandom > "$body_file"
fi

url() {
  python3 "$SCRIPT_DIR/presign.py" --host "$HOST" --access-key "$AK" --secret "$SK" \
    --bucket "$BUCKET" --key "$2" --method "$1"
}

run() { # method key concurrency requests
  local method=$1 key=$2 c=$3 n=$4 extra=()
  local u; u=$(url "$method" "$key")
  if [ "$method" = "PUT" ]; then
    extra=(-p "$body_file" -T application/octet-stream)
  fi
  local out
  out=$(ab -k -q -c "$c" -n "$n" -s 180 "${extra[@]}" "$u" 2>&1)
  local rps mean p50 p95 p99 failed non2xx
  rps=$(printf '%s' "$out" | awk '/Requests per second/{print $4}')
  mean=$(printf '%s' "$out" | awk '/^Time per request/{print $4; exit}')
  p50=$(printf '%s' "$out" | awk '$1=="50%"{print $2}')
  p95=$(printf '%s' "$out" | awk '$1=="95%"{print $2}')
  p99=$(printf '%s' "$out" | awk '$1=="99%"{print $2}')
  failed=$(printf '%s' "$out" | awk '/Failed requests/{print $3}')
  non2xx=$(printf '%s' "$out" | awk '/Non-2xx responses/{print $3}')
  printf '%-14s %-4s c=%-3s n=%-5s rps=%-8s mean=%-8s p50=%-8s p95=%-8s p99=%-8s failed=%-4s non2xx=%s\n' \
    "$name" "$method" "$c" "$n" "${rps:-ERR}" "${mean:-ERR}" "${p50:-ERR}" "${p95:-ERR}" "${p99:-ERR}" \
    "${failed:-0}" "${non2xx:-0}" | tee -a /tmp/ab_results.txt
}

key="ab-$size.bin"
curl -s -o /dev/null -X PUT --data-binary @"$body_file" "$(url PUT "$key")"

case $size in
  4096)    gN=(600 1600 3200 6400); pN=(300 800 1600);;
  262144)  gN=(200 400 800 1600);   pN=(100 200 400);;
  *)       gN=(20 40 80 128);       pN=(16 32 48);;
esac
i=0
for c in 1 8 32 64; do
  run GET "$key" "$c" "${gN[$i]}"
  i=$((i+1))
done
i=0
for c in 1 8 32; do
  run PUT "$key" "$c" "${pN[$i]}"
  i=$((i+1))
done
