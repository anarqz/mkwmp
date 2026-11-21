# Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
"""MKWMP's art, from the pictures as drawn to what ships: the launcher's assets (app icon, backgrounds, the welcome's
hero and step pictures, empty states), the Steam library art of tools/linux/art and the website kit (site/assets/art).

    python -m pip install pillow numpy scipy imagequant pyoxipng vtracer
    python launcher/tools/make_art.py --src <art folder> --launcher launcher/src/Mkwmp.App/Assets --steam tools/linux/art

PNGs are quantised (libimagequant, 256 colours, dithered) and recompressed (oxipng) unless noted; opaque pictures that
are only backgrounds are JPEG (WPF reads it; it has no WebP codec)."""
import io
import pathlib
import struct

import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage as nd

import imagequant
import oxipng
import vtracer

BLUE = (21, 83, 250)  # the flat glyph's own blue (chat-5's median)


def load(path):
    return Image.open(path).convert("RGBA")


def floor_alpha(im, floor=10):
    """The generator leaves a faint haze (alpha under ~10) over much of the canvas, which shows as a grey box on a
    light page: alpha ramps from 0 at loor to 255, so the haze goes and the soft glows keep their fall-off."""
    a = np.array(im)
    alpha = a[..., 3].astype(np.float32)
    a[..., 3] = np.clip((alpha - floor) * 255.0 / (255.0 - floor), 0, 255).round().astype(np.uint8)
    a[a[..., 3] == 0, :3] = 0
    return Image.fromarray(a)

def feather(im, frac=0.08):
    """Alpha to 0 at the picture's edges, over frac of its size: a glow the generator let run off the canvas would
    otherwise end in a straight, visible edge."""
    a = np.array(im)
    h, w = a.shape[:2]
    ry = np.clip(np.minimum(np.arange(h), np.arange(h)[::-1]) / (h * frac), 0, 1)
    rx = np.clip(np.minimum(np.arange(w), np.arange(w)[::-1]) / (w * frac), 0, 1)
    a[..., 3] = (a[..., 3] * np.outer(ry, rx)).round().astype(np.uint8)
    return Image.fromarray(a)


def illustration(path):
    return trim(floor_alpha(feather(load(path))))

def trim(im, thresh=10, pad=0.02):
    a = np.array(im)[..., 3]
    ys, xs = np.nonzero(a > thresh)
    x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
    p = int(max(x1 - x0, y1 - y0) * pad)
    return im.crop((max(0, x0 - p), max(0, y0 - p), min(im.width, x1 + p), min(im.height, y1 + p)))


def fit(im, w, h=None):
    h = h or w
    s = min(w / im.width, h / im.height, 1.0)
    if s >= 1.0:
        return im.copy()
    return im.resize((max(1, round(im.width * s)), max(1, round(im.height * s))), Image.LANCZOS)


def square(im, size):
    """Fit into a size x size transparent square, centred."""
    im = fit(im, size, size) if max(im.size) > size else im.resize(
        (round(im.width * size / max(im.size)), round(im.height * size / max(im.size))), Image.LANCZOS)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.alpha_composite(im, ((size - im.width) // 2, (size - im.height) // 2))
    return out


def png_bytes(im, quant=True, colors=256):
    if quant and im.mode == "RGBA":
        im = imagequant.quantize_pil_image(im, dithering_level=1.0, max_colors=colors)
    elif quant and im.mode == "RGB":
        im = imagequant.quantize_pil_image(im.convert("RGBA"), dithering_level=1.0, max_colors=colors)
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=True)
    return oxipng.optimize_from_memory(buf.getvalue(), level=4)


def save_png(im, path, quant=True, colors=256):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    data = png_bytes(im, quant, colors)
    path.write_bytes(data)
    return len(data)


def save_jpg(im, path, q=84):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    im.convert("RGB").save(path, "JPEG", quality=q, optimize=True, progressive=False, subsampling="4:2:0")
    return path.stat().st_size


def save_webp(im, path, q=85):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    im.save(path, "WEBP", quality=q, method=6)
    return path.stat().st_size


def smooth_mask(mask, up=4, sigma=None):
    """A hard mask to an anti-aliased alpha: supersample, blur a little, threshold, area-average back."""
    sigma = sigma if sigma is not None else up * 0.9
    big = np.kron(mask.astype(np.float32), np.ones((up, up), np.float32))
    big = nd.gaussian_filter(big, sigma) > 0.5
    h, w = mask.shape
    return (big.reshape(h, up, w, up).mean(axis=(1, 3)) * 255).round().astype(np.uint8)


def clean_glyph(path, min_area=1000):
    """chat-5: the flat mark. Only the solid parts (alpha > 128) and islands bigger than min_area;
    the speckle is the half-transparent noise around them. Returns the mask (bool) and an RGBA image."""
    a = np.array(Image.open(path).convert("RGBA"))[..., 3]
    m = a > 128
    lab, n = nd.label(m)
    sizes = nd.sum(m, lab, range(1, n + 1))
    keep = np.isin(lab, [i + 1 for i, s in enumerate(sizes) if s >= min_area])
    # pin-holes inside a part (not the designed holes: those are large)
    holes = nd.binary_fill_holes(keep) & ~keep
    hl, hn = nd.label(holes)
    hs = nd.sum(holes, hl, range(1, hn + 1))
    keep |= np.isin(hl, [i + 1 for i, s in enumerate(hs) if s < 60])
    return keep


def glyph_image(mask, color=BLUE, size=None):
    alpha = smooth_mask(mask)
    h, w = mask.shape
    rgba = np.zeros((h, w, 4), np.uint8)
    rgba[..., :3] = color
    rgba[..., 3] = alpha
    im = Image.fromarray(rgba)
    im = trim(im, 0, 0.0)
    if size:
        im = square(im, size)
    return im


def icon_tile(path):
    """chat-4 (RGB, black outside the rounded square) as RGBA with the corners transparent."""
    rgb = np.array(Image.open(path).convert("RGB"))
    m = rgb.max(axis=2) > 14
    lab, n = nd.label(m)
    sizes = nd.sum(m, lab, range(1, n + 1))
    m = lab == (int(np.argmax(sizes)) + 1)
    m = nd.binary_fill_holes(m)
    # a little inside the edge: the black fringe of the corners goes
    m = nd.binary_erosion(m, iterations=2)
    alpha = smooth_mask(m, up=2)
    rgba = np.dstack([rgb, alpha])
    im = Image.fromarray(rgba.astype(np.uint8))
    ys, xs = np.nonzero(m)
    x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
    side = max(x1 - x0, y1 - y0)
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    return im.crop((cx - side // 2, cy - side // 2, cx - side // 2 + side, cy - side // 2 + side))


def write_ico(path, images):
    """images: {size: RGBA image}. 256 as PNG, the rest as 32-bit BMP (every reader knows them)."""
    entries = []
    for size in sorted(images, reverse=True):
        im = images[size].convert("RGBA")
        if size >= 256:
            buf = io.BytesIO()
            im.save(buf, "PNG", optimize=True)
            data = oxipng.optimize_from_memory(buf.getvalue(), level=4)
        else:
            px = np.array(im)[..., [2, 1, 0, 3]][::-1]  # BGRA, bottom-up
            mask_row = ((size + 31) // 32) * 4
            data = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, px.size, 0, 0, 0, 0) \
                + px.tobytes() + bytes(mask_row * size)
        entries.append((size, data))
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        b = 0 if size >= 256 else size
        out.write(struct.pack("<BBBBHHII", b, b, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for _, data in entries:
        out.write(data)
    pathlib.Path(path).parent.mkdir(parents=True, exist_ok=True)
    pathlib.Path(path).write_bytes(out.getvalue())
    return out.tell()


# ───────────────────────── the outputs ─────────────────────────

STEPS = {  # welcome step -> the illustration
    "name": "01_name_helmet", "game": "02_game_setup", "online": "03_play_online", "server": "04_choose_server",
    "consent": "05_privacy_consent", "pin": "06_pin", "done": "07_all_set",
}
EMPTY = {"friends": "01_no_friends_yet", "servers": "02_no_servers_found", "mods": "03_no_mods",
         "offline": "04_offline", "crash": "05_crash_report"}
PADS = ["A", "B", "X", "Y", "LB", "RB", "Start", "Select", "Dpad"]


def report(path, n):
    print(f"{n / 1024:8.1f} KB  {path}")


def logo_image(src):
    """The wordmark: alpha floor, trimmed (the source is 1536x1024 with a wide transparent margin)."""
    return trim(floor_alpha(load(src / "MKWMP_Steam_Library_Art_Pack/MKWMP_Logo_1536x1024_Transparent.png"), 6), 6, 0.01)


def small_icon(tile, size, zoom=1.25):
    """16-32 px: the tile cropped closer around the kart (it stays a kart, not a blur), corners rounded again."""
    s = tile.width
    c = s / zoom
    x0, y0 = (s - c) / 2 + s * 0.02, (s - c) / 2 + s * 0.03
    crop = tile.crop((int(x0), int(y0), int(x0 + c), int(y0 + c))).resize((size, size), Image.LANCZOS)
    crop = crop.filter(ImageFilter.UnsharpMask(radius=0.6, percent=80, threshold=0))
    up = 4
    m = Image.new("L", (size * up, size * up), 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size * up - 1, size * up - 1), radius=int(size * up * 0.2), fill=255)
    arr = np.array(crop)
    arr[..., 3] = np.array(m.resize((size, size), Image.LANCZOS))
    return Image.fromarray(arr)


def icon_at(tile, size):
    if size <= 32:
        return small_icon(tile, size)
    im = tile.resize((size, size), Image.LANCZOS)
    return im.filter(ImageFilter.UnsharpMask(radius=0.6, percent=50, threshold=0)) if size <= 64 else im


# the welcome card's sky (Views/Setup/SetupPage.xaml, its gradient stops): the hero's left edge melts into exactly these
HERO_SKY = [(0.0, (0xE2, 0xE4, 0xFB)), (0.6, (0xD3, 0xDE, 0xFC)), (1.0, (0xCB, 0xC9, 0xF8))]


def welcome_hero(src):
    """chat-3 with its left third faded into the card's sky: the card shows it whole on its right, the words on the sky."""
    im = np.array(Image.open(src / "chat-3.png").convert("RGB")).astype(np.float32)
    h, w = im.shape[:2]
    ys = np.linspace(0, 1, h)
    sky = np.stack([np.interp(ys, [p for p, _ in HERO_SKY], [c[i] for _, c in HERO_SKY]) for i in range(3)], axis=1)
    t = np.clip(np.arange(w) / (w * 0.34), 0, 1)
    wgt = 1 - t * t * (3 - 2 * t)  # smoothstep: 1 at the left edge, 0 from a third in
    out = im * (1 - wgt[None, :, None]) + sky[:, None, :] * wgt[None, :, None]
    return Image.fromarray(out.round().clip(0, 255).astype(np.uint8))

def launcher(src, assets):
    tile = icon_tile(src / "chat-4.png")
    art = assets / "Art"
    # the app icon: exe, window, tray (Assets/mkwmp.ico) and the 256 px picture beside the exe / in the update helper
    n = write_ico(assets / "mkwmp.ico", {s: icon_at(tile, s) for s in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)})
    report(assets / "mkwmp.ico", n)
    report(assets / "mkwmp-256.png", save_png(tile.resize((256, 256), Image.LANCZOS), assets / "mkwmp-256.png", quant=False))
    # the welcome's hero (the page backgrounds were dropped: pages are the plain theme colour)
    report(art / "welcome-hero.jpg", save_jpg(welcome_hero(src), art / "welcome-hero.jpg", 86))
    report(art / "logo.png", save_png(fit(logo_image(src), 960), art / "logo.png"))
    # illustrations: shown at most ~250 stage px (1080 high); stored at 1.5x
    for key, name in STEPS.items():
        im = fit(illustration(src / "welcome_step_illustrations_1024" / f"{name}.png"), 380)
        report(art / f"step-{key}.png", save_png(im, art / f"step-{key}.png"))
    for key, name in EMPTY.items():
        im = fit(illustration(src / "empty_states_1024" / f"{name}.png"), 380)
        report(art / f"empty-{key}.png", save_png(im, art / f"empty-{key}.png"))


def steam(src, out):
    pack = src / "MKWMP_Steam_Library_Art_Pack"
    tile = icon_tile(src / "chat-4.png")
    report(out / "grid.png", save_png(Image.open(pack / "MKWMP_Steam_Grid_600x900.png").convert("RGB"), out / "grid.png", quant=False))
    report(out / "wide.png", save_png(Image.open(pack / "MKWMP_Steam_Wide_920x430.png").convert("RGB"), out / "wide.png", quant=False))
    # the hero is an upscale (3840x1240 from a 1536 panel): 256 colours with dithering keep it under 3 MB
    report(out / "hero.png", save_png(Image.open(pack / "MKWMP_Steam_Hero_3840x1240.png").convert("RGB"), out / "hero.png", quant=True))
    report(out / "logo.png", save_png(fit(logo_image(src), 1280), out / "logo.png"))
    report(out / "icon.png", save_png(tile.resize((256, 256), Image.LANCZOS), out / "icon.png", quant=False))


def pad_light(im):
    """A controller glyph for dark backgrounds: the shape white, the letters dark."""
    a = np.array(im)
    a[..., :3] = 255 - a[..., :3]
    lum = a[..., :3].astype(int).mean(axis=2)
    a[..., :3] = np.where(lum[..., None] > 128, [255, 255, 255], [27, 27, 31]).astype(np.uint8)
    return Image.fromarray(a)


def pad_dark(im):
    a = np.array(im)
    lum = a[..., :3].astype(int).mean(axis=2)
    a[..., :3] = np.where(lum[..., None] > 128, [255, 255, 255], [27, 27, 31]).astype(np.uint8)
    a[..., 3] = np.where(a[..., 3] >= 250, 255, a[..., 3])
    return Image.fromarray(a)


def site(src, out):
    web = src / "MKWMP_Website_Art_Pack"
    tile = icon_tile(src / "chat-4.png")
    og = Image.open(web / "MKWMP_OG_Social_1200x630.png").convert("RGB")
    report(out / "og.png", save_png(og, out / "og.png", quant=False))
    report(out / "og.webp", save_webp(og, out / "og.webp"))
    logo = logo_image(src)
    report(out / "logo.png", save_png(fit(logo, 1280), out / "logo.png"))
    report(out / "logo.webp", save_webp(fit(logo, 1280), out / "logo.webp"))
    report(out / "hero.webp", save_webp(Image.open(src / "chat-3.png").convert("RGB"), out / "hero.webp"))
    hero = Image.open(src / "MKWMP_Steam_Library_Art_Pack/MKWMP_Steam_Hero_3840x1240.png").convert("RGB")
    report(out / "hero-wide.webp", save_webp(hero.resize((2560, round(2560 * hero.height / hero.width)), Image.LANCZOS), out / "hero-wide.webp"))
    report(out / "bg-dark.webp", save_webp(Image.open(src / "chat-1.png").convert("RGB"), out / "bg-dark.webp"))
    report(out / "bg-light.webp", save_webp(Image.open(src / "chat-2.png").convert("RGB"), out / "bg-light.webp"))
    for key, name in [("handheld", "Handheld_Console"), ("server", "Host_Your_Own_Server"), ("mods", "Mods")]:
        for ratio, dims in [("2x1", "1536x768"), ("3x1", "1536x512")]:
            p = out / f"banner-{key}-{ratio}.webp"
            report(p, save_webp(Image.open(web / f"MKWMP_{name}_Banner_{ratio}_{dims}.png").convert("RGB"), p))
    for key, name in STEPS.items():
        p = out / f"step-{key}.webp"
        report(p, save_webp(square(illustration(src / "welcome_step_illustrations_1024" / f"{name}.png"), 512), p))
    for key, name in EMPTY.items():
        p = out / f"empty-{key}.webp"
        report(p, save_webp(square(illustration(src / "empty_states_1024" / f"{name}.png"), 512), p))
    # icons: the manifest's, the apple touch one (opaque: iOS rounds it), the favicon from the flat glyph
    report(out / "icon-512.png", save_png(tile.resize((512, 512), Image.LANCZOS), out / "icon-512.png", quant=False))
    report(out / "icon-192.png", save_png(tile.resize((192, 192), Image.LANCZOS), out / "icon-192.png", quant=False))
    zoom = small_icon(tile, 720, zoom=1.12)
    touch = Image.new("RGBA", zoom.size, tuple(np.array(tile)[tile.height // 2, 40, :3]) + (255,))
    touch.alpha_composite(zoom)
    report(out / "apple-touch-icon.png", save_png(touch.convert("RGB").resize((180, 180), Image.LANCZOS), out / "apple-touch-icon.png", quant=False))
    mask = clean_glyph(src / "chat-5.png")
    glyph = glyph_image(mask)
    report(out / "glyph.png", save_png(square(glyph, 1024), out / "glyph.png", quant=False))
    report(out / "glyph-white.png", save_png(square(glyph_image(mask, (255, 255, 255)), 1024), out / "glyph-white.png", quant=False))
    n = write_ico(out / "favicon.ico", {s: square(glyph, s) for s in (16, 32, 48)})
    report(out / "favicon.ico", n)
    # the glyph as a vector: traced from the cleaned mask
    bw = Image.fromarray(np.where(smooth_mask(mask) > 127, 0, 255).astype(np.uint8)).convert("RGB")
    buf = io.BytesIO()
    bw.save(buf, "PNG")
    # polygon mode: the spline fit bends the thin swooshes out of shape; at this resolution the polygon is smooth
    svg = vtracer.convert_raw_image_to_svg(buf.getvalue(), img_format="png", colormode="binary", mode="polygon", filter_speckle=20)
    h, w = mask.shape
    svg = svg.replace('fill="#000000"', 'fill="#1553FA"').replace(f'width="{w}" height="{h}"', f'viewBox="0 0 {w} {h}" width="{w}" height="{h}"')
    (out / "glyph.svg").write_text(svg, encoding="utf-8")
    report(out / "glyph.svg", len(svg.encode()))
    pads = out / "pad"
    for name in PADS:
        im = trim(load(src / "controller_glyphs_separated_png" / f"{name}.png"), 8, 0.01)
        im = fit(im, 256, 256)
        key = name.lower()
        report(pads / f"{key}.png", save_png(pad_dark(im), pads / f"{key}.png"))
        report(pads / f"{key}-light.png", save_png(pad_light(im), pads / f"{key}-light.png"))


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="MKWMP art: the launcher's assets, the Steam library art, the website kit")
    ap.add_argument("--src", required=True, help="the art as drawn (chat-*.png and the packs)")
    ap.add_argument("--launcher", help="launcher/src/Mkwmp.App/Assets")
    ap.add_argument("--steam", help="tools/linux/art")
    ap.add_argument("--site", help="site/assets/art")
    a = ap.parse_args()
    src = pathlib.Path(a.src)
    if a.launcher:
        launcher(src, pathlib.Path(a.launcher))
    if a.steam:
        steam(src, pathlib.Path(a.steam))
    if a.site:
        site(src, pathlib.Path(a.site))
