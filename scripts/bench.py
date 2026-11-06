#!/usr/bin/env python3
"""针对本服务的并发性能基准（不依赖 aws cli / SDK）。

用法：
    python scripts/bench.py --host 127.0.0.1:9000 \
        --access-key KEY --secret SECRET --bucket bench

与 smoke.py 的区别：
- 每个工作线程复用一条 HTTP 长连接（避免把 TCP 握手算进延迟）；
- 同一天内复用 SigV4 派生密钥与相同请求的签名（减少压测端 CPU 开销）；
- 输出 P50 / P90 / P95 / P99 / 最大值与吞吐，用于容量评估与回归对比。

注意：客户端本身也会吃 CPU，压测结果应结合服务端 CPU 使用率一起看。
"""

import argparse
import datetime
import hashlib
import hmac
import http.client
import statistics
import sys
import threading
import time

AMZ_DATE = "%Y%m%dT%H%M%SZ"
UNRESERVED = set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")


def _sign(key: bytes, msg: str) -> bytes:
    return hmac.new(key, msg.encode("utf-8"), hashlib.sha256).digest()


def uri_encode(value: str) -> str:
    out = []
    for char in value:
        if char in UNRESERVED:
            out.append(char)
        else:
            out.append("".join("%%%02X" % b for b in char.encode("utf-8")))
    return "".join(out)


class Signer:
    """SigV4 签名器，缓存派生密钥与最近一次的请求签名。"""

    def __init__(self, host: str, access_key: str, secret: str, region: str = "us-east-1") -> None:
        self.host = host
        self.access_key = access_key
        self.secret = secret
        self.region = region
        self._key_cache: dict[str, bytes] = {}
        self._sig_cache: dict[tuple, tuple[str, str]] = {}
        self._lock = threading.Lock()

    def _signing_key(self, date_stamp: str) -> bytes:
        with self._lock:
            cached = self._key_cache.get(date_stamp)
            if cached is not None:
                return cached
        key = _sign(("AWS4" + self.secret).encode("utf-8"), date_stamp)
        for part in (self.region, "s3", "aws4_request"):
            key = _sign(key, part)
        with self._lock:
            self._key_cache[date_stamp] = key
        return key

    def sign(self, method: str, path: str, query: dict, payload_hash: str) -> dict:
        now = datetime.datetime.now(datetime.timezone.utc)
        amz_date = now.strftime(AMZ_DATE)
        date_stamp = now.strftime("%Y%m%d")

        cache_key = (method, path, tuple(sorted(query.items())), payload_hash, amz_date)
        with self._lock:
            hit = self._sig_cache.get(cache_key)
            if hit is not None:
                headers, signed_headers = hit
                return {"x-amz-date": amz_date, "x-amz-content-sha256": payload_hash,
                        "authorization": headers, "signed-headers": signed_headers}

        canonical_headers = (
            f"host:{self.host}\n"
            f"x-amz-content-sha256:{payload_hash}\n"
            f"x-amz-date:{amz_date}\n"
        )
        signed_headers = "host;x-amz-content-sha256;x-amz-date"
        canonical_request = "\n".join([
            method,
            path,
            "&".join(f"{uri_encode(k)}={uri_encode(v)}" for k, v in sorted(query.items())),
            canonical_headers,
            signed_headers,
            payload_hash,
        ])
        scope = f"{date_stamp}/{self.region}/s3/aws4_request"
        string_to_sign = "\n".join([
            "AWS4-HMAC-SHA256",
            amz_date,
            scope,
            hashlib.sha256(canonical_request.encode("utf-8")).hexdigest(),
        ])
        signature = hmac.new(self._signing_key(date_stamp), string_to_sign.encode("utf-8"),
                             hashlib.sha256).hexdigest()
        authorization = (f"AWS4-HMAC-SHA256 Credential={self.access_key}/{scope}, "
                         f"SignedHeaders={signed_headers}, Signature={signature}")
        with self._lock:
            if len(self._sig_cache) > 4096:
                self._sig_cache.clear()
            self._sig_cache[cache_key] = (authorization, signed_headers)
        return {"x-amz-date": amz_date, "x-amz-content-sha256": payload_hash,
                "authorization": authorization, "signed-headers": signed_headers}


class Stats:
    def __init__(self) -> None:
        self.latencies: list[float] = []
        self.errors: dict[str, int] = {}
        self.bytes = 0
        self.ok = 0
        self.lock = threading.Lock()

    def record(self, latency_ms: float, status: int, size: int) -> None:
        with self.lock:
            if 200 <= status < 300:
                self.latencies.append(latency_ms)
                self.bytes += size
                self.ok += 1
            else:
                self.errors[str(status)] = self.errors.get(str(status), 0) + 1


class Worker(threading.Thread):
    def __init__(self, signer: Signer, host: str, path: str, method: str, body: bytes,
                 stats: Stats, stop_at: float, delay: float) -> None:
        super().__init__(daemon=True)
        self.signer = signer
        self.host = host
        self.path = path
        self.method = method
        self.body = body
        self.stats = stats
        self.stop_at = stop_at
        self.delay = delay
        self._conn = None
        self._payload_hash = hashlib.sha256(body).hexdigest()

    def _connect(self):
        if self._conn is not None:
            try:
                self._conn.close()
            except Exception:
                pass
        self._conn = http.client.HTTPConnection(self.host, timeout=60)

    def run(self) -> None:
        self._connect()
        while time.perf_counter() < self.stop_at:
            sig = self.signer.sign(self.method, self.path, {}, self._payload_hash)
            headers = {
                "Host": self.host,
                "x-amz-date": sig["x-amz-date"],
                "x-amz-content-sha256": sig["x-amz-content-sha256"],
                "Authorization": sig["authorization"],
                "Content-Length": str(len(self.body)),
            }
            started = time.perf_counter()
            try:
                self._conn.request(self.method, self.path, body=self.body, headers=headers)
                response = self._conn.getresponse()
                data = response.read()
                elapsed = (time.perf_counter() - started) * 1000
                self.stats.record(elapsed, response.status, len(data) if self.method == "GET" else len(self.body))
                if response.will_close:
                    self._connect()
            except Exception as exc:  # 连接被对端关闭或超时后重连，计入错误
                elapsed = (time.perf_counter() - started) * 1000
                with self.stats.lock:
                    name = type(exc).__name__
                    self.stats.errors[name] = self.stats.errors.get(name, 0) + 1
                self._connect()
            if self.delay:
                time.sleep(self.delay)
        try:
            if self._conn:
                self._conn.close()
        except Exception:
            pass


def percentile(values: list[float], pct: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, int(round(pct / 100 * (len(ordered) - 1)))))
    return ordered[index]


def run_round(args, signer, method: str, path: str, body: bytes, concurrency: int, duration: int) -> dict:
    stats = Stats()
    start = time.perf_counter()
    stop_at = start + duration
    workers = [Worker(signer, args.host, path, method, body, stats, stop_at, args.delay)
               for _ in range(concurrency)]
    for worker in workers:
        worker.start()
    for worker in workers:
        worker.join(timeout=duration + 90)
    elapsed = time.perf_counter() - start
    lat = stats.latencies
    total = stats.ok + sum(stats.errors.values())
    return {
        "method": method,
        "size": len(body),
        "concurrency": concurrency,
        "ok": stats.ok,
        "errors": dict(stats.errors),
        "total": total,
        "rps": stats.ok / elapsed if elapsed else 0,
        "mbps": stats.bytes / elapsed / (1024 * 1024) if elapsed else 0,
        "p50": percentile(lat, 50),
        "p90": percentile(lat, 90),
        "p95": percentile(lat, 95),
        "p99": percentile(lat, 99),
        "max": max(lat) if lat else 0.0,
        "mean": statistics.fmean(lat) if lat else 0.0,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="S3 兼容服务端并发性能基准")
    parser.add_argument("--host", default="127.0.0.1:9000")
    parser.add_argument("--access-key", required=True)
    parser.add_argument("--secret", required=True)
    parser.add_argument("--bucket", default="bench")
    parser.add_argument("--sizes", default="4096,262144,4194304",
                        help="对象大小（字节），逗号分隔")
    parser.add_argument("--concurrency", default="1,8,32", help="并发数，逗号分隔")
    parser.add_argument("--duration", type=int, default=8, help="每轮持续秒数")
    parser.add_argument("--delay", type=float, default=0.0, help="每次请求后休眠（限速用）")
    parser.add_argument("--ops", default="put,get", help="put / get / put,get")
    args = parser.parse_args()

    import importlib.util
    import os
    smoke_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "smoke.py")
    spec = importlib.util.spec_from_file_location("smoke", smoke_path)
    smoke = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(smoke)

    signer = Signer(args.host, args.access_key, args.secret)

    status, _, _ = smoke.request("PUT", args.host, f"/{args.bucket}", {}, {}, b"",
                                 args.access_key, args.secret)
    if status not in (200, 409):
        print(f"建桶失败: {status}", file=sys.stderr)
        return 2

    sizes = [int(s) for s in args.sizes.split(",") if s]
    levels = [int(c) for c in args.concurrency.split(",") if c]
    ops = [o.strip().lower() for o in args.ops.split(",")]

    results = []
    header = (f"{'op':<4} {'size':>9} {'conc':>4} {'ok':>7} {'err':>4} "
              f"{'rps':>9} {'MB/s':>8} {'p50':>8} {'p90':>8} {'p95':>8} {'p99':>9} {'max':>9}")
    print(header)
    print("-" * len(header))

    for size in sizes:
        body = (b"benchmark-payload-" * (size // 18 + 1))[:size]
        object_path = f"/{args.bucket}/bench-{size}.bin"
        if "put" in ops:
            smoke.request("PUT", args.host, object_path, {}, {}, body, args.access_key, args.secret)
        for concurrency in levels:
            if "put" in ops:
                result = run_round(args, signer, "PUT", object_path, body, concurrency, args.duration)
                results.append(result)
                print(f"{'PUT':<4} {size:>9} {concurrency:>4} {result['ok']:>7} "
                      f"{sum(result['errors'].values()):>4} {result['rps']:>9.1f} {result['mbps']:>8.2f} "
                      f"{result['p50']:>8.2f} {result['p90']:>8.2f} {result['p95']:>8.2f} "
                      f"{result['p99']:>9.2f} {result['max']:>9.2f}")
            if "get" in ops:
                result = run_round(args, signer, "GET", object_path, b"", concurrency, args.duration)
                results.append(result)
                print(f"{'GET':<4} {size:>9} {concurrency:>4} {result['ok']:>7} "
                      f"{sum(result['errors'].values()):>4} {result['rps']:>9.1f} {result['mbps']:>8.2f} "
                      f"{result['p50']:>8.2f} {result['p90']:>8.2f} {result['p95']:>8.2f} "
                      f"{result['p99']:>9.2f} {result['max']:>9.2f}")

    failures = [r for r in results if r["errors"]]
    if failures:
        print("\n出现错误：")
        for item in failures:
            print(f"  {item['method']} size={item['size']} conc={item['concurrency']} -> {item['errors']}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
