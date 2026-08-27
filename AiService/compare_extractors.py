# -*- coding: utf-8 -*-
"""
A/B harness: extract_claude_up.py (old) vs extract_universal.py (new).

Runs both over the same inputs and reports the numbers that decide whether the
new pipeline is actually better for retrieval:

  * ligature damage   -- corrupted Arabic sequences vs healthy ones
  * word-splitting    -- the old cleaner manufactured 'المعل ومات' from
                         'المعلومات'; this counts how often that happened
  * coverage          -- characters recovered, and pages that produced nothing
  * tables            -- how many, and how many reached the chunks at all
  * time              -- wall clock per file

Usage:
    python compare_extractors.py Uploads/ compare_out/
    python compare_extractors.py Uploads/ compare_out/ --no-ocr   (fast)
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

try:
    import regex as re
except ImportError:
    import re  # type: ignore

HERE = Path(__file__).resolve().parent
OLD = HERE / "extract_claude_up.py"
NEW = HERE / "extract_universal.py"

ARABIC = r"؀-ۿ"
CORRUPT = re.compile(r"امل|اخل|اجل|احل|واال|واإل|األ|اإل|االس|االق|\bيف\b")
HEALTHY = re.compile(r"الم|الخ|الج|الح|والا|والإ|الأ|الإ|\bفي\b")

# words the old pipeline split apart mid-word; each is a token that should
# never appear as a standalone word in correct Arabic
SPLIT_ARTEFACTS = [
    "المعل ومات", "معل ومات", "التح ويلات", "المقب ولين", "المسؤ ولية",
    "الأ ولوية", "الاستقل الية", "مست فيدين", "الم الية", "الأ من",
]
SPLIT_RE = re.compile("|".join(re.escape(w) for w in SPLIT_ARTEFACTS))


def run(script: Path, src: Path, out: Path, extra: list) -> float:
    out.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    try:
        subprocess.run(
            [sys.executable, str(script), str(src), str(out)] + extra,
            capture_output=True, timeout=3600,
        )
    except subprocess.TimeoutExpired:
        return -1.0
    return time.time() - t0


def read_text(out: Path, stem: str) -> str:
    for cand in list(out.rglob(stem + "_clean.txt")) or list(out.rglob("*_clean.txt")):
        try:
            return cand.read_text(encoding="utf-8", errors="replace")
        except Exception:
            pass
    return ""


def read_json(out: Path, suffix: str):
    for cand in out.rglob("*" + suffix):
        try:
            return json.loads(cand.read_text(encoding="utf-8"))
        except Exception:
            pass
    return None


def measure(out: Path, stem: str) -> dict:
    text = read_text(out, stem)
    chunks = read_json(out, "_chunks.json") or []
    tables = read_json(out, "_tables.json") or []
    bad, good = len(CORRUPT.findall(text)), len(HEALTHY.findall(text))
    table_chunks = sum(
        1 for c in chunks
        if str((c.get("metadata") or {}).get("type", "")).startswith("table")
    )
    return {
        "chars": len(text),
        "arabic": len(re.findall(rf"[{ARABIC}]", text)),
        "corrupt": bad,
        "healthy": good,
        "damage_pct": round(100.0 * bad / max(bad + good, 1), 1),
        "split_words": len(SPLIT_RE.findall(text)),
        "chunks": len(chunks),
        "table_chunks": table_chunks,
        "tables": len(tables),
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("output")
    ap.add_argument("--no-ocr", action="store_true",
                    help="pass --no-ocr to the new extractor (much faster)")
    args = ap.parse_args()

    src = Path(args.input)
    root = Path(args.output)
    root.mkdir(parents=True, exist_ok=True)

    files = [src] if src.is_file() else sorted(
        p for p in src.rglob("*")
        if p.is_file() and p.suffix.lower() in
        {".pdf", ".docx", ".xlsx", ".xls", ".pptx", ".txt", ".csv", ".html",
         ".png", ".jpg", ".jpeg"}
    )
    if not files:
        print("no input files found")
        return 1

    rows = []
    for f in files:
        print("=" * 78)
        print(f.name)
        old_dir = root / "old" / f.stem
        new_dir = root / "new" / f.stem

        # the old script only ever handled PDFs
        if f.suffix.lower() == ".pdf":
            t_old = run(OLD, f, old_dir, [])
            m_old = measure(old_dir, f.stem)
        else:
            t_old, m_old = 0.0, None

        extra = ["--no-ocr"] if args.no_ocr else []
        t_new = run(NEW, f, new_dir, extra)
        m_new = measure(new_dir, f.stem)

        rows.append((f.name, t_old, m_old, t_new, m_new))

        def line(tag, t, m):
            if m is None:
                print(f"  {tag:4} UNSUPPORTED (old pipeline was PDF-only)")
                return
            print(f"  {tag:4} {t:6.1f}s chars={m['chars']:7} arabic={m['arabic']:6} "
                  f"damage={m['damage_pct']:5.1f}% split={m['split_words']:4} "
                  f"chunks={m['chunks']:4} tables={m['tables']:3} "
                  f"table_chunks={m['table_chunks']:4}")

        line("OLD", t_old, m_old)
        line("NEW", t_new, m_new)

    print("=" * 78)
    print("SUMMARY")
    print(f"{'file':38} {'damage old->new':>18} {'split old->new':>16} {'table chunks':>14}")
    for name, _, mo, _, mn in rows:
        if mo is None:
            print(f"{name[:38]:38} {'(new only)':>18} {'-':>16} {mn['table_chunks']:>14}")
            continue
        print(f"{name[:38]:38} "
              f"{str(mo['damage_pct']) + '% -> ' + str(mn['damage_pct']) + '%':>18} "
              f"{str(mo['split_words']) + ' -> ' + str(mn['split_words']):>16} "
              f"{str(mo['table_chunks']) + ' -> ' + str(mn['table_chunks']):>14}")
    print("\nNote: 'table chunks' is how many chunks carry table content. The old"
          "\npipeline wrote tables to separate files that the embedder never read,"
          "\nso table data was unretrievable regardless of extraction quality.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
