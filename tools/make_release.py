"""Builds the release zips into dist/ from the current Release build.

    dotnet build plugin -c Release
    python tools/make_release.py

dist/ResourceMap-<version>.zip              GitHub / Nexus: extract into the server's main folder.
dist/ToobsTube-Prospector-<version>.zip      Thunderstore package (manifest, icon, README, plugins/).
"""
import json
import os
import re
import sys
import zipfile

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DLL = os.path.join(ROOT, "plugin", "bin", "Release", "net6.0", "ResourceMap.dll")
CONFIG = os.path.join(ROOT, "config", "Prospector.cfg")
DIST = os.path.join(ROOT, "dist")

THUNDERSTORE_DEPENDENCIES = [
    "BepInEx-BepInExPack_V_Rising-1.733.2",
    "deca-VampireCommandFramework-0.11.0",
]


def version():
    csproj = open(os.path.join(ROOT, "plugin", "ResourceMap.csproj"), encoding="utf-8").read()
    return re.search(r"<Version>([^<]+)</Version>", csproj).group(1)


def main():
    if not os.path.isfile(DLL):
        sys.exit(f"Build the plugin first: dotnet build plugin -c Release ({DLL} not found)")
    v = version()
    os.makedirs(DIST, exist_ok=True)

    # Manual install: paths mirror the server folder, so extracting into it puts everything in place.
    manual = os.path.join(DIST, f"ResourceMap-{v}.zip")
    with zipfile.ZipFile(manual, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(DLL, "BepInEx/plugins/ResourceMap/ResourceMap.dll")
        z.write(CONFIG, "BepInEx/plugins/ResourceMap/Prospector.cfg.template")
        z.write(os.path.join(ROOT, "README.md"), "BepInEx/plugins/ResourceMap/README.md")
        z.write(os.path.join(ROOT, "LICENSE"), "BepInEx/plugins/ResourceMap/LICENSE")

    # Thunderstore: mod managers copy plugins/ into BepInEx/plugins/<Team>-ResourceMap/.
    manifest = {
        "name": "Prospector",
        "version_number": v,
        "website_url": "https://github.com/ToobsTube/VRising-ResourceMap",
        "description": "Server plugin: every ore, plant and tree on your server with per-spot spawn odds. "
                       "Chat commands to find and mark resources, Discord sharing of map dumps. "
                       "Works in single player with ServerLaunchFix.",
        "dependencies": THUNDERSTORE_DEPENDENCIES,
    }
    assert len(manifest["description"]) <= 250
    # Named Team-Package-Version like Thunderstore itself, so mod managers' "Import local mod" reads the author right.
    store = os.path.join(DIST, f"ToobsTube-Prospector-{v}.zip")
    with zipfile.ZipFile(store, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest, indent=2))
        z.write(os.path.join(ROOT, "assets", "icon.png"), "icon.png")
        z.write(os.path.join(ROOT, "README.md"), "README.md")
        z.write(os.path.join(ROOT, "CHANGELOG.md"), "CHANGELOG.md")
        z.write(DLL, "plugins/ResourceMap.dll")
        z.write(CONFIG, "plugins/Prospector.cfg.template")

    for path in (manual, store):
        print(f"{os.path.relpath(path, ROOT)}  ({os.path.getsize(path) / 1024:.0f} KB)")
        for name in zipfile.ZipFile(path).namelist():
            print("   ", name)


if __name__ == "__main__":
    main()
