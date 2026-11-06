#!/usr/bin/env python3
"""导出实例的规范化快照（桶 / 对象 / 访问密钥），用于备份、恢复与升级的一致性比对。

用法：python3 snapshot.py <port> <admin-token> <output.json>
只依赖标准库，输出按键排序，便于直接 diff。
"""

import json
import sys
import urllib.parse
import urllib.request

port = sys.argv[1] if len(sys.argv) > 1 else "9500"
token = sys.argv[2] if len(sys.argv) > 2 else ""
out = sys.argv[3] if len(sys.argv) > 3 else "snapshot.json"
base = f"http://127.0.0.1:{port}"


def get(path):
    req = urllib.request.Request(base + path, headers={"Authorization": f"Bearer {token}"})
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def main():
    overview = get("/admin/api/overview")
    snapshot = {"buckets": [], "keys": []}

    for bucket in sorted(overview.get("bucketSummaries", []), key=lambda b: b["name"]):
        entry = {"name": bucket["name"], "objects": [], "uploads": []}
        continuation = ""
        while True:
            query = f"?maxKeys=1000&continuation={urllib.parse.quote(continuation)}" if continuation else "?maxKeys=1000"
            page = get(f"/admin/api/buckets/{urllib.parse.quote(bucket['name'])}/objects{query}")
            for item in page.get("objects", []):
                entry["objects"].append({
                    "key": item["key"],
                    "size": item["size"],
                    "etag": item["etag"],
                    "contentType": item.get("contentType"),
                })
            if not page.get("isTruncated"):
                break
            continuation = page.get("nextContinuationToken") or ""

        entry["objects"].sort(key=lambda o: o["key"])

        uploads = get(f"/admin/api/buckets/{urllib.parse.quote(bucket['name'])}/uploads")
        for upload in uploads.get("uploads", []):
            entry["uploads"].append({
                "key": upload["key"],
                "uploadedBytes": upload["uploadedBytes"],
                "uploadedParts": upload["uploadedParts"],
            })
        entry["uploads"].sort(key=lambda u: u["key"] + str(u["uploadedBytes"]))
        snapshot["buckets"].append(entry)

    keys = get("/admin/api/access-keys").get("accessKeys", [])
    for key in sorted(keys, key=lambda k: k["accessKeyId"]):
        snapshot["keys"].append({
            "accessKeyId": key["accessKeyId"],
            "isAdmin": key["isAdmin"],
            "enabled": key["enabled"],
        })

    snapshot["totals"] = {
        "bucketCount": len(snapshot["buckets"]),
        "objectCount": sum(len(b["objects"]) for b in snapshot["buckets"]),
        "bytes": sum(o["size"] for b in snapshot["buckets"] for o in b["objects"]),
    }

    with open(out, "w", encoding="utf-8") as handle:
        json.dump(snapshot, handle, indent=2, sort_keys=True)

    print(f"{out}: buckets={snapshot['totals']['bucketCount']} "
          f"objects={snapshot['totals']['objectCount']} bytes={snapshot['totals']['bytes']}")


if __name__ == "__main__":
    main()
