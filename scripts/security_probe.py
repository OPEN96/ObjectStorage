#!/usr/bin/env python3
"""安全对抗探测：过期/篡改签名、越权、路径穿越、畸形输入、元数据滥用。"""

import datetime
import hashlib
import hmac
import http.client
import os
import sys
import time
import urllib.parse
import uuid

HOST = os.environ.get("S3_HOST", "127.0.0.1:9000")
AK = os.environ.get("S3_ACCESS_KEY", "AKIAIOSFODNN7EXAMPLE")
SK = os.environ.get("S3_SECRET_KEY", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")
# 每次运行使用独立桶名，保证脚本可重复执行。
RUN = uuid.uuid4().hex[:6]
BUCKET = f"sec-{RUN}"
OTHER_BUCKET = f"sec-{RUN}-other"
REGION = "us-east-1"

sys.path.insert(0, "scripts")
import presign as presign_mod  # noqa: E402
import smoke  # noqa: E402

results = []


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name} {detail}")


def signed(method, path, query=None, body=b"", headers=None, ak=AK, sk=SK):
    return smoke.request(method, HOST, path, query or {}, headers or {}, body, ak, sk, REGION)


def raw_payload(method, path, body, payload_hash, ak=AK, sk=SK):
    """声明与实际不一致的载荷哈希，用于验证服务端是否真的校验内容。"""
    now = datetime.datetime.now(datetime.timezone.utc)
    amz_date = now.strftime("%Y%m%dT%H%M%SZ")
    date_stamp = amz_date[:8]
    hdrs = {"host": HOST, "x-amz-date": amz_date, "x-amz-content-sha256": payload_hash}
    signed_headers = ";".join(sorted(hdrs))
    canonical_headers = "".join(f"{k}:{hdrs[k]}\n" for k in sorted(hdrs))
    scope = f"{date_stamp}/{REGION}/s3/aws4_request"
    cr = "\n".join([method, path, "", canonical_headers, signed_headers, payload_hash])
    sts = "\n".join(["AWS4-HMAC-SHA256", amz_date, scope, hashlib.sha256(cr.encode()).hexdigest()])
    key = hmac.new(("AWS4" + sk).encode(), date_stamp.encode(), hashlib.sha256).digest()
    for part in (REGION, "s3", "aws4_request"):
        key = hmac.new(key, part.encode(), hashlib.sha256).digest()
    sig = hmac.new(key, sts.encode(), hashlib.sha256).hexdigest()
    hdrs["authorization"] = (f"AWS4-HMAC-SHA256 Credential={ak}/{scope}, "
                             f"SignedHeaders={signed_headers}, Signature={sig}")
    conn = http.client.HTTPConnection(HOST, timeout=15)
    try:
        conn.request(method, path, body=body, headers=hdrs)
        r = conn.getresponse()
        return r.status, r.read()
    finally:
        conn.close()


def send(method, target, body=b"", headers=None, timeout=15):
    conn = http.client.HTTPConnection(HOST, timeout=timeout)
    try:
        conn.request(method, target, body=body, headers=headers or {})
        r = conn.getresponse()
        return r.status, r.read()
    finally:
        conn.close()


def pre(method, key, expires=3600, secret=SK):
    path = f"/{BUCKET}/{presign_mod.uri_encode(key)}"
    return presign_mod.presign(HOST, AK, secret, method, path, expires, REGION)


def main():
    print("准备测试桶 ...")
    signed("PUT", f"/{BUCKET}")
    signed("PUT", f"/{BUCKET}/obj.txt", body=b"hello-security")
    signed("PUT", f"/{BUCKET}/big.bin", body=b"B" * (2 * 1024 * 1024))

    print("=== 1. 预签名有效期 ===")
    # 有效期取 5 秒：客户端与服务器时钟可能存在数秒偏差，太短会把「未过期」误判成过期。
    url = pre("GET", "obj.txt", expires=5).replace(f"http://{HOST}", "")
    st, body = send("GET", url)
    check("有效期内可访问", st == 200, f"status={st}")
    time.sleep(6)
    st, body = send("GET", url)
    check("过期后拒绝", st == 403 and b"expired" in body.lower(), f"status={st}")

    print("=== 2. 签名篡改 / 换 key / 换密钥 ===")
    tampered = url.split("X-Amz-Signature=")[0] + "X-Amz-Signature=" + "0" * 64
    st, _ = send("GET", tampered)
    check("签名错误被拒绝", st == 403, f"status={st}")

    other = pre("GET", "obj.txt").replace("/obj.txt?", "/big.bin?").replace(f"http://{HOST}", "")
    st, _ = send("GET", other)
    check("改路径后签名失效", st == 403, f"status={st}")

    wrong = pre("GET", "obj.txt", secret=SK + "x").replace(f"http://{HOST}", "")
    st, _ = send("GET", wrong)
    check("换密钥后签名失效", st == 403, f"status={st}")

    print("=== 3. 未知 AccessKey ===")
    st, _, _ = signed("GET", "/", ak="AKIANOTEXIST00000001")
    check("未知 AccessKey 拒绝", st == 403, f"status={st}")

    print("=== 4. 载荷篡改（声明哈希与实际不符）===")
    st, body = raw_payload("PUT", f"/{BUCKET}/tamper.txt", b"tampered-body",
                           hashlib.sha256(b"original-body").hexdigest())
    check("内容哈希不符被拒绝", st in (400, 403), f"status={st}")

    print("=== 5. 路径穿越（已签名）===")
    st1, _, _ = signed("PUT", f"/{BUCKET}/%2e%2e%2f%2e%2e%2fescape.bin", body=b"x")
    st2, _, _ = signed("PUT", f"/{BUCKET}/..%2F..%2Fescape2.bin", body=b"x")
    st3, _, listing = signed("GET", f"/{BUCKET}")
    check("穿越请求不返回 5xx", st1 < 500 and st2 < 500, f"status={st1}/{st2}")
    check("未产生越界对象", b"../escape" not in listing, f"listing ok={st3}")

    print("=== 6. 超大自定义元数据 ===")
    st, _, _ = signed("PUT", f"/{BUCKET}/bigmeta.txt", body=b"x",
                      headers={"x-amz-meta-blob": "a" * 20000})
    check("超大元数据被拒绝", st in (400, 431, 413), f"status={st}")

    print("=== 7. 畸形 / 超大 XML ===")
    st, _, body = signed("POST", f"/{BUCKET}/mp.bin", {"uploads": ""})
    check("InitiateMultipartUpload", st == 200, f"status={st}")
    uid = body.split(b"<UploadId>")[1].split(b"</UploadId>")[0].decode()
    st, _, _ = signed("POST", f"/{BUCKET}/mp.bin", {"uploadId": uid},
                      body=b"<CompleteMultipartUpload><Part>")
    check("畸形 XML 返回 400 而非 500", st == 400, f"status={st}")
    st, _, _ = signed("POST", f"/{BUCKET}/mp.bin", {"uploadId": uid}, body=b"<a>" * 400000)
    check("超限 XML 被拒绝", st in (400, 413), f"status={st}")
    signed("DELETE", f"/{BUCKET}/mp.bin", {"uploadId": uid})

    print("=== 8. 跨桶 uploadId ===")
    signed("PUT", f"/{OTHER_BUCKET}")
    st, _, body = signed("POST", f"/{BUCKET}/cross.bin", {"uploads": ""})
    uid = body.split(b"<UploadId>")[1].split(b"</UploadId>")[0].decode()
    st, _, _ = signed("PUT", f"/{OTHER_BUCKET}/cross.bin", {"partNumber": "1", "uploadId": uid},
                      body=b"x" * 1024)
    check("跨桶复用 uploadId 被拒绝", st in (403, 404), f"status={st}")

    print("=== 9. 非法桶名 ===")
    for name in ["UPPER", "a", "a" * 64, "-start", "has_underscore", "double..dot"]:
        st, _, _ = signed("PUT", f"/{name}")
        check(f"非法桶名 {name[:12]!r}", st == 400, f"status={st}")

    print("=== 10. 管理端未授权 ===")
    st, _ = send("GET", "/admin/api/overview")
    check("未登录 401", st == 401, f"status={st}")
    st, _ = send("POST", "/admin/api/auth/login", body=b'{"token":"x"}',
                 headers={"Content-Type": "application/json", "Origin": "https://evil.example"})
    check("跨站 Origin 403", st == 403, f"status={st}")

    print("=== 11. 错误响应信息泄露 ===")
    st, _, body = signed("GET", f"/{BUCKET}/no-such-key.bin")
    text = body.decode("utf-8", "replace")
    check("404 无堆栈/内部路径", st == 404 and " at " not in text and "/app/" not in text,
          f"status={st}")

    print("=== 12. 注入式 key ===")
    key = urllib.parse.quote("a'; DROP TABLE Objects;--.bin", safe="")
    st, _, _ = signed("PUT", f"/{BUCKET}/{key}", body=b"x")
    st2, _, _ = signed("GET", f"/{BUCKET}")
    check("注入式 key 不影响元数据库", st in (200, 400) and st2 == 200, f"status={st}/{st2}")

    signed("DELETE", f"/{BUCKET}")
    signed("DELETE", f"/{OTHER_BUCKET}")
    passed = sum(1 for _, ok, _ in results if ok)
    print(f"\n结果：{passed}/{len(results)} 通过")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
