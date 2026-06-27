Export to VAM CUA — Blender bridge (minimal example)
=====================================================

A tiny convenience add-on: from Blender, send your model straight into Asset CUA
Studio with one click — skipping the "export glb → open a file browser → drag it in"
steps. The actual conversion and tuning (orientation, materials, collider, vertex
colors, animation, texture slimming…) still happen in the app, where you can preview
and adjust before baking the VAM CUA.

This is a bonus / starting point shipped with the app — it is intentionally minimal,
Windows-only, and leans on the app that comes in this zip. Feel free to read and tweak
it (it's a single short .py file).

INSTALL
-------
1. Blender:  Edit > Preferences > Add-ons > Install…  →  pick  vam_cua_export.py  →  enable it.
2. Expand the add-on entry and set "Asset CUA Studio.exe" to the AssetCuaStudio.exe
   that came in this zip.

USE
---
- Select your model in Blender.
- File > Export > "VAM CUA (.glb → Asset CUA Studio)".
- Asset CUA Studio opens with your model loaded. Tune it, then Convert to CUA.

NOTES
-----
- Windows only (the converter engine is a Windows build).
- "Selection only" (on by default) exports just the selected objects; turn it off to
  export the whole scene.
- Modifiers are applied on export for a clean result.
- Tested baseline: Blender 4.x (works on 3.6 LTS+; uses only stable glTF export options).

Two-way of working — both are fine, pick what fits your habit:
  • This add-on:  stay in Blender, one click, app opens with the model.
  • Or directly:  export a .glb yourself and drag it onto Asset CUA Studio.
