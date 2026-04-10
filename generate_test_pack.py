"""
generate_test_pack.py
=====================
Generates a 260-paint test modlet for validating PaintUnlocked.

Paints 1-254 use vanilla-range indices (should work without patch).
Paints 255-260 use extended-range indices (only work with PaintUnlocked).

Each paint is a solid color swatch - no real textures needed.

Usage:
    python generate_test_pack.py

Output:
    ./PaintUnlockedTest/   (drop this folder into your Mods/ directory)

Requirements:
    pip install UnityPy Pillow
"""

import sys
import shutil
from pathlib import Path

try:
    from PIL import Image
    import UnityPy
except ImportError:
    print("Missing dependencies. Run: pip install UnityPy Pillow")
    sys.exit(1)

# -----------------------------------------------------------------------
# Config
# -----------------------------------------------------------------------
PACK_NAME       = "PaintUnlockedTest"
PAINT_COUNT     = 260
OUTPUT_DIR      = Path(__file__).parent / PACK_NAME
TEMPLATE_PATH   = Path("C:/Users/darab/WebstormProjects/KitsunePaint/scripts/Atlas.template.unity3d")
TARGET_SIZE     = (512, 512)
MIP_COUNT       = 10
FMT_DXT1        = 10
FMT_DXT5        = 12

DEFAULT_NORMAL_COLOR   = (255, 128, 0, 128)
DEFAULT_SPECULAR_COLOR = (16, 200, 0, 235)

# -----------------------------------------------------------------------
# Color generation - spread 260 colors across HSV space
# -----------------------------------------------------------------------
def make_color(index: int, total: int) -> tuple:
    import colorsys
    hue = (index / total) % 1.0
    band = (index // 10) % 3
    sat = [1.0, 0.7, 0.5][band]
    val = [0.9, 0.8, 1.0][band]
    r, g, b = colorsys.hsv_to_rgb(hue, sat, val)
    return (int(r * 255), int(g * 255), int(b * 255), 255)


# -----------------------------------------------------------------------
# Bundle builder (matches KitsunePaint's build_bundle.py logic exactly)
# -----------------------------------------------------------------------
def rename_cab(bundle, paint_name: str) -> None:
    cab_name  = f"CAB-{paint_name}"
    ress_name = f"CAB-{paint_name}.resS"
    old_files = dict(bundle.files)
    bundle.files.clear()
    for key, val in old_files.items():
        if key.endswith(".resS"):
            bundle.files[ress_name] = val
        else:
            if hasattr(val, "name"):
                val.name = cab_name
            bundle.files[cab_name] = val


def build_bundle(paint_name: str, diffuse: Image.Image, output_path: Path) -> None:
    neutral_normal   = Image.new("RGBA", TARGET_SIZE, DEFAULT_NORMAL_COLOR)
    default_specular = Image.new("RGBA", TARGET_SIZE, DEFAULT_SPECULAR_COLOR)

    env    = UnityPy.load(str(TEMPLATE_PATH))
    bundle = env.file

    rename_cab(bundle, paint_name)

    ab_object = next(obj for obj in env.objects if obj.type.name == "AssetBundle")
    ab_data   = ab_object.parse_as_object()
    container_entries = list(ab_data.m_Container)
    tex_by_pathid     = {obj.path_id: obj for obj in env.objects if obj.type.name == "Texture2D"}

    fmt12_slots   = []
    diffuse_slots = []
    for i, (path, info) in enumerate(container_entries):
        path_id = info.asset.m_PathID
        tex     = tex_by_pathid[path_id].parse_as_object()
        fmt     = int(tex.m_TextureFormat)
        if fmt == FMT_DXT1:
            diffuse_slots.append((i, path, info, path_id))
        else:
            fmt12_slots.append((i, path, info, path_id))

    specular_slots = [fmt12_slots[-1]] if fmt12_slots else []
    normal_slots   = fmt12_slots[:-1]  if len(fmt12_slots) > 1 else []

    new_container = []

    if diffuse_slots:
        slot_i, _, asset_info, path_id = diffuse_slots[0]
        tex_data = tex_by_pathid[path_id].parse_as_object()
        tex_data.set_image(diffuse.convert("RGBA"), mipmap_count=MIP_COUNT)
        tex_data.m_Name = f"{paint_name}_diffuse"
        tex_data.save()
        new_container.append((slot_i, f"assets/{paint_name}_diffuse.png", asset_info))

    if normal_slots:
        slot_i, _, asset_info, path_id = normal_slots[0]
        tex_data = tex_by_pathid[path_id].parse_as_object()
        tex_data.set_image(neutral_normal, mipmap_count=MIP_COUNT)
        tex_data.m_Name = f"{paint_name}_normal"
        tex_data.save()
        new_container.append((slot_i, f"assets/{paint_name}_normal.png", asset_info))

    if specular_slots:
        slot_i, _, asset_info, path_id = specular_slots[0]
        tex_data = tex_by_pathid[path_id].parse_as_object()
        tex_data.set_image(default_specular, mipmap_count=MIP_COUNT)
        tex_data.m_Name = f"{paint_name}_specular"
        tex_data.save()
        new_container.append((slot_i, f"assets/{paint_name}_specular.png", asset_info))

    new_container.sort(key=lambda x: x[0])
    ab_data.m_Container = [(path, info) for _, path, info in new_container]

    container_pathids = {info.asset.m_PathID for _, info in ab_data.m_Container}
    ab_data.m_PreloadTable = [p for p in ab_data.m_PreloadTable if p.m_PathID in container_pathids]
    ab_data.save()

    with open(output_path, "wb") as f:
        f.write(bundle.save())


# -----------------------------------------------------------------------
# XML / config generation
# -----------------------------------------------------------------------
def generate_painting_xml(paints: list) -> str:
    entries = []
    for i, name in enumerate(paints, 1):
        bundle_name = f"Atlas_{i:03d}.unity3d"
        paint_id    = f"paintunlocked_test_{name}"
        tex_name    = f"txName_{paint_id}"
        entries.append(f"""  <opaque id="{paint_id}" name="{tex_name}" x="0" y="0" w="1" h="1" blockw="1" blockh="1">
    <property name="Diffuse"   value="#@modfolder:Resources/{bundle_name}?assets/{name}_diffuse.png"/>
    <property name="Normal"    value="#@modfolder:Resources/{bundle_name}?assets/{name}_normal.png"/>
    <property name="Specular"  value="#@modfolder:Resources/{bundle_name}?assets/{name}_specular.png"/>
    <property name="PaintCost" value="1"/>
    <property name="Hidden"    value="false"/>
    <property name="Group"     value="txGroupCustom"/>
    <property name="SortIndex" value="255"/>
  </opaque>""")
    return "<configs><append xpath=\"/paints\">\n" + "\n".join(entries) + "\n</append></configs>"


def generate_localization(paints: list) -> str:
    lines = ["Key,File,Type,UsedInMainMenu,NoTranslate,english"]
    for name in paints:
        paint_id = f"paintunlocked_test_{name}"
        tex_name = f"txName_{paint_id}"
        num      = name.replace("test_paint_", "").lstrip("0") or "0"
        lines.append(f"{tex_name},painting,txGroupCustom,,,Test Paint {num}")
    return "\n".join(lines)


def generate_modinfo() -> str:
    return """<?xml version="1.0" encoding="UTF-8"?>
<xml>
  <Name value="PaintUnlockedTest"/>
  <DisplayName value="Paint Unlocked - Test Pack"/>
  <Version value="1.0.0"/>
  <Author value="AdaInTheLab"/>
  <Description value="260 solid color test paints for validating PaintUnlocked. Paints 255-260 require PaintUnlocked mod. Requires OCBCustomTextures."/>
  <Website value="https://github.com/Kitsune-Den/KitsunePaintUnlocked"/>
</xml>"""


# -----------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------
def main():
    if not TEMPLATE_PATH.exists():
        print(f"ERROR: Template not found at {TEMPLATE_PATH}")
        sys.exit(1)

    if OUTPUT_DIR.exists():
        shutil.rmtree(OUTPUT_DIR)

    resources_dir = OUTPUT_DIR / "Resources"
    config_dir    = OUTPUT_DIR / "Config"
    resources_dir.mkdir(parents=True)
    config_dir.mkdir(parents=True)

    paints = [f"test_paint_{i:03d}" for i in range(1, PAINT_COUNT + 1)]

    print(f"Generating {PACK_NAME} with {PAINT_COUNT} paints...")
    print(f"Paints 1-254:   vanilla range")
    print(f"Paints 255-260: extended range (require PaintUnlocked)\n")

    for i, name in enumerate(paints, 1):
        color      = make_color(i - 1, PAINT_COUNT)
        diffuse    = Image.new("RGBA", TARGET_SIZE, color)
        bundle_out = resources_dir / f"Atlas_{i:03d}.unity3d"
        label      = "EXTENDED" if i >= 255 else "vanilla "
        print(f"  [{label}] Paint {i:03d}: RGB({color[0]:3d},{color[1]:3d},{color[2]:3d}) -> Atlas_{i:03d}.unity3d")
        build_bundle(name, diffuse, bundle_out)

    (config_dir / "painting.xml").write_text(generate_painting_xml(paints), encoding="utf-8")
    (config_dir / "Localization.txt").write_text(generate_localization(paints), encoding="utf-8")
    (OUTPUT_DIR / "ModInfo.xml").write_text(generate_modinfo(), encoding="utf-8")

    print(f"\n✅ Done! Modlet at: {OUTPUT_DIR}")
    print(f"\nNext steps:")
    print(f"  1. Copy PaintUnlockedTest to F:\\72D2D-Server\\Mods\\")
    print(f"  2. Make sure OCBCustomTextures + PaintUnlocked are in Mods/")
    print(f"  3. Restart server via launch.bat")
    print(f"  4. In game: open paint menu, scroll to the end")
    print(f"  5. Paints 255-260 = distinct colors means patch works")
    print(f"     Wrong color / all same = patch isn't firing")


if __name__ == "__main__":
    main()
