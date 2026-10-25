"""Extracts V Rising's in-game world map from your own game install and saves it next to the viewer.

The map is the texture the game shows on its map screen (ZoneMap_Wilderness_VRisingWorld). It is game art, so it is
not shipped with this project: everyone extracts it from their own copy of the game.

    pip install UnityPy
    python tools/extract_map.py "C:/Program Files (x86)/Steam/steamapps/common/VRising"

The image is written to viewer/map.jpg, which the viewer loads automatically.
"""
import os
import sys

import UnityPy

MAP_TEXTURE = "ZoneMap_Wilderness_VRisingWorld"


def find_map(data_dir):
    # It has lived in resources.assets; fall back to scanning the content archives if a patch moves it.
    candidates = [os.path.join(data_dir, "resources.assets")]
    archives = os.path.join(data_dir, "StreamingAssets", "ContentArchives")
    if os.path.isdir(archives):
        candidates += [os.path.join(archives, f) for f in os.listdir(archives)]
    for path in candidates:
        if not os.path.isfile(path):
            continue
        try:
            env = UnityPy.load(path)
        except Exception:
            continue
        for obj in env.objects:
            if obj.type.name != "Texture2D":
                continue
            tex = obj.read()
            if tex.m_Name == MAP_TEXTURE:
                return tex
    return None


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    game_dir = sys.argv[1]
    data_dir = os.path.join(game_dir, "VRising_Data")
    if not os.path.isdir(data_dir):
        sys.exit(f"Couldn't find VRising_Data in {game_dir}. Pass the folder that contains VRising.exe.")

    print(f"Looking for {MAP_TEXTURE}...")
    tex = find_map(data_dir)
    if tex is None:
        sys.exit("Map texture not found. The game may have renamed it in an update.")

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "viewer", "map.jpg")
    tex.image.convert("RGB").save(out, quality=90)
    print(f"Saved {tex.m_Width}x{tex.m_Height} map to {os.path.normpath(out)}")


if __name__ == "__main__":
    main()
