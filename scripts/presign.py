#!/usr/bin/env python3
"""生成 S3 预签名 URL（SigV4 query 签名），便于用 curl / ab / wrk 等通用工具直接压测。

用法：
    python scripts/presign.py --host 127.0.0.1:9000 \
        --access-key KEY --secret SECRET --bucket bench --key obj.bin \
        --method GET --expires 3600
"""

import argparse
import datetime
import hashlib
import hmac
import sys

UNRESERVED = set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")


def uri_encode(value: str) -> str:
    out = []
    for char in value:
        if char in UNRESERVED:
            out.append(char)
        else:
            out.append("".join("%%%02X" % b for b in char.encode("utf-8")))
    return "".join(out)


def sign(key: bytes, msg: str) -> bytes:
    return hmac.new(key, msg.encode("utf-8"), hashlib.sha256).digest()


def presign(host: str, access_key: str, secret: str, method: str, path: str,
            expires: int = 3600, region: str = "us-east-1") -> str:
    now = datetime.datetime.now(datetime.timezone.utc)
    amz_date = now.strftime("%Y%m%dT%H%M%SZ")
    date_stamp = now.strftime("%Y%m%d")
    scope = f"{date_stamp}/{region}/s3/aws4_request"

    query = {
        "X-Amz-Algorithm": "AWS4-HMAC-SHA256",
        "X-Amz-Credential": f"{access_key}/{scope}",
        "X-Amz-Date": amz_date,
        "X-Amz-Expires": str(expires),
        "X-Amz-SignedHeaders": "host",
    }
    canonical_query = "&".join(f"{uri_encode(k)}={uri_encode(v)}" for k, v in sorted(query.items()))
    canonical_request = "\n".join([
        method,
        path,
        canonical_query,
        f"host:{host}\n",
        "host",
        "UNSIGNED-PAYLOAD",
    ])
    string_to_sign = "\n".join([
        "AWS4-HMAC-SHA256",
        amz_date,
        scope,
        hashlib.sha256(canonical_request.encode("utf-8")).hexdigest(),
    ])
    key = sign(("AWS4" + secret).encode("utf-8"), date_stamp)
    for part in (region, "s3", "aws4_request"):
        key = sign(key, part)
    signature = hmac.new(key, string_to_sign.encode("utf-8"), hashlib.sha256).hexdigest()
    return f"http://{host}{path}?{canonical_query}&X-Amz-Signature={signature}"


def main() -> int:
    parser = argparse.ArgumentParser(description="生成 S3 预签名 URL")
    parser.add_argument("--host", default="127.0.0.1:9000")
    parser.add_argument("--access-key", required=True)
    parser.add_argument("--secret", required=True)
    parser.add_argument("--bucket", required=True)
    parser.add_argument("--key", default="", help="对象名，留空表示桶级操作")
    parser.add_argument("--method", default="GET")
    parser.add_argument("--expires", type=int, default=3600)
    parser.add_argument("--region", default="us-east-1")
    args = parser.parse_args()

    path = f"/{args.bucket}"
    if args.key:
        path += "/" + uri_encode(args.key)
    print(presign(args.host, args.access_key, args.secret, args.method.upper(),
                  path, args.expires, args.region))
    return 0


if __name__ == "__main__":
    sys.exit(main())
