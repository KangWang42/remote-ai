"""Single-source generator for Remote AI application icons across Windows, Android and Web.

Generates:
  - agent-windows/remote-ai.ico      (16..64px 32-bit BGRA DIB + 128/256px PNG)
  - web/favicon.svg                  (shared vector mark for embedded & relay web pages)
  - android/res/drawable/ic_launcher_background.xml
  - android/res/drawable/ic_launcher_foreground.xml
  - android/res/drawable/ic_launcher_monochrome.xml

Optional preview sheets (Android adaptive masks + Windows 16px/32px pixel previews):
  python tools/build_icons.py --preview .build/icon-preview
"""
import argparse
import io
import math
import struct
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def _clamp(value, lo=0.0, hi=1.0):
    return lo if value < lo else hi if value > hi else value


def _lerp(a, b, t):
    return a + (b - a) * t


def _mix_rgb(c1, c2, t):
    return (
        int(round(_lerp(c1[0], c2[0], t))),
        int(round(_lerp(c1[1], c2[1], t))),
        int(round(_lerp(c1[2], c2[2], t))),
    )


def _smoothstep(edge0, edge1, x):
    t = _clamp((x - edge0) / (edge1 - edge0))
    return t * t * (3.0 - 2.0 * t)


def _render_background(size):
    """Deep midnight-indigo gradient with subtle cobalt top-left and emerald bottom-right glow."""
    base_size = min(size, 256)
    img = Image.new("RGBA", (base_size, base_size))
    px = img.load()
    c_top = (34, 52, 114)       # #223472
    c_mid = (20, 29, 61)        # #141D3D
    c_bot = (11, 16, 34)        # #0B1022
    glow_blue = (96, 165, 250)  # #60A5FA
    glow_mint = (52, 211, 153)  # #34D399
    inv = 1.0 / max(1, base_size - 1)
    for y in range(base_size):
        ny = y * inv
        for x in range(base_size):
            nx = x * inv
            t = _clamp(0.54 * nx + 0.46 * ny)
            if t < 0.55:
                r, g, b = _mix_rgb(c_top, c_mid, t / 0.55)
            else:
                r, g, b = _mix_rgb(c_mid, c_bot, (t - 0.55) / 0.45)

            d1 = math.hypot((nx - 0.24) / 0.62, (ny - 0.20) / 0.62)
            a1 = 0.34 * (1.0 - _smoothstep(0.0, 1.0, d1))
            d2 = math.hypot((nx - 0.80) / 0.52, (ny - 0.82) / 0.52)
            a2 = 0.21 * (1.0 - _smoothstep(0.0, 1.0, d2))

            r = int(round(_clamp(r * (1 - a1) + glow_blue[0] * a1, 0, 255)))
            g = int(round(_clamp(g * (1 - a1) + glow_blue[1] * a1, 0, 255)))
            b = int(round(_clamp(b * (1 - a1) + glow_blue[2] * a1, 0, 255)))

            r = int(round(_clamp(r * (1 - a2) + glow_mint[0] * a2, 0, 255)))
            g = int(round(_clamp(g * (1 - a2) + glow_mint[1] * a2, 0, 255)))
            b = int(round(_clamp(b * (1 - a2) + glow_mint[2] * a2, 0, 255)))
            px[x, y] = (r, g, b, 255)
    if base_size != size:
        return img.resize((size, size), Image.Resampling.BICUBIC)
    return img


def _rounded_mask(size, inset, radius):
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle([inset, inset, size - 1 - inset, size - 1 - inset], radius=radius, fill=255)
    return mask


def _star_polygon(cx, cy, outer_r, inner_ratio=0.24, steps=16):
    """Smooth 4-point AI sparkle polygon sampled from quadratic curves."""
    pts = []
    tips = [
        (cx, cy - outer_r),
        (cx + outer_r, cy),
        (cx, cy + outer_r),
        (cx - outer_r, cy),
    ]
    ctrl = [
        (cx + outer_r * inner_ratio, cy - outer_r * inner_ratio),
        (cx + outer_r * inner_ratio, cy + outer_r * inner_ratio),
        (cx - outer_r * inner_ratio, cy + outer_r * inner_ratio),
        (cx - outer_r * inner_ratio, cy - outer_r * inner_ratio),
    ]
    for i in range(4):
        p0 = tips[i]
        pc = ctrl[i]
        p1 = tips[(i + 1) % 4]
        for s in range(steps):
            t = s / steps
            mt = 1.0 - t
            x = mt * mt * p0[0] + 2 * mt * t * pc[0] + t * t * p1[0]
            y = mt * mt * p0[1] + 2 * mt * t * pc[1] + t * t * p1[1]
            pts.append((x, y))
    return pts


def _capsule_mask(size, p0, p1, width):
    """Draw a smooth round-capped segment onto an L mask."""
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    r = width * 0.5
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    length = math.hypot(dx, dy)
    if length > 0:
        nx, ny = -dy / length * r, dx / length * r
        poly = [
            (p0[0] + nx, p0[1] + ny),
            (p1[0] + nx, p1[1] + ny),
            (p1[0] - nx, p1[1] - ny),
            (p0[0] - nx, p0[1] - ny),
        ]
        draw.polygon(poly, fill=255)
    for cx, cy in (p0, p1):
        draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
    return mask


def _gradient_fill(size, c_start, c_end, vertical=False):
    base_size = min(size, 128)
    grad = Image.new("RGBA", (base_size, base_size))
    px = grad.load()
    inv = 1.0 / max(1, base_size - 1)
    for y in range(base_size):
        for x in range(base_size):
            t = y * inv if vertical else _clamp(0.65 * x * inv + 0.35 * y * inv)
            r, g, b = _mix_rgb(c_start, c_end, t)
            px[x, y] = (r, g, b, 255)
    if base_size != size:
        return grad.resize((size, size), Image.Resampling.BILINEAR)
    return grad


def _composite_masked(base, fill_img, mask):
    layer = Image.new("RGBA", base.size, (0, 0, 0, 0))
    layer.paste(fill_img, (0, 0), mask)
    return Image.alpha_composite(base, layer)


def render_icon(target_size, square_full=False):
    """Render the Remote AI icon at target_size with pixel-snapped optical sizing for small icons."""
    small = target_size <= 24 and not square_full
    ss = 8 if target_size <= 48 else 4
    w = target_size * ss

    bg = _render_background(w)

    if square_full:
        canvas = bg
    else:
        # Pixel-snapped insets so 16/20/24/32/48px edges stay razor sharp with zero half-pixel haze
        if target_size <= 24:
            inset = 0.0
            radius = target_size * 0.235 * ss
        elif target_size == 32:
            inset = 1.0 * ss
            radius = 7.0 * ss
        elif target_size <= 48:
            inset = round(target_size * 0.04) * ss
            radius = target_size * 0.225 * ss
        else:
            inset = target_size * 0.039 * ss
            radius = target_size * 0.225 * ss

        outer_mask = _rounded_mask(w, inset, radius)
        canvas = Image.new("RGBA", (w, w), (0, 0, 0, 0))
        canvas.paste(bg, (0, 0), outer_mask)

        # Subtle inner rim so the tile separates crisply on both dark and light taskbars
        border_w = (1.0 if target_size <= 32 else max(1.0, target_size * 0.012)) * ss
        inner_mask = _rounded_mask(w, inset + border_w, max(1.0, radius - border_w))
        ring_mask = ImageChops.subtract(outer_mask, inner_mask)
        ring_grad = Image.new("RGBA", (1, w))
        rpx = ring_grad.load()
        top_a = 92 if small else 78
        bot_a = 40 if small else 28
        for y in range(w):
            t = y / max(1, w - 1)
            a = int(round(_lerp(top_a, bot_a, t)))
            rgb = _mix_rgb((224, 242, 254), (125, 211, 252), t)
            rpx[0, y] = (rgb[0], rgb[1], rgb[2], a)
        ring_grad = ring_grad.resize((w, w), Image.Resampling.NEAREST)
        canvas = _composite_masked(canvas, ring_grad, ring_mask)

    scale = w / 256.0
    if small:
        # Optical sizing for 16/20/24px: crisp 2px-weight prompt, pixel-aligned bar & 4-point sparkle
        p_top = (56 * scale, 82 * scale)
        p_mid = (120 * scale, 130 * scale)
        p_bot = (56 * scale, 178 * scale)
        stroke = 31.0 * scale
        bar_p0 = (138 * scale, 178 * scale)
        bar_p1 = (198 * scale, 178 * scale)
        bar_w = 29.0 * scale
        star_cx, star_cy, star_r = 184 * scale, 74 * scale, 35.0 * scale
        star_inner = 0.20
    else:
        p_top = (66 * scale, 87 * scale)
        p_mid = (122 * scale, 131 * scale)
        p_bot = (66 * scale, 175 * scale)
        stroke = 24.5 * scale
        bar_p0 = (136 * scale, 175 * scale)
        bar_p1 = (194 * scale, 175 * scale)
        bar_w = 24.0 * scale
        star_cx, star_cy, star_r = 178 * scale, 81 * scale, 26.5 * scale
        star_inner = 0.24

    chev_mask = ImageChops.lighter(
        _capsule_mask(w, p_top, p_mid, stroke),
        _capsule_mask(w, p_mid, p_bot, stroke),
    )
    bar_mask = _capsule_mask(w, bar_p0, bar_p1, bar_w)
    star_mask = Image.new("L", (w, w), 0)
    ImageDraw.Draw(star_mask).polygon(_star_polygon(star_cx, star_cy, star_r, star_inner), fill=255)

    if not small:
        combined = ImageChops.lighter(ImageChops.lighter(chev_mask, bar_mask), star_mask)
        shadow = Image.new("RGBA", (w, w), (0, 0, 0, 0))
        shadow_mask = ImageChops.offset(combined, 0, int(round(5 * scale)))
        shadow_mask = shadow_mask.filter(ImageFilter.GaussianBlur(radius=6.5 * scale))
        shadow.paste(Image.new("RGBA", (w, w), (4, 8, 22, 120)), (0, 0), shadow_mask)
        canvas = Image.alpha_composite(canvas, shadow)

        mint_glow = bar_mask.filter(ImageFilter.GaussianBlur(radius=10 * scale))
        canvas = _composite_masked(canvas, Image.new("RGBA", (w, w), (52, 211, 153, 62)), mint_glow)

        star_glow = star_mask.filter(ImageFilter.GaussianBlur(radius=9 * scale))
        canvas = _composite_masked(canvas, Image.new("RGBA", (w, w), (125, 211, 252, 76)), star_glow)

    chev_grad = _gradient_fill(w, (255, 255, 255), (147, 197, 253), vertical=True)
    bar_grad = _gradient_fill(w, (52, 211, 153), (110, 231, 183), vertical=False)
    star_grad = _gradient_fill(w, (255, 255, 255), (125, 211, 252), vertical=True)

    canvas = _composite_masked(canvas, chev_grad, chev_mask)
    canvas = _composite_masked(canvas, bar_grad, bar_mask)
    canvas = _composite_masked(canvas, star_grad, star_mask)

    return canvas.resize((target_size, target_size), Image.Resampling.LANCZOS)


def _encode_ico_dib(img):
    """Encode an RGBA Pillow image as a 32-bit BGRA ICO DIB (BITMAPINFOHEADER + XOR + AND mask)."""
    img = img.convert("RGBA")
    w, h = img.size
    header = struct.pack(
        "<IiiHHIIiiII",
        40,
        w,
        h * 2,
        1,
        32,
        0,
        w * h * 4,
        0, 0, 0, 0,
    )
    px = img.load()
    xor_rows = bytearray()
    and_stride = ((w + 31) // 32) * 4
    and_rows = bytearray()
    for y in range(h - 1, -1, -1):
        row_and = bytearray(and_stride)
        for x in range(w):
            r, g, b, a = px[x, y]
            xor_rows.extend((b, g, r, a))
            if a == 0:
                row_and[x // 8] |= 1 << (7 - (x % 8))
        and_rows.extend(row_and)
    return header + bytes(xor_rows) + bytes(and_rows)


def _encode_png(img):
    buf = io.BytesIO()
    img.convert("RGBA").save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def build_ico(path):
    """Build multi-size Windows ICO: 32-bit DIB for <=64px (.NET/Win32 safe) + PNG for 128/256px."""
    entries = []
    for size in ICO_SIZES:
        img = render_icon(size)
        data = _encode_png(img) if size >= 128 else _encode_ico_dib(img)
        entries.append((size, data))

    count = len(entries)
    out = bytearray(struct.pack("<HHH", 0, 1, count))
    offset = 6 + count * 16
    for size, data in entries:
        dim = 0 if size >= 256 else size
        out.extend(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for _, data in entries:
        out.extend(data)
    path.write_bytes(bytes(out))


def build_web_favicon(path):
    svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-labelledby="title desc">
  <title id="title">Remote AI</title>
  <desc id="desc">Remote AI terminal prompt and sparkle mark in a deep indigo rounded tile</desc>
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0%" stop-color="#223472"/>
      <stop offset="55%" stop-color="#141d3d"/>
      <stop offset="100%" stop-color="#0b1022"/>
    </linearGradient>
    <radialGradient id="glow-blue" cx="24%" cy="20%" r="62%">
      <stop offset="0%" stop-color="#60a5fa" stop-opacity="0.34"/>
      <stop offset="100%" stop-color="#60a5fa" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="glow-mint" cx="80%" cy="82%" r="52%">
      <stop offset="0%" stop-color="#34d399" stop-opacity="0.22"/>
      <stop offset="100%" stop-color="#34d399" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="rim" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#e0f2fe" stop-opacity="0.30"/>
      <stop offset="100%" stop-color="#7dd3fc" stop-opacity="0.10"/>
    </linearGradient>
    <linearGradient id="prompt" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#ffffff"/>
      <stop offset="100%" stop-color="#93c5fd"/>
    </linearGradient>
    <linearGradient id="cursor" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0%" stop-color="#34d399"/>
      <stop offset="100%" stop-color="#6ee7b7"/>
    </linearGradient>
    <linearGradient id="spark" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#ffffff"/>
      <stop offset="100%" stop-color="#7dd3fc"/>
    </linearGradient>
  </defs>
  <rect x="10" y="10" width="236" height="236" rx="54" fill="url(#bg)"/>
  <rect x="10" y="10" width="236" height="236" rx="54" fill="url(#glow-blue)"/>
  <rect x="10" y="10" width="236" height="236" rx="54" fill="url(#glow-mint)"/>
  <rect x="11.5" y="11.5" width="233" height="233" rx="52.5" fill="none" stroke="url(#rim)" stroke-width="3"/>
  <path d="M66 87l56 44-56 44" fill="none" stroke="url(#prompt)" stroke-width="24.5" stroke-linecap="round" stroke-linejoin="round"/>
  <path d="M136 175h58" fill="none" stroke="url(#cursor)" stroke-width="24" stroke-linecap="round"/>
  <path d="M178 54.5Q184.4 74.6 204.5 81Q184.4 87.4 178 107.5Q171.6 87.4 151.5 81Q171.6 74.6 178 54.5Z" fill="url(#spark)"/>
</svg>
"""
    path.write_text(svg, encoding="utf-8", newline="\n")


def _android_coord(v, scale=0.28):
    return 54.0 + (v - 128.0) * scale


def build_android_drawables(res_dir):
    """Write Android adaptive icon background, foreground, monochrome and launcher XMLs."""
    drawable = res_dir / "drawable"
    mipmap_v26 = res_dir / "mipmap-anydpi-v26"
    mipmap_default = res_dir / "mipmap"

    bg_xml = """<?xml version="1.0" encoding="utf-8"?>
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:aapt="http://schemas.android.com/aapt"
    android:width="108dp"
    android:height="108dp"
    android:viewportWidth="108"
    android:viewportHeight="108">
    <path android:pathData="M0,0h108v108h-108z">
        <aapt:attr name="android:fillColor">
            <gradient
                android:type="linear"
                android:startX="12"
                android:startY="10"
                android:endX="98"
                android:endY="98">
                <item android:offset="0.0" android:color="#FF223472" />
                <item android:offset="0.55" android:color="#FF141D3D" />
                <item android:offset="1.0" android:color="#FF0B1022" />
            </gradient>
        </aapt:attr>
    </path>
    <path android:pathData="M0,0h108v108h-108z">
        <aapt:attr name="android:fillColor">
            <gradient
                android:type="radial"
                android:centerX="32"
                android:centerY="28"
                android:gradientRadius="52">
                <item android:offset="0.0" android:color="#5760A5FA" />
                <item android:offset="1.0" android:color="#0060A5FA" />
            </gradient>
        </aapt:attr>
    </path>
    <path android:pathData="M0,0h108v108h-108z">
        <aapt:attr name="android:fillColor">
            <gradient
                android:type="radial"
                android:centerX="78"
                android:centerY="80"
                android:gradientRadius="44">
                <item android:offset="0.0" android:color="#3634D399" />
                <item android:offset="1.0" android:color="#0034D399" />
            </gradient>
        </aapt:attr>
    </path>
</vector>
"""
    (drawable / "ic_launcher_background.xml").write_text(bg_xml, encoding="utf-8", newline="\n")

    s = 0.28
    px0, py0 = _android_coord(66, s), _android_coord(87, s)
    px1, py1 = _android_coord(122, s), _android_coord(131, s)
    px2, py2 = _android_coord(66, s), _android_coord(175, s)
    bx0, by0 = _android_coord(136, s), _android_coord(175, s)
    bx1 = _android_coord(194, s)
    sx, sy = _android_coord(178, s), _android_coord(81, s)
    sr = 26.5 * s
    si = sr * 0.24
    sw_p = 24.5 * s
    sw_b = 24.0 * s

    chev_path = f"M{px0:.2f},{py0:.2f}L{px1:.2f},{py1:.2f}L{px2:.2f},{py2:.2f}"
    bar_path = f"M{bx0:.2f},{by0:.2f}L{bx1:.2f},{by0:.2f}"
    star_path = (
        f"M{sx:.2f},{sy - sr:.2f}"
        f"Q{sx + si:.2f},{sy - si:.2f} {sx + sr:.2f},{sy:.2f}"
        f"Q{sx + si:.2f},{sy + si:.2f} {sx:.2f},{sy + sr:.2f}"
        f"Q{sx - si:.2f},{sy + si:.2f} {sx - sr:.2f},{sy:.2f}"
        f"Q{sx - si:.2f},{sy - si:.2f} {sx:.2f},{sy - sr:.2f}Z"
    )

    fg_xml = f"""<?xml version="1.0" encoding="utf-8"?>
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:aapt="http://schemas.android.com/aapt"
    android:width="108dp"
    android:height="108dp"
    android:viewportWidth="108"
    android:viewportHeight="108">
    <group android:translateY="1.4">
        <path
            android:pathData="{chev_path}"
            android:strokeColor="#48040816"
            android:strokeWidth="{sw_p:.2f}"
            android:strokeLineCap="round"
            android:strokeLineJoin="round" />
        <path
            android:pathData="{bar_path}"
            android:strokeColor="#48040816"
            android:strokeWidth="{sw_b:.2f}"
            android:strokeLineCap="round" />
        <path
            android:pathData="{star_path}"
            android:fillColor="#48040816" />
    </group>
    <path
        android:pathData="{chev_path}"
        android:strokeWidth="{sw_p:.2f}"
        android:strokeLineCap="round"
        android:strokeLineJoin="round">
        <aapt:attr name="android:strokeColor">
            <gradient
                android:type="linear"
                android:startX="{px0:.2f}"
                android:startY="{py0:.2f}"
                android:endX="{px1:.2f}"
                android:endY="{py2:.2f}">
                <item android:offset="0.0" android:color="#FFFFFFFF" />
                <item android:offset="1.0" android:color="#FF93C5FD" />
            </gradient>
        </aapt:attr>
    </path>
    <path
        android:pathData="{bar_path}"
        android:strokeWidth="{sw_b:.2f}"
        android:strokeLineCap="round">
        <aapt:attr name="android:strokeColor">
            <gradient
                android:type="linear"
                android:startX="{bx0:.2f}"
                android:startY="{by0:.2f}"
                android:endX="{bx1:.2f}"
                android:endY="{by0:.2f}">
                <item android:offset="0.0" android:color="#FF34D399" />
                <item android:offset="1.0" android:color="#FF6EE7B7" />
            </gradient>
        </aapt:attr>
    </path>
    <path android:pathData="{star_path}">
        <aapt:attr name="android:fillColor">
            <gradient
                android:type="linear"
                android:startX="{sx:.2f}"
                android:startY="{sy - sr:.2f}"
                android:endX="{sx:.2f}"
                android:endY="{sy + sr:.2f}">
                <item android:offset="0.0" android:color="#FFFFFFFF" />
                <item android:offset="1.0" android:color="#FF7DD3FC" />
            </gradient>
        </aapt:attr>
    </path>
</vector>
"""
    (drawable / "ic_launcher_foreground.xml").write_text(fg_xml, encoding="utf-8", newline="\n")

    mono_xml = f"""<?xml version="1.0" encoding="utf-8"?>
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="108dp"
    android:height="108dp"
    android:viewportWidth="108"
    android:viewportHeight="108">
    <path
        android:pathData="{chev_path}"
        android:strokeColor="#FFFFFFFF"
        android:strokeWidth="{sw_p:.2f}"
        android:strokeLineCap="round"
        android:strokeLineJoin="round" />
    <path
        android:pathData="{bar_path}"
        android:strokeColor="#FFFFFFFF"
        android:strokeWidth="{sw_b:.2f}"
        android:strokeLineCap="round" />
    <path
        android:pathData="{star_path}"
        android:fillColor="#FFFFFFFF" />
</vector>
"""
    (drawable / "ic_launcher_monochrome.xml").write_text(mono_xml, encoding="utf-8", newline="\n")

    adaptive_xml = """<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@drawable/ic_launcher_background" />
    <foreground android:drawable="@drawable/ic_launcher_foreground" />
    <monochrome android:drawable="@drawable/ic_launcher_monochrome" />
</adaptive-icon>
"""
    (mipmap_v26 / "ic_launcher.xml").write_text(adaptive_xml, encoding="utf-8", newline="\n")

    default_xml = """<?xml version="1.0" encoding="utf-8"?>
<!-- The default icon. An adaptive icon under mipmap-anydpi-v26 alone leaves no
     default configuration, and some installers cannot parse the APK's icon.
     API 26+ still uses the adaptive layer; this one is the same design as a
     plain layer-list for everything that asks for the unqualified resource. -->
<layer-list xmlns:android="http://schemas.android.com/apk/res/android">
    <item android:drawable="@drawable/ic_launcher_background" />
    <item android:drawable="@drawable/ic_launcher_foreground" />
</layer-list>
"""
    (mipmap_default / "ic_launcher.xml").write_text(default_xml, encoding="utf-8", newline="\n")


def _extract_ico_frames(ico_path):
    """Read back the actual frames stored inside the ICO file."""
    raw = ico_path.read_bytes()
    _, _, count = struct.unpack_from("<HHH", raw)
    frames = {}
    for i in range(count):
        w, h, _, _, _, bpp, sz, off = struct.unpack_from("<BBBBHHII", raw, 6 + i * 16)
        size = w or 256
        chunk = raw[off:off + sz]
        if chunk[:4] == b"\x89PNG":
            frames[size] = Image.open(io.BytesIO(chunk)).convert("RGBA")
        else:
            header_sz, bw, bh, _, bit_cnt = struct.unpack_from("<IiiHH", chunk)
            assert bit_cnt == 32 and bw == size and bh == size * 2
            img = Image.new("RGBA", (size, size))
            px = img.load()
            pos = header_sz
            for y in range(size - 1, -1, -1):
                for x in range(size):
                    b, g, r, a = chunk[pos:pos + 4]
                    px[x, y] = (r, g, b, a)
                    pos += 4
            frames[size] = img
    return frames


def _render_android_full_108(size=432):
    """Render the exact 108dp adaptive icon composite (background + foreground) for crop previews."""
    bg = _render_background(size)
    s = 0.28 * (size / 108.0)
    cx_map = lambda v: (54.0 + (v - 128.0) * 0.28) * (size / 108.0)

    p_top = (cx_map(66), cx_map(87))
    p_mid = (cx_map(122), cx_map(131))
    p_bot = (cx_map(66), cx_map(175))
    bar_p0 = (cx_map(136), cx_map(175))
    bar_p1 = (cx_map(194), cx_map(175))
    sx, sy = cx_map(178), cx_map(81)
    sr = 26.5 * s

    chev_mask = ImageChops.lighter(
        _capsule_mask(size, p_top, p_mid, 24.5 * s),
        _capsule_mask(size, p_mid, p_bot, 24.5 * s),
    )
    bar_mask = _capsule_mask(size, bar_p0, bar_p1, 24.0 * s)
    star_mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(star_mask).polygon(_star_polygon(sx, sy, sr, 0.24), fill=255)

    combined = ImageChops.lighter(ImageChops.lighter(chev_mask, bar_mask), star_mask)
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    shadow_mask = ImageChops.offset(combined, 0, int(round(1.4 * size / 108.0)))
    shadow_mask = shadow_mask.filter(ImageFilter.GaussianBlur(radius=1.8 * size / 108.0))
    shadow.paste(Image.new("RGBA", (size, size), (4, 8, 22, 110)), (0, 0), shadow_mask)
    canvas = Image.alpha_composite(bg, shadow)

    chev_grad = _gradient_fill(size, (255, 255, 255), (147, 197, 253), vertical=True)
    bar_grad = _gradient_fill(size, (52, 211, 153), (110, 231, 183), vertical=False)
    star_grad = _gradient_fill(size, (255, 255, 255), (125, 211, 252), vertical=True)

    canvas = _composite_masked(canvas, chev_grad, chev_mask)
    canvas = _composite_masked(canvas, bar_grad, bar_mask)
    canvas = _composite_masked(canvas, star_grad, star_mask)
    return canvas


def _shape_mask(size, kind):
    ss = 4
    w = size * ss
    mask = Image.new("L", (w, w), 0)
    draw = ImageDraw.Draw(mask)
    if kind == "circle":
        draw.ellipse([0, 0, w - 1, w - 1], fill=255)
    elif kind == "rounded":
        draw.rounded_rectangle([0, 0, w - 1, w - 1], radius=w * 0.225, fill=255)
    elif kind == "squircle":
        pts = []
        half = w / 2.0
        for i in range(180):
            ang = 2 * math.pi * i / 180
            ca, sa = math.cos(ang), math.sin(ang)
            x = half + (half - 1) * math.copysign(abs(ca) ** 0.55, ca)
            y = half + (half - 1) * math.copysign(abs(sa) ** 0.55, sa)
            pts.append((x, y))
        draw.polygon(pts, fill=255)
    elif kind == "teardrop":
        # Standard Android teardrop: circle with bottom-right quadrant extended to a rounded corner
        draw.ellipse([0, 0, w - 1, w - 1], fill=255)
        draw.pieslice([0, 0, w - 1, w - 1], 0, 90, fill=255)
        draw.rectangle([w // 2, w // 2, w - 1, w - 1], fill=255)
        # Round only the outer bottom-right corner cleanly
        cr = int(round(w * 0.16))
        corner = Image.new("L", (w, w), 255)
        cdraw = ImageDraw.Draw(corner)
        cdraw.rectangle([w - cr, w - cr, w - 1, w - 1], fill=0)
        cdraw.pieslice([w - 2 * cr, w - 2 * cr, w - 1, w - 1], 0, 90, fill=255)
        mask = ImageChops.darker(mask, corner)
    return mask.resize((size, size), Image.Resampling.LANCZOS)


def build_previews(out_dir, ico_path):
    """Generate Android crop preview sheet and Windows 16/32px pixel preview sheet."""
    out_dir.mkdir(parents=True, exist_ok=True)

    full = _render_android_full_108(432)
    visible = full.crop((72, 72, 360, 360))

    sheet = Image.new("RGBA", (1120, 620), (244, 246, 250, 255))
    draw = ImageDraw.Draw(sheet)
    draw.rectangle([0, 330, 1120, 620], fill=(22, 25, 34, 255))

    keyline = full.resize((216, 216), Image.Resampling.LANCZOS)
    kdraw = ImageDraw.Draw(keyline)
    kdraw.rectangle([36, 36, 180, 180], outline=(147, 197, 253, 210), width=2)
    kdraw.ellipse([42, 42, 174, 174], outline=(52, 211, 153, 230), width=2)
    sheet.alpha_composite(keyline, (36, 56))
    draw.text((36, 24), "108dp Adaptive Canvas (Blue=72dp, Green=66dp Safe)", fill=(30, 41, 59, 255))

    shapes = [("Circle", "circle"), ("Squircle", "squircle"), ("Rounded", "rounded"), ("Teardrop", "teardrop")]
    for idx, (label, kind) in enumerate(shapes):
        x = 300 + idx * 196
        icon160 = visible.resize((160, 160), Image.Resampling.LANCZOS)
        mask160 = _shape_mask(160, kind)
        cropped = Image.new("RGBA", (160, 160), (0, 0, 0, 0))
        cropped.paste(icon160, (0, 0), mask160)
        sheet.alpha_composite(cropped, (x, 76))
        draw.text((x + 48, 46), label, fill=(30, 41, 59, 255))
        sheet.alpha_composite(cropped, (x, 388))
        draw.text((x + 48, 358), label, fill=(226, 232, 240, 255))

    draw.text((36, 358), "Home Screen 60px Preview", fill=(226, 232, 240, 255))
    for idx, (_, kind) in enumerate(shapes):
        small60 = visible.resize((60, 60), Image.Resampling.LANCZOS)
        m60 = _shape_mask(60, kind)
        c60 = Image.new("RGBA", (60, 60), (0, 0, 0, 0))
        c60.paste(small60, (0, 0), m60)
        sheet.alpha_composite(c60, (36 + (idx % 2) * 86, 404 + (idx // 2) * 86))

    android_preview = out_dir / "android-crop-preview.png"
    sheet.save(android_preview)

    frames = _extract_ico_frames(ico_path)
    win = Image.new("RGBA", (1120, 560), (243, 245, 249, 255))
    wdraw = ImageDraw.Draw(win)
    wdraw.rectangle([0, 290, 1120, 560], fill=(24, 27, 34, 255))
    wdraw.text((32, 20), "Windows ICO Actual Extracted Frames (Light & Dark Surfaces)", fill=(30, 41, 59, 255))

    sizes_1x = (16, 20, 24, 32, 48, 64)
    cx = 36
    for s in sizes_1x:
        im = frames[s]
        win.alpha_composite(im, (cx, 120 - s // 2))
        wdraw.text((cx, 168), f"{s}px", fill=(51, 65, 85, 255))
        win.alpha_composite(im, (cx, 410 - s // 2))
        wdraw.text((cx, 458), f"{s}px", fill=(203, 213, 225, 255))
        cx += max(s, 36) + 26

    z16 = frames[16].resize((160, 160), Image.Resampling.NEAREST)
    z32 = frames[32].resize((160, 160), Image.Resampling.NEAREST)
    z256 = frames[256].resize((160, 160), Image.Resampling.LANCZOS)

    for bx, label, img in ((520, "16x16 (10x Nearest)", z16), (720, "32x32 (5x Nearest)", z32), (920, "256x256 Master", z256)):
        wdraw.text((bx, 48), label, fill=(30, 41, 59, 255))
        win.alpha_composite(img, (bx, 76))
        wdraw.text((bx, 318), label, fill=(226, 232, 240, 255))
        win.alpha_composite(img, (bx, 346))

    frames[16].save(out_dir / "windows-16px.png")
    frames[32].save(out_dir / "windows-32px.png")
    windows_preview = out_dir / "windows-16-32-preview.png"
    win.save(windows_preview)
    return android_preview, windows_preview


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preview", type=Path, default=None, help="Write Android crop and Windows 16/32px preview PNGs to this folder")
    args = parser.parse_args()

    ico_path = ROOT / "agent-windows" / "remote-ai.ico"
    build_ico(ico_path)
    build_web_favicon(ROOT / "web" / "favicon.svg")
    build_android_drawables(ROOT / "android" / "res")
    print(f"Generated {ico_path.relative_to(ROOT)} ({ico_path.stat().st_size} bytes), web/favicon.svg, and Android launcher drawables.")

    if args.preview:
        a_prev, w_prev = build_previews(args.preview, ico_path)
        print(f"Previews saved: {a_prev}, {w_prev}")


if __name__ == "__main__":
    main()
