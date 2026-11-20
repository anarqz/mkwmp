"""fonts.py <src dir with NotoSans??-VF.ttf> <out dir> <i18n dir>
Makes the launcher's CJK fallback fonts: static Regular and Bold instances of Noto Sans JP / KR /
SC / TC (OFL), subset to the everyday characters of each language plus every character the
launcher's catalogs use. Run with python -I (the sources are downloads)."""
import site, sys, os, io, json
sys.path.append(site.getusersitepackages())
from fontTools.ttLib import TTFont
from fontTools import subset
from fontTools.varLib import instancer

src, out, i18n = sys.argv[1], sys.argv[2], sys.argv[3]

def decode_range(codec, leads, trails):
    chars = set()
    for lead in leads:
        for trail in trails:
            try:
                chars.add(bytes([lead, trail]).decode(codec))
            except UnicodeDecodeError:
                pass
    return {ord(c) for c in chars if len(c) == 1}

def span(a, b):
    return set(range(a, b + 1))

COMMON = span(0x3000, 0x303F) | span(0xFF01, 0xFF60) | span(0xFFE0, 0xFFE6) | {0x00B7, 0x2014, 0x2015, 0x2018, 0x2019, 0x201C, 0x201D, 0x2026, 0x2022, 0x203B, 0x2192, 0x00D7}
A1FE = range(0xA1, 0xFF)
SETS = {
    # JIS X 0208 level 1 kanji (rows 16-47) and the kana
    "JP": lambda: span(0x3040, 0x30FF) | decode_range("euc_jp", range(0xB0, 0xD0), A1FE),
    # KS X 1001: the 2350 everyday syllables, and the compatibility jamo
    "KR": lambda: span(0x3131, 0x318E) | decode_range("euc_kr", range(0xB0, 0xC9), A1FE),
    # GB 2312 level 1 (3755 characters)
    "SC": lambda: decode_range("gb2312", range(0xB0, 0xD8), A1FE),
    # Big5 level 1 (5401 frequently used characters)
    "TC": lambda: decode_range("big5", range(0xA4, 0xC7), list(range(0x40, 0x7F)) + list(A1FE)),
}
CATALOG = {"JP": "ja", "KR": "ko", "SC": "zh-Hans", "TC": "zh-Hant"}

def catalog_chars():
    """Every non-Latin character of every catalog (the selector lists each language in its own name)."""
    chars = {}
    for name in os.listdir(i18n):
        if name.endswith(".json"):
            text = "".join(json.load(io.open(os.path.join(i18n, name), encoding="utf-8")).values())
            chars[name[:-5]] = {ord(c) for c in text if ord(c) >= 0x2E80}
    return chars

def rename(font, family, style, weight):
    name = font["name"]
    for record in list(name.names):
        if record.nameID in (16, 17, 21, 22, 25) or record.nameID > 255:
            name.removeNames(nameID=record.nameID)
    full = f"{family} {style}" if style != "Regular" else family
    for platform, enc, lang in ((3, 1, 0x409), (1, 0, 0)):
        name.setName(family, 1, platform, enc, lang)
        name.setName(style, 2, platform, enc, lang)
        name.setName(f"{family} {style}; MKWMP subset", 3, platform, enc, lang)
        name.setName(full, 4, platform, enc, lang)
        name.setName(family.replace(" ", "") + "-" + style, 6, platform, enc, lang)
    os2 = font["OS/2"]
    os2.usWeightClass = weight
    bold = weight >= 700
    os2.fsSelection = (os2.fsSelection & ~0x61) | (0x20 if bold else 0x40)
    font["head"].macStyle = (font["head"].macStyle & ~1) | (1 if bold else 0)

os.makedirs(out, exist_ok=True)
used = catalog_chars()
# every font also has the four languages' own names, which the selector lists in any language
everything = {ord(c) for c in "日本語한국어简体中文繁體中文"}
total = 0
for tag, make in SETS.items():
    family = f"Noto Sans {tag}"
    # the language's own everyday set, what its catalog uses, and what any catalog uses that this font has
    wanted = COMMON | make() | used.get(CATALOG[tag], set()) | everything
    vf = TTFont(os.path.join(src, f"NotoSans{tag}-VF.ttf"))
    options = subset.Options()
    options.layout_features = ["ccmp", "locl", "kern", "palt"] if False else []
    options.hinting = False
    options.notdef_outline = True
    options.name_IDs = [0, 1, 2, 3, 4, 5, 6, 13, 14]
    options.drop_tables += ["DSIG", "vhea", "vmtx", "VORG", "BASE", "GDEF", "GPOS", "GSUB"]
    sub = subset.Subsetter(options)
    sub.populate(unicodes=wanted)
    sub.subset(vf)
    small = os.path.join(out, f"NotoSans{tag}-vf.tmp")
    vf.save(small)
    for style, weight in (("Regular", 400), ("Bold", 700)):
        font = instancer.instantiateVariableFont(TTFont(small), {"wght": weight}, inplace=False)
        rename(font, family, style, weight)
        path = os.path.join(out, f"NotoSans{tag}-{style}.ttf")
        font.save(path)
        size = os.path.getsize(path)
        total += size
        print(f"{os.path.basename(path)}: {len(font.getBestCmap())} characters, {size:,} bytes")
    os.remove(small)
print(f"total {total:,} bytes")
