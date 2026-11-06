"""用 MinIO 官方 Python SDK 对服务端做端到端兼容性验证。

与 scripts/smoke.py（自实现 SigV4）以及 .NET 集成测试（AWS 官方 SDK）互为独立
第三方客户端，专门覆盖 MinIO 生态特有的行为：
  - 非 TLS 下的 UNSIGNED-PAYLOAD / STREAMING-UNSIGNED-PAYLOAD-TRAILER
  - PutObject 的 aws-chunked + checksum trailer
  - 大文件自动分片上传
  - ListObjects 的 encoding-type=url
  - 预签名 URL

用法：先启动服务端，再 `python scripts/minio_compat.py [endpoint]`。
"""

from __future__ import annotations

import io
import os
import sys
import traceback
import uuid

from minio import Minio
from minio.error import S3Error

ENDPOINT = os.environ.get("S3_ENDPOINT", "127.0.0.1:9000")
ACCESS_KEY = os.environ.get("S3_ACCESS_KEY", "AKIAEXAMPLELOCAL001")
SECRET_KEY = os.environ.get("S3_SECRET_KEY", "local-development-secret-please-change-me")


class Checker:
    def __init__(self) -> None:
        self.total = 0
        self.failed = 0

    def run(self, name, fn):
        self.total += 1
        try:
            detail = fn()
            print(f"  [PASS] {name}" + (f"  {detail}" if detail else ""))
        except Exception as exc:  # noqa: BLE001
            self.failed += 1
            print(f"  [FAIL] {name}: {type(exc).__name__}: {exc}")
            if not isinstance(exc, S3Error):
                traceback.print_exc(limit=3)

    def check(self, name, condition, detail=None):
        self.run(name, lambda: self._assert(condition, detail))

    @staticmethod
    def _assert(condition, detail):
        if not condition:
            raise AssertionError(f"断言失败: {detail!r}")
        return ""


def main() -> int:
    endpoint = sys.argv[1] if len(sys.argv) > 1 else ENDPOINT
    client = Minio(
        endpoint,
        access_key=ACCESS_KEY,
        secret_key=SECRET_KEY,
        secure=False,
        region="us-east-1",
    )
    checker = Checker()
    bucket = "minio-" + uuid.uuid4().hex[:10]
    print(f"MinIO Python SDK 兼容性验证 -> http://{endpoint}  bucket={bucket}\n")

    # 1. 列出桶（mc alias set 的连通性探测也走这一步）
    checker.run("ListBuckets", lambda: _list_buckets(client))

    # 2. 建桶
    checker.run("MakeBucket", lambda: client.make_bucket(bucket))

    # 3. BucketExists
    checker.check("BucketExists", client.bucket_exists(bucket) is True)

    # 4. 小对象上传（minio-py 在非 TLS 下用 UNSIGNED-PAYLOAD，可能带 checksum trailer）
    payload = b"hello from minio python sdk\n" * 8
    checker.run(
        "PutObject (small, unsigned)",
        lambda: client.put_object(
            bucket, "readme.txt", io.BytesIO(payload), len(payload), content_type="text/plain"
        ),
    )

    # 5. StatObject
    checker.check(
        "StatObject",
        _stat_size(client, bucket, "readme.txt") == len(payload),
    )

    # 6. GetObject 内容一致
    checker.check("GetObject", _get_bytes(client, bucket, "readme.txt") == payload)

    # 7. 带特殊字符的 key（encoding-type=url 路径）
    odd_key = "docs/2026 报告 (final)+100%.txt"
    checker.run(
        "PutObject (special chars in key)",
        lambda: client.put_object(bucket, odd_key, io.BytesIO(b"odd"), 3),
    )
    checker.check("GetObject (special chars)", _get_bytes(client, bucket, odd_key) == b"odd")

    # 8. ListObjects（MinIO SDK 默认会带 encoding-type=url）
    listed = list(client.list_objects(bucket, recursive=True))
    checker.check(
        "ListObjects (recursive)",
        {"readme.txt", odd_key}.issubset({o.object_name for o in listed}),
        [o.object_name for o in listed],
    )

    # 9. 大对象：触发 SDK 的自动分片上传（multipart）
    big = os.urandom(6 * 1024 * 1024 + 123)
    checker.run(
        f"PutObject (multipart, {len(big)} bytes)",
        lambda: client.put_object(
            bucket, "big.bin", io.BytesIO(big), len(big), part_size=5 * 1024 * 1024
        ),
    )
    checker.check("GetObject (multipart)", _get_bytes(client, bucket, "big.bin") == big)

    # 10. 预签名 GET URL
    import urllib.request

    checker.run("PresignedGetObject", lambda: None)  # 占位，下面单独做 URL 检查
    url = client.presigned_get_object(bucket, "readme.txt")
    with urllib.request.urlopen(url, timeout=20) as response:
        body = response.read()
    checker.check("PresignedGetObject (anonymous GET)", body == payload, (response.status, len(body)))

    # 11. CopyObject
    checker.run(
        "CopyObject",
        lambda: client.copy_object(bucket, "copy.txt", CopySource(bucket, "readme.txt")),
    )
    checker.check("GetObject (copied)", _get_bytes(client, bucket, "copy.txt") == payload)

    # 12. 清理
    for key in ("readme.txt", odd_key, "big.bin", "copy.txt"):
        checker.run(f"RemoveObject {key}", lambda k=key: client.remove_object(bucket, k))
    checker.run("RemoveBucket", lambda: client.remove_bucket(bucket))

    print(f"\n结果：{checker.total - checker.failed}/{checker.total} 通过")
    return 1 if checker.failed else 0


def _list_buckets(client):
    names = [b.name for b in client.list_buckets()]
    return f"buckets={len(names)}"


def _stat_size(client, bucket, key):
    return client.stat_object(bucket, key).size


def _get_bytes(client, bucket, key):
    response = None
    try:
        response = client.get_object(bucket, key)
        return response.read()
    finally:
        if response is not None:
            response.close()
            response.release_conn()


def CopySource(bucket, key):
    from minio.commonconfig import CopySource as _CopySource

    return _CopySource(bucket, key)


if __name__ == "__main__":
    sys.exit(main())
