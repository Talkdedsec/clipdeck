"""Builds Assets/emoji.tsv from Unicode emoji-test.txt and CLDR Turkish annotations.

Usage: python tools/emoji_veri.py
Output columns: emoji, group index, English name, Turkish name, Turkish keywords (|), skin tone variants (space separated, 5 or empty)
"""
import json
import pathlib
import tempfile
import urllib.request

SOURCES = {
    "emoji-test.txt": "https://unicode.org/Public/emoji/latest/emoji-test.txt",
    "tr.json": "https://raw.githubusercontent.com/unicode-org/cldr-json/main/cldr-json/cldr-annotations-full/annotations/tr/annotations.json",
    "tr-derived.json": "https://raw.githubusercontent.com/unicode-org/cldr-json/main/cldr-json/cldr-annotations-derived-full/annotationsDerived/tr/annotations.json",
}

GROUPS = [
    "Smileys & Emotion", "People & Body", "Animals & Nature", "Food & Drink",
    "Travel & Places", "Activities", "Objects", "Symbols", "Flags",
]
TONES = ["light skin tone", "medium-light skin tone", "medium skin tone", "medium-dark skin tone", "dark skin tone"]

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "Assets" / "emoji.tsv"


def fetch(cache: pathlib.Path) -> dict[str, pathlib.Path]:
    paths = {}
    for name, url in SOURCES.items():
        path = cache / name
        if not path.exists():
            print("indiriliyor:", url)
            with urllib.request.urlopen(url, timeout=60) as r:
                path.write_bytes(r.read())
        paths[name] = path
    return paths


def load_annotations(path: pathlib.Path, root_key: str) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    return data[root_key]["annotations"]


def split_name(name: str):
    """'woman: medium skin tone, red hair' -> ('woman: red hair', tones=['medium skin tone'])"""
    if ": " not in name:
        return name, []
    base, rest = name.split(": ", 1)
    attrs = [a.strip() for a in rest.split(",")]
    tones = [a for a in attrs if a in TONES]
    others = [a for a in attrs if a not in TONES]
    key = base + (": " + ", ".join(others) if others else "")
    return key, tones


def main():
    cache = pathlib.Path(tempfile.gettempdir()) / "clipdeck-emoji-kaynak"
    cache.mkdir(exist_ok=True)
    paths = fetch(cache)
    tr = load_annotations(paths["tr.json"], "annotations")
    tr.update(load_annotations(paths["tr-derived.json"], "annotationsDerived"))

    group = None
    bases = []          # (emoji, group index, en name, key)
    by_key = {}
    variants = {}       # key -> {tone: emoji}
    for line in paths["emoji-test.txt"].read_text(encoding="utf-8").splitlines():
        if line.startswith("# group:"):
            g = line.split(":", 1)[1].strip()
            group = GROUPS.index(g) if g in GROUPS else None
            continue
        if not line or line.startswith("#") or group is None:
            continue
        code, rest = line.split(";", 1)
        status, comment = rest.split("#", 1)
        if status.strip() != "fully-qualified":
            continue
        parts = comment.strip().split(" ", 2)
        emoji, name = parts[0], parts[2]
        key, tones = split_name(name)
        if not tones:
            if key not in by_key:
                by_key[key] = emoji
                bases.append((emoji, group, name, key))
        elif len(tones) == 1:
            variants.setdefault(key, {})[tones[0]] = emoji

    def lookup(e: str):
        return tr.get(e) or tr.get(e.replace("️", ""))

    rows = 0
    with OUT.open("w", encoding="utf-8", newline="\n") as f:
        for emoji, g, en, key in bases:
            a = lookup(emoji) or {}
            tr_name = (a.get("tts") or [""])[0]
            keywords = "|".join(k for k in a.get("default", []) if k != tr_name)
            v = variants.get(key, {})
            tones = " ".join(v[t] for t in TONES) if all(t in v for t in TONES) else ""
            f.write(f"{emoji}\t{g}\t{en}\t{tr_name}\t{keywords}\t{tones}\n")
            rows += 1
    print(f"{rows} emoji yazildi -> {OUT}")


if __name__ == "__main__":
    main()
