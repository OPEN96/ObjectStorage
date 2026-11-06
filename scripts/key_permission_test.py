#!/usr/bin/env python3
"""密钥生命周期与权限模型测试：轮换、禁用、删除、租户隔离、会话撤销。

覆盖商业级场景：
  1. 管理端签发 / 禁用 / 启用 / 删除访问密钥的即时生效性
  2. 非管理员密钥的租户隔离（跨桶越权）
  3. Secret 轮换能力（含是否支持原地轮换）
  4. 最后一个管理员密钥的自我保护
  5. 管理员会话 Cookie 在密钥被删除后的有效性（撤销语义）
"""

import http.client
import json
import os
import sys
import uuid

HOST = os.environ.get("S3_HOST", "127.0.0.1:9000")
ADMIN_TOKEN = os.environ.get("ADMIN_TOKEN", "change-me-admin-token")
ADMIN_AK = os.environ.get("S3_ACCESS_KEY", "AKIAIOSFODNN7EXAMPLE")
ADMIN_SK = os.environ.get("S3_SECRET_KEY", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")
REGION = "us-east-1"

# 每次运行用独立桶名，保证脚本可重复执行（同名桶已被上一轮的密钥占用时无法重建）。
RUN = uuid.uuid4().hex[:6]
B_TENANT = f"kp-{RUN}-tenant"
B_ADMIN = f"kp-{RUN}-admin"

sys.path.insert(0, "scripts")
import smoke  # noqa: E402

results = []


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name} {detail}")


def call(method, target, body=b"", headers=None, cookie=None, timeout=15):
    hdrs = dict(headers or {})
    if cookie:
        hdrs["Cookie"] = cookie
    conn = http.client.HTTPConnection(HOST, timeout=timeout)
    try:
        conn.request(method, target, body=body, headers=hdrs)
        r = conn.getresponse()
        raw = r.read()
        set_cookie = r.getheader("Set-Cookie") or ""
        return r.status, raw, set_cookie
    finally:
        conn.close()


def admin(method, target, payload=None, cookie=None):
    body = b"" if payload is None else json.dumps(payload).encode()
    headers = {"Content-Type": "application/json"} if payload is not None else {}
    if cookie is None:
        headers["Authorization"] = f"Bearer {ADMIN_TOKEN}"
    return call(method, target, body, headers, cookie=cookie)


def s3(method, path, query=None, body=b"", headers=None, ak=ADMIN_AK, sk=ADMIN_SK):
    return smoke.request(method, HOST, path, query or {}, headers or {}, body, ak, sk, REGION)


def login_token():
    st, _, cookie = call("POST", "/admin/api/auth/login",
                         json.dumps({"token": ADMIN_TOKEN}).encode(),
                         {"Content-Type": "application/json"})
    if st != 200:
        raise SystemExit(f"管理员登录失败 status={st}")
    return cookie.split(";")[0]


def create_key(name, is_admin=False, cookie=None):
    st, body, _ = admin("POST", "/admin/api/access-keys",
                        {"displayName": name, "isAdmin": is_admin}, cookie)
    if st != 201:
        raise SystemExit(f"创建密钥失败 status={st} body={body[:200]}")
    data = json.loads(body)
    return data["accessKeyId"], data["secretAccessKey"]


def main():
    cookie = login_token()
    print("管理员登录成功")

    print("=== 1. 密钥签发与基本可用性 ===")
    k1, s1 = create_key("tenant-a", cookie=cookie)
    st, _, _ = s3("PUT", f"/{B_TENANT}", ak=k1, sk=s1)
    check("新建密钥可创建桶", st == 200, f"status={st}")
    st, _, _ = s3("PUT", f"/{B_TENANT}/a.txt", body=b"tenant-a-data", ak=k1, sk=s1)
    check("新建密钥可写对象", st == 200, f"status={st}")
    st, _, body = s3("GET", f"/{B_TENANT}/a.txt", ak=k1, sk=s1)
    check("新建密钥可读对象", st == 200 and body == b"tenant-a-data", f"status={st}")

    print("=== 2. 租户隔离（非管理员密钥）===")
    st, _, _ = s3("PUT", f"/{B_ADMIN}", ak=ADMIN_AK, sk=ADMIN_SK)
    s3("PUT", f"/{B_ADMIN}/secret.txt", body=b"admin-only", ak=ADMIN_AK, sk=ADMIN_SK)
    st, _, _ = s3("GET", f"/{B_ADMIN}/secret.txt", ak=k1, sk=s1)
    check("跨租户读对象被拒", st == 403, f"status={st}")
    st, _, _ = s3("PUT", f"/{B_ADMIN}/hijack.txt", body=b"x", ak=k1, sk=s1)
    check("跨租户写对象被拒", st == 403, f"status={st}")
    st, _, _ = s3("DELETE", f"/{B_ADMIN}/secret.txt", ak=k1, sk=s1)
    check("跨租户删对象被拒", st == 403, f"status={st}")
    st, _, body = s3("GET", f"/{B_ADMIN}", ak=k1, sk=s1)
    check("跨租户列举被拒", st == 403, f"status={st}")
    st, _, body = s3("GET", "/", ak=k1, sk=s1)
    text = body.decode("utf-8", "replace")
    check("ListBuckets 只返回自有桶", st == 200 and B_TENANT in text and B_ADMIN not in text,
          f"status={st}")
    st, _, _ = s3("GET", "/admin/api/overview", ak=k1, sk=s1)
    check("非管理员密钥无法进入管理端", st in (401, 403), f"status={st}")
    st, raw, _ = call("POST", "/admin/api/auth/login",
                      json.dumps({"accessKeyId": k1, "secretAccessKey": s1}).encode(),
                      {"Content-Type": "application/json"})
    check("非管理员密钥无法登录管理端", st == 401, f"status={st}")

    print("=== 3. 禁用 / 启用即时性 ===")
    st, _, _ = admin("PATCH", f"/admin/api/access-keys/{k1}", {"enabled": False}, cookie)
    check("管理端可禁用密钥", st == 200, f"status={st}")
    st, _, _ = s3("GET", f"/{B_TENANT}/a.txt", ak=k1, sk=s1)
    check("禁用后立即失效（无缓存延迟）", st == 403, f"status={st}")
    st, _, _ = admin("PATCH", f"/admin/api/access-keys/{k1}", {"enabled": True}, cookie)
    st2, _, _ = s3("GET", f"/{B_TENANT}/a.txt", ak=k1, sk=s1)
    check("重新启用后立即恢复", st == 200 and st2 == 200, f"patch={st} get={st2}")

    print("=== 4. Secret 原地轮换 ===")
    rotated = "rotated-secret-" + "9" * 20
    st, body, _ = admin("PATCH", f"/admin/api/access-keys/{k1}",
                        {"secretAccessKey": rotated}, cookie)
    st_old, _, _ = s3("GET", f"/{B_TENANT}/a.txt", ak=k1, sk=s1)
    st_new, _, _ = s3("GET", f"/{B_TENANT}/a.txt", ak=k1, sk=rotated)
    if st_new == 200 and st_old == 403:
        check("PATCH 支持原地轮换 Secret", True, f"old={st_old} new={st_new}")
    else:
        check("PATCH 支持原地轮换 Secret", False,
              f"patch={st} old={st_old} new={st_new}（接口不接受 secretAccessKey）")

    print("=== 5. 删除后的行为 ===")
    st, _, _ = admin("DELETE", f"/admin/api/access-keys/{k1}", cookie=cookie)
    check("管理端可删除密钥", st == 200, f"status={st}")
    st, _, _ = s3("GET", f"/{B_TENANT}/a.txt", ak=k1, sk=s1)
    check("删除后密钥立即失效", st == 403, f"status={st}")
    st, _, body = s3("GET", f"/{B_TENANT}/a.txt", ak=ADMIN_AK, sk=ADMIN_SK)
    check("被删密钥遗留的桶/对象仍可由管理员访问", st == 200, f"status={st}")

    print("=== 6. 最后一个管理员保护 ===")
    st, body, _ = admin("PATCH", f"/admin/api/access-keys/{ADMIN_AK}", {"enabled": False}, cookie)
    check("拒绝禁用最后一个管理员", st == 409, f"status={st}")
    st, body, _ = admin("DELETE", f"/admin/api/access-keys/{ADMIN_AK}", cookie=cookie)
    check("拒绝删除最后一个管理员", st == 409, f"status={st}")

    print("=== 7. 会话撤销语义 ===")
    k2, s2 = create_key("temp-admin", is_admin=True, cookie=cookie)
    st, _, c2 = call("POST", "/admin/api/auth/login",
                     json.dumps({"accessKeyId": k2, "secretAccessKey": s2}).encode(),
                     {"Content-Type": "application/json"})
    check("管理员密钥可登录并拿到会话", st == 200, f"status={st}")
    session = c2.split(";")[0]
    st, _, _ = call("GET", "/admin/api/access-keys", cookie=session)
    check("会话可访问管理接口", st == 200, f"status={st}")
    admin("DELETE", f"/admin/api/access-keys/{k2}", cookie=cookie)
    st, _, _ = call("GET", "/admin/api/access-keys", cookie=session)
    check("密钥删除后旧会话仍可用（应视为缺陷）", st != 200,
          f"status={st}（200 表示会话未被撤销）")
    st, _, _ = s3("GET", "/", ak=k2, sk=s2)
    check("密钥删除后签名请求失效", st == 403, f"status={st}")

    print("=== 8. 篡改会话 Cookie ===")
    tampered = session.rsplit(".", 1)[0] + ".AAAA"
    st, _, _ = call("GET", "/admin/api/access-keys", cookie=tampered)
    check("伪造会话被拒", st == 401, f"status={st}")

    # 清理
    for bucket in (B_TENANT, B_ADMIN):
        s3("DELETE", f"/{bucket}")

    passed = sum(1 for _, ok, _ in results if ok)
    print(f"\n结果：{passed}/{len(results)} 通过")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
