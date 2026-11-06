#!/usr/bin/env python3
"""针对本服务的端到端冒烟脚本（不依赖 aws cli / SDK）。

用法：
    python scripts/smoke.py [--host 127.0.0.1:9000] [--access-key KEY] [--secret SEC]

脚本自己实现 AWS SigV4 签名，因此可以作为「与服务端实现无关」的第三方校验。
"""

import argparse
import datetime
import hashlib
import hmac
import http.client
import sys
import xml.etree.ElementTree as ET

AMZ_DATE = "%Y%m%dT%H%M%SZ"
UNSIGNED_PAYLOAD = "UNSIGNED-PAYLOAD"


def sign(key, msg):
    return hmac.new(key, msg.encode("utf-8"), hashlib.sha256).digest()


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


UNRESERVED = set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")


def uri_encode(value: str) -> str:
    """RFC 3986 百分号编码（SigV4 规范要求）。"""
    encoded = []
    for char in value:
        encoded.append(char if char in UNRESERVED else "".join("%%%02X" % b for b in char.encode("utf-8")))
    return "".join(encoded)


class CIDict(dict):
    """大小写不敏感的响应头字典。HTTP 头名本身是大小写不敏感的。"""

    def __init__(self, data):
        items = data.items() if isinstance(data, dict) else data
        super().__init__({str(k).lower(): v for k, v in items})

    def __contains__(self, key):
        return super().__contains__(key.lower())

    def __getitem__(self, key):
        return super().__getitem__(key.lower())

    def get(self, key, default=None):
        return super().get(key.lower(), default)


def canonical_headers(headers):
    return "".join(f"{k}:{' '.join(str(v).split())}\n" for k, v in sorted(headers.items()))


def request(method, host, path, query, headers, body=b"", access_key="", secret="", region="us-east-1", unsigned=False):
    """发送一次带 SigV4 签名的请求，返回 (status, headers, body)。"""
    now = datetime.datetime.now(datetime.timezone.utc)
    amz_date = now.strftime(AMZ_DATE)
    date_stamp = now.strftime("%Y%m%d")

    payload_hash = UNSIGNED_PAYLOAD if unsigned else sha256_hex(body)

    lower = {k.lower(): v for k, v in headers.items()}
    lower["x-amz-date"] = amz_date
    lower["x-amz-content-sha256"] = payload_hash
    lower["host"] = host

    signed_headers = ";".join(sorted(lower))
    canonical_request = "\n".join([
        method,
        path,
        "&".join(f"{uri_encode(k)}={uri_encode(v)}" for k, v in sorted(query.items())),
        canonical_headers(lower),
        signed_headers,
        payload_hash,
    ])

    scope = f"{date_stamp}/{region}/s3/aws4_request"
    string_to_sign = "\n".join([
        "AWS4-HMAC-SHA256",
        amz_date,
        scope,
        sha256_hex(canonical_request.encode("utf-8")),
    ])

    signing_key = sign(("AWS4" + secret).encode("utf-8"), date_stamp)
    for part in (region, "s3", "aws4_request"):
        signing_key = sign(signing_key, part)
    signature = hmac.new(signing_key, string_to_sign.encode("utf-8"), hashlib.sha256).hexdigest()

    lower["authorization"] = (
        f"AWS4-HMAC-SHA256 Credential={access_key}/{scope}, "
        f"SignedHeaders={signed_headers}, Signature={signature}"
    )

    target = path + ("?" + "&".join(f"{uri_encode(k)}={uri_encode(v)}" for k, v in query.items()) if query else "")
    conn = http.client.HTTPConnection(host, timeout=30)
    try:
        conn.request(method, target, body=body, headers=lower)
        response = conn.getresponse()
        data = response.read()
        return response.status, CIDict(response.getheaders()), data
    finally:
        conn.close()


class Checker:
    def __init__(self):
        self.passed = 0
        self.failed = 0

    def check(self, name, ok, detail=""):
        if ok:
            self.passed += 1
            print(f"  [OK]   {name}")
        else:
            self.failed += 1
            print(f"  [FAIL] {name} {detail}")


def local(tag: str) -> str:
    """去掉 xmlns 前缀，只保留本地元素名。"""
    return tag.rsplit("}", 1)[-1]


def child_text(element, name: str):
    """按本地名取直接子元素的文本（服务端响应带 xmlns，需要忽略命名空间）。"""
    for child in element:
        if local(child.tag) == name:
            return child.text
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1:9000")
    parser.add_argument("--access-key", default="AKIAEXAMPLELOCAL001")
    parser.add_argument("--secret", default="local-development-secret-please-change-me")
    parser.add_argument("--region", default="us-east-1")
    args = parser.parse_args()

    checker = Checker()
    bucket = "smoke-" + datetime.datetime.now().strftime("%H%M%S")

    def call(method, path, query=None, headers=None, body=b""):
        return request(
            method, args.host, path, query or {}, headers or {}, body,
            args.access_key, args.secret, args.region,
        )

    print(f"== 冒烟测试 {args.host} ==")

    status, _, body = call("PUT", f"/{bucket}")
    checker.check("CreateBucket", status == 200, status)

    status, _, body = call("GET", "/")
    names = [el.text for el in ET.fromstring(body).iter() if local(el.tag) == "Name"]
    checker.check("ListBuckets 包含新桶", status == 200 and bucket in names, names)

    payload = b"hello workbuddy object storage"
    status, _, _ = call(
        "PUT", f"/{bucket}/docs/hello.txt",
        headers={"content-type": "text/plain", "x-amz-meta-source": "smoke"},
        body=payload,
    )
    checker.check("PutObject", status == 200, status)

    status, headers, body = call("GET", f"/{bucket}/docs/hello.txt")
    checker.check("GetObject 内容一致", status == 200 and body == payload, (status, body[:40]))
    checker.check("GetObject 返回自定义元数据", headers.get("x-amz-meta-source") == "smoke", headers.get("x-amz-meta-source"))

    status, headers, _ = call("HEAD", f"/{bucket}/docs/hello.txt")
    checker.check("HeadObject", status == 200 and int(headers.get("content-length", 0)) == len(payload), status)

    status, headers, body = call("GET", f"/{bucket}/docs/hello.txt", headers={"range": "bytes=0-4"})
    checker.check("Range GET 返回 206", status == 206 and body == b"hello", (status, body))

    status, _, body = call("GET", f"/{bucket}", {"list-type": "2", "prefix": "docs/", "delimiter": "/"})
    root = ET.fromstring(body)
    keys = [child_text(child, "Key") for child in root if local(child.tag) == "Contents"]
    prefixes = [child_text(child, "Prefix") for child in root if local(child.tag) == "CommonPrefixes"]
    checker.check(
        "ListObjectsV2 + prefix/delimiter",
        status == 200 and keys == ["docs/hello.txt"] and prefixes == [],
        (status, keys, prefixes),
    )

    # 分片上传
    status, _, body = call("POST", f"/{bucket}/big.bin", {"uploads": ""})
    upload_id = next(el.text for el in ET.fromstring(body).iter() if local(el.tag) == "UploadId")
    checker.check("CreateMultipartUpload", status == 200 and bool(upload_id), status)

    part1 = b"1" * (1024 * 100)
    part2 = b"2" * 1024
    status, headers, _ = call("PUT", f"/{bucket}/big.bin", {"partNumber": "1", "uploadId": upload_id}, body=part1)
    status2, headers2, _ = call("PUT", f"/{bucket}/big.bin", {"partNumber": "2", "uploadId": upload_id}, body=part2)
    checker.check("UploadPart x2", status == 200 and status2 == 200, (status, status2))

    xml_body = (
        "<CompleteMultipartUpload>"
        f"<Part><PartNumber>1</PartNumber><ETag>{headers['etag']}</ETag></Part>"
        f"<Part><PartNumber>2</PartNumber><ETag>{headers2['etag']}</ETag></Part>"
        "</CompleteMultipartUpload>"
    ).encode()
    status, _, body = call("POST", f"/{bucket}/big.bin", {"uploadId": upload_id}, body=xml_body)
    ok = status == 200 and "-2" in next(el.text for el in ET.fromstring(body).iter() if local(el.tag) == "ETag")
    checker.check("CompleteMultipartUpload", ok, status)

    status, _, body = call("GET", f"/{bucket}/big.bin")
    checker.check("分片合并后内容正确", status == 200 and body == part1 + part2, (status, len(body)))

    delete_body = ("<Delete><Object><Key>docs/hello.txt</Key></Object></Delete>").encode()
    status, _, body = call("POST", f"/{bucket}", {"delete": ""},
                           headers={"content-md5": __import__("base64").b64encode(hashlib.md5(delete_body).digest()).decode()},
                           body=delete_body)
    deleted = [el for el in ET.fromstring(body).iter() if local(el.tag) == "Key"]
    checker.check("DeleteObjects", status == 200 and deleted and deleted[0].text == "docs/hello.txt", (status, body[:120]))

    status, _, _ = call("DELETE", f"/{bucket}/big.bin")
    checker.check("DeleteObject", status == 204, status)

    status, _, _ = call("HEAD", f"/{bucket}")
    checker.check("HeadBucket", status == 200, status)

    status, _, _ = call("DELETE", f"/{bucket}")
    checker.check("DeleteBucket", status == 204, status)

    status, _, _ = call("GET", f"/{bucket}/ghost.txt")
    checker.check("读取已删除对象返回 404", status == 404, status)

    # 故意用错密钥
    bad = request("GET", args.host, "/", {}, {}, b"", args.access_key, args.secret + "wrong", args.region)
    checker.check("错误签名被拒绝 403", bad[0] == 403, bad[0])

    print(f"\n通过 {checker.passed} 项，失败 {checker.failed} 项")
    return 1 if checker.failed else 0


if __name__ == "__main__":
    sys.exit(main())
