#!/usr/bin/env python3
"""把 SQLite 文件的 schema 版本改成指定值，用于验证版本守卫的降级保护。"""

import sqlite3
import sys

path, version = sys.argv[1], int(sys.argv[2])
connection = sqlite3.connect(path)
connection.execute(f"PRAGMA user_version={version}")
connection.commit()
current = connection.execute("PRAGMA user_version").fetchone()[0]
connection.close()
print(f"{path}: user_version={current}")
