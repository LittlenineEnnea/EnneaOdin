#!/usr/bin/env python3
"""EnneaOdin icon: macOS squircle in the app's accent gradient, a download
arrow, and the wordmark beside it. The type is fitted to the space, not
guessed: the font size is searched so the wider of the two lines lands inside
the right-hand column."""
from PIL import Image, ImageDraw, ImageFont

S, SS = 1024, 4
N = S * SS

TOP = (0x1A, 0x72, 0xDD)
BOT = (0x17, 0xA2, 0xB8)
WHITE = (255, 255, 255, 255)
FONT = '/System/Library/Fonts/Supplemental/Arial Black.ttf'
LINES = ('Scam', 'Sung')

INSET = round(N * 0.085)
RADIUS = round(N * 0.2246)

# Columns: arrow on the left, type on the right, both inside the squircle
# with a margin that clears the corner curve.
PAD = INSET + round(N * 0.055)
ARROW_CX = round(N * 0.335)
TEXT_LEFT = round(N * 0.470)
TEXT_RIGHT = N - PAD


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def fit_font(width, height):
    """Largest size at which both lines fit the box."""
    size = 10
    best = None
    while size < N:
        f = ImageFont.truetype(FONT, size)
        w = max(f.getbbox(t)[2] - f.getbbox(t)[0] for t in LINES)
        h = sum(f.getbbox(t)[3] - f.getbbox(t)[1] for t in LINES)
        if w > width or h > height:
            break
        best = f
        size += 4
    return best


img = Image.new('RGBA', (N, N), (0, 0, 0, 0))

grad = Image.new('RGB', (N, N))
gd = ImageDraw.Draw(grad)
for i in range(N * 2):
    gd.line([(i, 0), (0, i)], fill=lerp(TOP, BOT, i / (N * 2 - 1)))

mask = Image.new('L', (N, N), 0)
ImageDraw.Draw(mask).rounded_rectangle(
    [INSET, INSET, N - INSET, N - INSET], radius=RADIUS, fill=255)
img.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(img)

# ---- the arrow: shaft, head, and the line it lands on
sh_w = round(N * 0.080)
sh_top = round(N * 0.300)
sh_bot = round(N * 0.540)
d.rounded_rectangle([ARROW_CX - sh_w // 2, sh_top, ARROW_CX + sh_w // 2, sh_bot],
                    radius=sh_w // 2, fill=WHITE)

head_w = round(N * 0.215)
head_h = round(N * 0.140)
tip_y = round(N * 0.675)
d.polygon([(ARROW_CX - head_w // 2, tip_y - head_h),
           (ARROW_CX + head_w // 2, tip_y - head_h),
           (ARROW_CX, tip_y)], fill=WHITE)

bar_w = round(N * 0.270)
bar_h = round(N * 0.050)
bar_y = round(N * 0.735)
d.rounded_rectangle([ARROW_CX - bar_w // 2, bar_y, ARROW_CX + bar_w // 2, bar_y + bar_h],
                    radius=bar_h // 2, fill=WHITE)

# ---- the wordmark, fitted and centred on the arrow's own vertical span
font = fit_font(TEXT_RIGHT - TEXT_LEFT, round(N * 0.460))
gap = round(N * 0.022)
heights = [font.getbbox(t)[3] - font.getbbox(t)[1] for t in LINES]
block = sum(heights) + gap
y = round((sh_top + bar_y + bar_h) / 2 - block / 2)

for text, h in zip(LINES, heights):
    bbox = font.getbbox(text)
    d.text((TEXT_LEFT - bbox[0], y - bbox[1]), text, font=font, fill=WHITE)
    y += h + gap

out = img.resize((S, S), Image.LANCZOS)
out.save('icon.png')

f = ImageFont.truetype(FONT, font.size)
print('icon.png %dx%d, type %d px, widest line %d of %d available'
      % (S, S, font.size // SS,
         max(f.getbbox(t)[2] - f.getbbox(t)[0] for t in LINES) // SS,
         (TEXT_RIGHT - TEXT_LEFT) // SS))
