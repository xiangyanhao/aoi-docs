#!/usr/bin/env python3
"""Copy chapter docs (1-/2-/3-/4-) into playground wwwroot/doc-content and write index.json.

Uses doc-content/ (not docs/) so the static files do not collide with the Blazor
/docs route on GitHub Pages — otherwise opening /docs in a new tab serves index.json.
"""

from __future__ import annotations

import json
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WWWROOT = Path(__file__).resolve().parents[1] / "Aoi.Playground" / "wwwroot"
OUT = WWWROOT / "doc-content"
LEGACY = WWWROOT / "docs"


def first_heading(text: str) -> str | None:
    for line in text.splitlines():
        m = re.match(r"^#\s+(.+?)\s*$", line)
        if m:
            return m.group(1).strip()
    return None


def is_overview(stem: str) -> bool:
    """Chapter overview like 1-type-basics / 2-basic-grammar (no 1.2-style section)."""
    return bool(re.match(r"^\d+-[a-zA-Z].*$", stem)) and "." not in stem


def doc_sort_key(stem: str) -> tuple:
    # 1-type-basics first, then 1.1..., 1.2...
    m = re.match(r"^(\d+)(?:\.(\d+))?-?(.*)$", stem)
    if not m:
        return (999, 999, stem)
    major = int(m.group(1))
    minor = int(m.group(2)) if m.group(2) else -1
    return (major, minor, m.group(3) or "")


def main() -> int:
    # Drop the old path that shadowed the /docs SPA route.
    if LEGACY.exists():
        shutil.rmtree(LEGACY)
    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir(parents=True)

    chapters: list[dict] = []
    all_docs: list[dict] = []

    for folder in sorted(ROOT.iterdir()):
        if not folder.is_dir() or not re.match(r"^[1-4]-", folder.name):
            continue

        chapter_id = folder.name
        md_files = sorted(folder.glob("*.md"), key=lambda p: doc_sort_key(p.stem))
        if not md_files:
            continue

        chapter_title = chapter_id
        for md in md_files:
            if is_overview(md.stem):
                chapter_title = first_heading(md.read_text(encoding="utf-8")) or chapter_title
                break

        chapter_docs: list[dict] = []
        for md in md_files:
            rel = f"{chapter_id}/{md.name}"
            dest = OUT / rel
            dest.parent.mkdir(parents=True, exist_ok=True)
            text = md.read_text(encoding="utf-8")
            dest.write_text(text, encoding="utf-8")
            title = first_heading(text) or md.stem
            doc = {
                "id": rel[:-3],
                "path": rel,
                "title": title,
                "chapter": chapter_id,
            }
            chapter_docs.append(doc)
            all_docs.append(doc)

        chapters.append(
            {
                "id": chapter_id,
                "title": chapter_title,
                "docs": chapter_docs,
            }
        )

    index = {"chapters": chapters, "docs": all_docs}
    (OUT / "index.json").write_text(
        json.dumps(index, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"Synced {len(all_docs)} docs → {OUT.relative_to(ROOT)}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
