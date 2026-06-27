# CUA Asset Studio

Convert **glTF / GLB** models into **Virt-A-Mate** `CustomUnityAsset` (`.assetbundle`) packages — with a live 3D preview, a transform gizmo, per-material tuning, animation baking, and batch conversion.

CUA Asset Studio is the desktop front-end of the [Shadow Venom](https://r18sv.github.io/) **CRAFT** tool line (*Creators Reclaiming Art From Tech*). It wraps the offline `glb → CUA` baking pipeline in a friendly WPF app so you can drop in a model, dial it in against a human reference, and ship a VAM-ready asset without ever opening Unity.

> **Status:** open-source GUI. The actual mesh/texture/animation baking is performed by the separate, also-open-source **conversion engine** [`glb2cua`](https://github.com/R18SV/glb2cua), which you point the app at in `config.json` — see [Configuration](#configuration).

---

## Features

- **Live three.js preview** — drop or select a `.glb` and see it immediately, framed against a 1:1 human reference for scale.
- **Transform gizmo** — move, rotate (yaw), and **uniformly scale** the model right in the viewport. Drag handles write straight back to the panel and the bake.
  - `W` — Move (X / Y / Z)
  - `E` — Rotate (yaw)
  - `R` — Scale (uniform → drives target height)
  - Or use the on-canvas **Move / Rotate / Scale** toolbar (top-right).
- **Per-material editor** — color, metallic, smoothness, emission, normal scale, two-sided, blend/cutout/opaque mode, all previewed live and baked into the asset.
- **Animation baking** — auto-plays clips, lets you pick the default clip, supports ping-pong (seamless) loops; all clips are baked.
- **Texture slimming** — optionally downscale normal / spec / albedo maps at bake time to keep package size down.
- **Colliders** — none / box / convex / mesh, with a perf warning when a mesh collider would be too heavy for VAM physics.
- **Batch conversion** — convert many models at once, or pack several models into one switchable CUA.
- **Multi-language UI** — English, 한국어, 日本語, 繁體中文 (live switch, no restart).
- **Blender bridge** — an optional Blender add-on (`blender/`) exports directly to the app's drop folder.

---

## Requirements

- **Windows 10/11**
- **[.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)** (only if you run the framework-dependent build; the release ZIP is self-contained)
- **[Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)** (pre-installed on current Windows)
- The **[`glb2cua`](https://github.com/R18SV/glb2cua) conversion engine** (Python script or frozen `.exe`) for actually baking assets — preview, gizmo, and material editing all work without it.

---

## Install

### Release (recommended)
Grab the latest self-contained ZIP from [Releases](../../releases), unzip anywhere, and run `AssetCuaStudio.exe`. No .NET install required.

### Build from source
```sh
git clone https://github.com/R18SV/cua-asset-studio.git
cd cua-asset-studio
dotnet build -c Release
# or a single-file, self-contained exe:
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

---

## Configuration

On first run the app copies `config.example.json` next to the executable as a template. Create your own `config.json` (same folder as the exe) to enable baking:

```json
{
  "python": "python",
  "glb2cuaScript": "C:\\path\\to\\glb2cua\\glb2cua_mold.py",
  "engineExe": "",
  "glb2cuaMold": "C:\\path\\to\\Mold.assetbundle",
  "outDir": "C:\\path\\to\\VAM\\Custom\\Assets",
  "personRef": "renlexi.obj"
}
```

| Key | Meaning |
| --- | --- |
| `python` | Python launcher used to run the engine script (`python`, `py`, or a full path). |
| `glb2cuaScript` | Path to the [`glb2cua`](https://github.com/R18SV/glb2cua) Python entry point (`glb2cua_mold.py`). |
| `engineExe` | Optional: path to a frozen engine `.exe` (used instead of Python when set). |
| `glb2cuaMold` | The Unity "Mold" asset bundle the engine bakes into. |
| `outDir` | Default output folder for finished `.assetbundle` packages. |
| `personRef` | OBJ used as the 1:1 scale reference in the preview (ships with the app). |

`config.json` is git-ignored so your machine paths never get committed.

---

## How the preview maps to the bake

The viewport is a faithful preview of what the engine bakes:

- **Move** → `POS_X / POS_Y / POS_Z` offsets (X mirrored to match VAM's convention).
- **Rotate** → `ORIENT_YAW` (degrees).
- **Scale** → uniform; sets `NORMALIZE=1` and `TARGET_HEIGHT` (the model is scaled so its height matches the target). Non-uniform scaling is intentionally not supported — VAM CUAs carry a single transform and non-uniform scale would have to be baked destructively into skinned meshes.

---

## Project layout

```
AssetCuaStudio.csproj      WPF app (.NET 9, WebView2)
MainWindow.xaml(.cs)       Main UI + host logic, engine invocation, message bridge
L.cs                       Lightweight i18n (en / ko / ja / zh-Hant)
config.example.json        Config template
web/
  preview.html             three.js preview + transform gizmo
  vendor/                   three.js, GLTFLoader, OBJLoader, OrbitControls, TransformControls
  renlexi.obj              human scale reference
blender/
  vam_cua_export.py        optional Blender export bridge
```

---

## Third-party

- [three.js](https://threejs.org/) — MIT
- [WPF-UI](https://github.com/lepoco/wpfui) — MIT
- [Microsoft.Web.WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) — see Microsoft's license

## License

[MIT](LICENSE) © 2026 Shadow Venom (R18SV)

Virt-A-Mate is a trademark of its respective owner. This project is an independent community tool and is not affiliated with or endorsed by Virt-A-Mate.
