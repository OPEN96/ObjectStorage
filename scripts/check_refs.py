#!/usr/bin/env python3
"""校验元数据库里的对象引用与磁盘 blob 是否一一对应（用于验证备份的引用完整性）。

用法：python3 check_refs.py <metadata.db> <blobs.txt>
blobs.txt 为 `find /data/blobs -name '*.bin'` 的输出（绝对路径）。
"""

import sqlite3
import sys

db_path, list_path = sys.argv[1], sys.argv[2]

def normalize(path):
    value = path.strip().lstrip("./")
    return value[len("data/"):] if value.startswith("data/") else value


with open(list_path, encoding="utf-8") as handle:
    present = {normalize(line) for line in handle if line.strip()}

connection = sqlite3.connect(db_path)
rows = [normalize(row[0]) for row in connection.execute("SELECT BlobPath FROM Objects")]
connection.close()

missing = [path for path in rows if path not in present]
orphan = len(present) - (len(rows) - len(missing))

print(f"db_rows={len(rows)} files={len(present)} missing={len(missing)} unreferenced={orphan}")
for path in missing[:5]:
    print("  missing:", path)
