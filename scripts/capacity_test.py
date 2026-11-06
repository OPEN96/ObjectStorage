#!/usr/bin/env python3
"""大对象与分片上传的容量/完整性验证（服务端主机上运行，避免网络干扰）。"""

import hashlib
import os
import random
import sys
import tempfile
import time

from minio import Minio
from minio.error import S3Error

ENDPOINT = os.environ.get("S3_ENDPOINT", "127.0.0.1:9500")
AK = os.environ.get("S3_ACCESS_KEY", "AKIAIOSFODNN7EXAMPLE")
SK = os.environ.get("S3_SECRET_KEY", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")
BUCKET = "capacity"
KEY = "large-200mb.bin"
SIZE = 200 * 1024 * 1024
PART = 16 * 1024 * 1024
WORKDIR = os.environ.get("CAPACITY_WORKDIR", tempfile.gettempdir())
LOCAL = os.path.join(WORKDIR, "large-200mb.bin")
DOWN = os.path.join(WORKDIR, "large-downloaded.bin")

client = Minio(ENDPOINT, access_key=AK, secret_key=SK, secure=False, region="us-east-1")


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            block = f.read(4 << 20)
            if not block:
                break
            h.update(block)
    return h.hexdigest()


def mbps(nbytes, seconds):
    return nbytes / seconds / (1024 * 1024) if seconds > 0 else 0


def main():
    results = []

    def check(name, ok, detail=""):
        results.append((name, ok, detail))
        print(f"  [{'PASS' if ok else 'FAIL'}] {name} {detail}")

    if not client.bucket_exists(BUCKET):
        client.make_bucket(BUCKET)

    if not os.path.exists(LOCAL) or os.path.getsize(LOCAL) != SIZE:
        print(f"生成 {SIZE // (1024*1024)}MB 测试文件 ...")
        rnd = random.Random(20261105)
        with open(LOCAL, "wb") as f:
            remaining = SIZE
            while remaining:
                chunk = rnd.randbytes(min(4 << 20, remaining))
                f.write(chunk)
                remaining -= len(chunk)

    src_sha = sha256_file(LOCAL)
    print(f"源文件 sha256={src_sha[:16]}... size={os.path.getsize(LOCAL)}")

    # 清理上一次的残留
    try:
        client.remove_object(BUCKET, KEY)
    except S3Error:
        pass

    print("=== 1. 分片上传 200MB（16MB/片）===")
    t0 = time.time()
    etag = client.fput_object(BUCKET, KEY, LOCAL, part_size=PART,
                              metadata={"x-amz-meta-origin": "capacity-test"})
    put_secs = time.time() - t0
    parts_expected = (SIZE + PART - 1) // PART
    check("上传成功", True, f"{put_secs:.2f}s {mbps(SIZE, put_secs):.1f} MB/s etag={etag.etag}")
    check("ETag 带分片数", str(etag.etag).endswith(f"-{parts_expected}"),
          f"期望后缀 -{parts_expected}")

    print("=== 2. 元数据 ===")
    st = client.stat_object(BUCKET, KEY)
    check("对象大小一致", st.size == SIZE, f"{st.size}")
    check("自定义元数据保留", (st.metadata or {}).get("x-amz-meta-origin") == "capacity-test",
          str(st.metadata))

    print("=== 3. 全量下载并比对 sha256 ===")
    t0 = time.time()
    client.fget_object(BUCKET, KEY, DOWN)
    get_secs = time.time() - t0
    dst_sha = sha256_file(DOWN)
    check("内容一致", src_sha == dst_sha, f"{get_secs:.2f}s {mbps(SIZE, get_secs):.1f} MB/s")
    os.remove(DOWN)

    print("=== 4. 区间读取（50MB 起 1MB）===")
    offset, length = 50 * 1024 * 1024, 1 * 1024 * 1024
    resp = client.get_object(BUCKET, KEY, offset=offset, length=length)
    try:
        part = resp.read()
    finally:
        resp.close()
        resp.release_conn()
    with open(LOCAL, "rb") as f:
        f.seek(offset)
        expected = f.read(length)
    check("Range 内容一致", part == expected, f"{len(part)} bytes")

    print("=== 5. 手工分片：创建 → 分片 → 列举 → 完成 ===")
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import smoke

    def req(method, path, query=None, body=b"", headers=None):
        return smoke.request(method, ENDPOINT, path, query or {}, headers or {}, body, AK, SK)

    st, hd, body = req("POST", f"/{BUCKET}/manual-multipart.bin", {"uploads": ""})
    uid = body.split(b"<UploadId>")[1].split(b"</UploadId>")[0].decode()
    check("InitiateMultipartUpload", st == 200, uid[:16] + "...")

    etags = []
    with open(LOCAL, "rb") as f:
        for n in range(1, 4):
            data = f.read(5 * 1024 * 1024)
            st, hd, _ = req("PUT", f"/{BUCKET}/manual-multipart.bin",
                            {"partNumber": str(n), "uploadId": uid}, data)
            etags.append((n, hd.get("etag", "").strip('"')))
            check(f"UploadPart {n}", st == 200, hd.get("etag", ""))

    st, hd, body = req("GET", f"/{BUCKET}/manual-multipart.bin", {"uploadId": uid})
    check("ListParts 返回 3 片", body.count(b"<Part>") == 3, f"{body.count(b'<Part>')}")

    xml = "<CompleteMultipartUpload>" + "".join(
        f"<Part><PartNumber>{n}</PartNumber><ETag>\"{etag}\"</ETag></Part>" for n, etag in etags
    ) + "</CompleteMultipartUpload>"
    st, hd, body = req("POST", f"/{BUCKET}/manual-multipart.bin", {"uploadId": uid}, xml.encode())
    check("Complete 成功", st == 200, hd.get("etag", ""))
    st = client.stat_object(BUCKET, "manual-multipart.bin")
    check("合并后大小正确", st.size == 15 * 1024 * 1024, f"{st.size}")
    client.remove_object(BUCKET, "manual-multipart.bin")

    print("=== 6. 中止分片应清理 ===")
    st, hd, body = req("POST", f"/{BUCKET}/abort-me.bin", {"uploads": ""})
    uid = body.split(b"<UploadId>")[1].split(b"</UploadId>")[0].decode()
    with open(LOCAL, "rb") as f:
        req("PUT", f"/{BUCKET}/abort-me.bin", {"partNumber": "1", "uploadId": uid},
            f.read(5 * 1024 * 1024))
    st, hd, _ = req("DELETE", f"/{BUCKET}/abort-me.bin", {"uploadId": uid})
    check("AbortMultipartUpload", st in (200, 204), f"status={st}")
    st, hd, body = req("GET", f"/{BUCKET}", {"uploads": ""})
    check("Abort 后不再列出该任务", uid.encode() not in body, f"响应 {len(body)} 字节")

    client.remove_object(BUCKET, KEY)
    os.remove(LOCAL)

    passed = sum(1 for _, ok, _ in results if ok)
    print(f"\n结果：{passed}/{len(results)} 通过")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
