#!/usr/bin/env python3
"""Build the website's pages from one template per page and one strings file per language.

    python3 tools/site/build.py          # writes site/index.html, site/host.html, site/pt-br/...

Templates (tools/site/templates/*.html) use:
    {{key}}          a string from tools/site/i18n/<lang>.json (may hold simple HTML we wrote)
    {{i:name}}       the launcher icon site/assets/launcher/icons/<name>.svg, inlined (CSS colours it)
    {{=root}}        the path back to site/ ("" or "../")
    {{=lang}}        the page's language (en, pt-BR)
    {{=alt_href}}    the same page in the other language
    {{=alt_label}}   that language's short name
    {{=i18n_js}}     the strings site.js needs, as JSON

The output is static HTML: GitHub Pages serves site/ as it is (no build step there). Run this
after editing a template or a strings file, and commit both.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
HERE = pathlib.Path(__file__).resolve().parent
SITE = ROOT / "site"
ICONS = SITE / "assets" / "launcher" / "icons"
LANGS = {"en": ("", "EN"), "pt-BR": ("pt-br/", "PT")}
PAGES = ["index.html", "host.html"]
JS_KEYS = ["themeLight", "themeDark", "copied", "copyFailed", "loading", "serversError", "serversNone",
           "serversCount", "serversCount1", "playing", "inQueue", "policyVersion", "policyError"]


def icon(name: str) -> str:
    f = ICONS / f"{name}.svg"
    if not f.exists():
        sys.exit(f"unknown icon {name!r} ({f})")
    d = re.search(r'\sd="([^"]+)"', f.read_text()).group(1)
    filled = name.endswith("-fill")
    cls = "i fill" if filled else "i"
    rule = ' fill-rule="evenodd"' if filled else ""
    return f'<svg class="{cls}" viewBox="0 0 24 24" aria-hidden="true"><path d="{d}"{rule}/></svg>'


def build(page: str, lang: str, strings: dict, other: str) -> str:
    prefix, _ = LANGS[lang]
    alt_prefix, alt_label = LANGS[other]
    root = "../" * prefix.count("/")
    alt_href = root + alt_prefix + ("" if page == "index.html" else page)
    values = {
        "root": root,
        "lang": lang,
        "alt_href": alt_href or "./",
        "alt_label": alt_label,
        "i18n_js": json.dumps({k: strings["js"][k] for k in JS_KEYS}, ensure_ascii=False),
    }
    text = (HERE / "templates" / page).read_text()
    missing = set()

    def sub(m: re.Match) -> str:
        key = m.group(1).strip()
        if key.startswith("i:"):
            return icon(key[2:])
        if key.startswith("="):
            return values[key[1:]]
        node = strings
        for part in key.split("."):
            if not isinstance(node, dict) or part not in node:
                missing.add(key)
                return f"[{key}]"
            node = node[part]
        return node

    out = re.sub(r"\{\{([^}]+)\}\}", sub, text)
    if missing:
        sys.exit(f"{lang}/{page}: missing strings: {', '.join(sorted(missing))}")
    return out


def main() -> None:
    langs = list(LANGS)
    strings = {l: json.loads((HERE / "i18n" / f"{l}.json").read_text()) for l in langs}
    for lang in langs:
        other = langs[1 - langs.index(lang)]
        for page in PAGES:
            dest = SITE / LANGS[lang][0] / page
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_text(build(page, lang, strings[lang], other))
            print(dest.relative_to(ROOT))


if __name__ == "__main__":
    main()
