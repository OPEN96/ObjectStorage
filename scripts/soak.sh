#!/bin/bash
# Soak test: sustained mixed GET/PUT load with periodic resource sampling.
# usage: soak.sh <container> <seconds> <get-url-file> <put-url-file> <body-file>
set -u

NAME=${1:-s3c}
DURATION=${2:-600}
GET_URL=$(cat "$3")
PUT_URL=$(cat "$4")
BODY=$5
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WORKDIR=${S3BENCH_DIR:-$SCRIPT_DIR}
OUT=$WORKDIR/soak.csv

# 容器内 PID 命名空间独立：tini 是 1，dotnet 进程号不固定，先探一次。
APP_PID=$(docker exec "$NAME" sh -c 'for p in /proc/[0-9]*; do tr -d "\000" < "$p/cmdline" 2>/dev/null | grep -q "^dotnet" && basename "$p" && break; done' | tr -d '\r')
echo "app pid inside container: $APP_PID"

: > "$OUT"
setsid "$WORKDIR/loadgen" -u "$GET_URL" -t 6 -n 100000000 -w 2 > "$WORKDIR/soak-get.log" 2>&1 &
GET_PID=$!
setsid "$WORKDIR/loadgen" -u "$PUT_URL" -m PUT -f "$BODY" -t 2 -n 100000000 -w 2 > "$WORKDIR/soak-put.log" 2>&1 &
PUT_PID=$!

echo "ts,rss_kb,anon_kb,fds,threads,wal_bytes,container_mem" > "$OUT"
for _ in $(seq 1 $((DURATION / 10))); do
  SAMPLE=$(docker exec "$NAME" sh -c "grep VmRSS /proc/$APP_PID/status | awk '{print \$2}'; grep '^anon ' /sys/fs/cgroup/memory.stat | awk '{print \$2}'; ls /proc/$APP_PID/fd | wc -l; ls /proc/$APP_PID/task | wc -l; stat -c %s /data/metadata.db-wal 2>/dev/null || echo 0" 2>/dev/null | tr -d '\r')
  RSS=$(echo "$SAMPLE" | sed -n 1p)
  ANON=$(echo "$SAMPLE" | sed -n 2p)
  FD=$(echo "$SAMPLE" | sed -n 3p)
  TH=$(echo "$SAMPLE" | sed -n 4p)
  WAL=$(echo "$SAMPLE" | sed -n 5p)
  MEM=$(docker stats --no-stream --format '{{.MemUsage}}' "$NAME" 2>/dev/null | tr -d ' ')
  echo "$(date +%s),$RSS,$ANON,$FD,$TH,$WAL,$MEM" >> "$OUT"
  sleep 10
done

kill "$GET_PID" "$PUT_PID" 2>/dev/null
sleep 2
echo "soak done: $(($(wc -l < "$OUT") - 1)) samples"
tail -1 "$WORKDIR/soak-get.log"
tail -1 "$WORKDIR/soak-put.log"
