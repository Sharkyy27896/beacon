from PIL import Image, ImageDraw, ImageFilter
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "src" / "Beacon" / "Assets" / "beacon.ico"
OUT.parent.mkdir(parents=True, exist_ok=True)

S = 512
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Soft outer glow
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.rounded_rectangle((72, 72, 440, 440), radius=104, fill=(56, 140, 255, 170))
glow = glow.filter(ImageFilter.GaussianBlur(38))
img.alpha_composite(glow)

# Dark rounded tile with gradient-ish layered fill
d.rounded_rectangle((82, 82, 430, 430), radius=100, fill=(15, 23, 42, 255))
d.rounded_rectangle((82, 82, 430, 300), radius=100, fill=(24, 37, 68, 255))
d.rounded_rectangle((92, 92, 420, 420), radius=92, outline=(86, 139, 220, 190), width=4)

# Bell body
bell = (104, 200, 355, 344)
d.rounded_rectangle(bell, radius=64, fill=(96, 188, 255, 255))
d.rounded_rectangle((114, 210, 345, 330), radius=52, fill=(150, 214, 255, 255))
d.rounded_rectangle((228, 152, 232, 182), radius=3, fill=(150, 214, 255, 255))
d.ellipse((218, 140, 242, 164), fill=(150, 214, 255, 255))
d.rounded_rectangle((202, 344, 258, 376), radius=16, fill=(96, 188, 255, 255))

# Clapper highlight
d.ellipse((222, 352, 238, 368), fill=(235, 248, 255, 255))

img.save(OUT, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print(OUT)
