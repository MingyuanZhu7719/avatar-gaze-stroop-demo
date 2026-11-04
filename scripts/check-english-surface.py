#!/usr/bin/env python3
"""Check project-owned app source and the active PRD for Chinese localization.

Historical design documents and UTF-8 compatibility fixtures are intentionally
outside the current presentation surface. This does not rewrite stored data or
inspect runtime-generated user text, paths, or third-party engine resources.
"""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
CJK = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U0003134f]")
ESCAPE = re.compile(r"\\u([0-9a-fA-F]{4})|\\U([0-9a-fA-F]{8})")


def main():
    files = sorted((ROOT / "Apps").rglob("*.cs")) + sorted((ROOT / "Assets/Stroop").rglob("*.cs"))
    files.append(ROOT / "docs/prd.md")
    failures = []
    for path in files:
        for line_number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
            decoded = ESCAPE.sub(lambda match: chr(int(match.group(1) or match.group(2), 16)), line)
            if CJK.search(decoded):
                failures.append(f"{path.relative_to(ROOT)}:{line_number}")
    if failures:
        print("FAIL Chinese localization in current app/PRD surface: " + ", ".join(failures))
        return 1
    print(f"PASS English-only app/PRD source surface: {len(files)} files; no literal or Unicode-escaped Chinese.")
    print("Boundary: historical design wording and synthetic UTF-8 round-trip tests are retained; stored records are not translated.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
