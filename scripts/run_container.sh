#!/bin/bash
# (Re)create one S3ObjectStorage test container with the hardened production flags.
# usage: run_container.sh <name> <host-port> <image> <volume>
set -euo pipefail

NAME=$1
PORT=$2
IMAGE=$3
VOL=$4

# 测试容器引导凭据，可用环境变量覆盖（请勿把真实凭据提交进仓库）。
ADMIN_TOKEN=${ADMIN_TOKEN:-change-me-admin-token}
TEST_AK=${TEST_AK:-AKIAIOSFODNN7EXAMPLE}
TEST_SK=${TEST_SK:-wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY}

docker rm -f "$NAME" >/dev/null 2>&1 || true

docker run -d --name "$NAME" \
  --restart unless-stopped \
  --init \
  --read-only \
  --tmpfs /tmp:rw,noexec,nosuid,size=128m \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  --memory 1g --memory-swap 1g \
  --cpus 2 \
  --ulimit nofile=65536:65536 \
  --log-driver json-file --log-opt max-size=20m --log-opt max-file=5 \
  -p "$PORT":9000 \
  -v "$VOL":/data \
  -e "S3STORAGE_Admin__Token=$ADMIN_TOKEN" \
  -e S3STORAGE_S3__RequireAuthentication=true \
  -e S3STORAGE_S3__Region=us-east-1 \
  -e S3STORAGE_Storage__VerifyPayloadHash=true \
  -e "S3STORAGE_Storage__BootstrapAccessKeys__0__AccessKeyId=$TEST_AK" \
  -e "S3STORAGE_Storage__BootstrapAccessKeys__0__SecretAccessKey=$TEST_SK" \
  -e S3STORAGE_Storage__BootstrapAccessKeys__0__IsAdmin=true \
  "$IMAGE" >/dev/null

for _ in $(seq 1 60); do
  STATUS=$(docker inspect "$NAME" --format '{{.State.Health.Status}}' 2>/dev/null || echo starting)
  if [ "$STATUS" = "healthy" ]; then
    echo "$NAME healthy"
    exit 0
  fi
  if [ "$STATUS" = "unhealthy" ]; then
    echo "$NAME unhealthy"
    docker logs --tail 20 "$NAME"
    exit 1
  fi
  sleep 1
done

echo "$NAME timeout waiting for healthy"
docker logs --tail 30 "$NAME"
exit 1
