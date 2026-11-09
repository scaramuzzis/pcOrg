import sys
from PIL import Image, ImageDraw
S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
# gradient background in a rounded square
grad = Image.new("RGBA", (S, S))
top, bottom = (59, 130, 246), (79, 70, 229)  # blu -> indaco
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / (S - 1)
    gd.line([(0, y), (S, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([40, 40, S - 40, S - 40], radius=220, fill=255)
img.paste(grad, (0, 0), mask)
d = ImageDraw.Draw(img)
# folder (back tab + body)
white = (255, 255, 255, 255)
soft = (219, 234, 254, 255)
d.rounded_rectangle([200, 300, 500, 420], radius=40, fill=soft)
d.rounded_rectangle([200, 360, 824, 760], radius=60, fill=soft)
d.rounded_rectangle([200, 420, 824, 760], radius=60, fill=white)
# three "ordered" bars inside the folder
bar = (79, 70, 229, 255)
for i, w in enumerate([360, 280, 200]):
    y = 490 + i * 80
    d.rounded_rectangle([270, y, 270 + w, y + 40], radius=20, fill=bar)
# magnifier
cx, cy, r = 700, 640, 120
d.ellipse([cx - r - 30, cy - r - 30, cx + r + 30, cy + r + 30], fill=white)
d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(191, 219, 254, 255), outline=(37, 99, 235, 255), width=40)
d.line([(cx + 80, cy + 80), (cx + 200, cy + 200)], fill=(37, 99, 235, 255), width=70)
d.ellipse([cx + 165, cy + 165, cx + 235, cy + 235], fill=(37, 99, 235, 255))
out = sys.argv[1]
sizes = [16, 24, 32, 48, 64, 128, 256]
img.save(out + ".ico", sizes=[(s, s) for s in sizes])
img.resize((256, 256), Image.LANCZOS).save(out + ".png")
img.resize((512, 512), Image.LANCZOS).save(out + "-preview.png")
