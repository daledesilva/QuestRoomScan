# QuestRoomScan

Real-time 3D room reconstruction on Meta Quest 3. Produces a textured mesh from depth + RGB camera data using GPU TSDF volume integration and Surface Nets mesh extraction, with server-based Gaussian Splat training and on-device rendering via [Unity Gaussian Splatting](https://github.com/arghyasur1991/UnityGaussianSplatting).

## Features

- **GPU TSDF Integration** — Depth frames fused into a signed distance field via compute shaders
- **GPU Surface Nets Meshing** — Fully GPU-driven mesh extraction via compute shaders with zero CPU readback, rendered via a single `Graphics.RenderPrimitivesIndirect` draw call
- **Three-Layer Texturing** — Triplanar world-space cache (persistent ~8mm/texel) → vertex colors (~5cm fallback) — all sourced from passthrough camera RGB. Keyframes captured as motion-gated JPEGs to disk for Gaussian Splat training.
- **Mesh Persistence** — Save/load full scan state (TSDF + color volumes + triplanar textures) to disk, auto-save on quit
- **Temporal Stabilization** — Adaptive per-vertex temporal blending on GPU prevents mesh jitter while allowing fast convergence
- **Exclusion Zones** — Cylindrical rejection around tracked heads prevents body reconstruction (configurable radius and height, up to 64 zones)
- **Gaussian Splat Training & Rendering** — Keyframe capture + point cloud export → PC server training → trained PLY download → on-device UGS rendering
- **VR Debug Menu** — World-space UI Toolkit panel with controller ray interaction, lazy-follow gaze tracking, live status, server training controls, persistence management, and FPS display
- **Render Mode Switching** — Cycle between Mesh, Splat, and Both views at runtime via debug menu or controller binding (default: A/X button)

## Requirements

- **Unity 6** (6000.x)
- **URP** (Universal Render Pipeline)
- **Meta Quest 3** (depth sensor required)

### Dependencies

| Package | Version | Notes |
|---------|---------|-------|
| `com.unity.xr.arfoundation` | 6.1+ | Depth frame access |
| `com.unity.render-pipelines.universal` | 17.0+ | URP rendering pipeline |
| `com.meta.xr.mrutilitykit` | 85+ | Passthrough camera RGB access |
| `com.unity.burst` | 1.8+ | Required by Collections/Mathematics |
| `com.unity.collections` | 2.4+ | NativeArray for plane detection |
| `com.unity.mathematics` | 1.3+ | Math types used throughout |
| `org.nesnausk.gaussian-splatting` | [fork](https://github.com/arghyasur1991/UnityGaussianSplatting) | Gaussian splat rendering with runtime PLY loading |

Additional project-level dependencies (not in `package.json` — installed via Meta's SDK or XR plugin management):
- `com.unity.xr.meta-openxr` (bridges Meta depth to AR Foundation)
- `com.unity.xr.openxr` (OpenXR runtime)
- `com.meta.xr.sdk.core` (OVRInput, OVRCameraRig)

### Android Permissions

- `com.oculus.permission.USE_SCENE` (depth API / spatial data)
- `horizonos.permission.HEADSET_CAMERA` (passthrough camera RGB access)

## Installation

Add to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.genesis.roomscan": "https://github.com/arghyasur1991/QuestRoomScan.git",
    "org.nesnausk.gaussian-splatting": "https://github.com/arghyasur1991/UnityGaussianSplatting.git?path=package#main"
  }
}
```

Or clone locally and reference as local packages:

```json
{
  "dependencies": {
    "com.genesis.roomscan": "file:../QuestRoomScan",
    "org.nesnausk.gaussian-splatting": "file:/path/to/UnityGaussianSplatting/package"
  }
}
```

## Quick Start

1. Open the setup wizard: **RoomScan > Setup Scene**
2. The wizard checks prerequisites (AR Session, OVRCameraRig/XROrigin, AROcclusionManager), configures project settings (boundaryless manifest, cleartext HTTP for LAN server), and adds all required components — including `GaussianSplatRenderer` with UGS shaders, the URP render feature, VR input handlers, and debug menu
3. Build and deploy to Quest 3
4. The room mesh appears as you look around — surfaces solidify with repeated observations

### Architecture

```
PassthroughCameraProvider (RGB frames from headset cameras)
       │
DepthCapture (AROcclusionManager → depth → normals → dilation)
       │
VolumeIntegrator (TSDF + color integration, exclusion zones, prune, freeze)
       │
MeshExtractor → GPUSurfaceNets (compute: classify → smooth → snap → temporal → index)
       │         └── GPUMeshRenderer (Graphics.RenderPrimitivesIndirect, single draw call)
       │
       ├── TriplanarCache (bake camera RGB → 3 world-space textures)
       │
       ├── KeyframeCollector (motion-gated JPEG + poses → GSExport/ on disk)
       │
       ├── PointCloudExporter (GPU mesh → points3d.ply via AsyncGPUReadback)
       │
       └── GSplatServerClient (ZIP upload → poll status → PLY download)
                   │
                   GSplatManager + GaussianSplatRenderer (UGS)
                   │
                   On-device Gaussian Splat rendering
```

See [ALGORITHM.md](ALGORITHM.md) for the full technical reference.

## Gaussian Splat Pipeline

QuestRoomScan captures keyframes and a dense point cloud during scanning, uploads them to a PC training server, and renders the trained Gaussian splats on-device.

### On-Device (automatic capture)

- **KeyframeCollector**: Motion-gated JPEG frames + camera poses saved to `GSExport/` on disk (`images/*.jpg`, `frames.jsonl`)
- **PointCloudExporter**: GPU mesh vertices exported as binary PLY (`points3d.ply`) via `AsyncGPUReadback`

### Server Training (via [RoomScan-GaussianSplatServer](https://github.com/arghyasur1991/RoomScan-GaussianSplatServer))

The companion PC server handles the full training pipeline:

```bash
python main.py --port 8420  # API server
npm run dev                  # Dashboard at http://localhost:5173
```

The flow:
1. **Export**: Point cloud exported from GPU mesh; keyframes already on disk from scanning
2. **Upload**: Quest ZIPs `GSExport/` contents (`frames.jsonl`, `points3d.ply`, `images/*.jpg`) and POSTs to `{serverUrl}/upload?iterations={N}`
3. **Train**: Server converts Unity poses + intrinsics to COLMAP binary format, trains via msplat/gsplat/3DGS
4. **Poll**: Quest polls `{serverUrl}/api/status` every ~3s — debug menu shows state, progress, iteration, elapsed, backend
5. **Download**: Quest GETs `{serverUrl}/download` → trained PLY bytes
6. **Render**: `GSplatManager` loads PLY via `GaussianSplatPlyLoader.LoadFromPlyBytes()` → `GaussianSplatRenderer` renders on-device

### On-Device Rendering (UGS)

Trained splats are rendered using a [fork of Unity Gaussian Splatting](https://github.com/arghyasur1991/UnityGaussianSplatting) with runtime PLY loading and Quest 3 optimizations:

- **`GaussianSplatPlyLoader`**: Parses binary PLY → converts to UGS internal format → creates GPU buffers directly (no Editor asset pipeline needed)
- **Coordinate conversion**: COLMAP (right-handed Y-down) → Unity (left-handed Y-up)
- **Quest 3 stereo**: Per-eye VP matrices for correct VR covariance projection, shared compute between eyes
- **Performance**: Reduced-resolution rendering (0.5x), optimized compute shaders, partial radix sort, contribution-based culling
- **Render mode switching**: Mesh, Splat, or Both — cycled via debug menu or controller binding without releasing GPU resources

### Supported Training Backends

| Backend | Platform | Install |
|---------|----------|---------|
| [msplat](https://github.com/nicknish/msplat) | Apple Silicon (Metal) | `pip install "msplat[cli]"` |
| [gsplat](https://github.com/nerfstudio-project/gsplat) | NVIDIA GPU (CUDA) | `pip install gsplat` |
| [3DGS](https://github.com/graphdeco-inria/gaussian-splatting) | NVIDIA GPU (CUDA) | Clone repo, pass `--gs-repo` |

## VR Debug Menu

World-space UI Toolkit panel activated via **left thumbstick click** (Quest OS reserves the Menu/Start button for system use). Uses controller ray interaction with trigger to click.

**Lazy-follow**: Panel floats at 0.75m, re-centers when gaze drifts past 45 degrees.

| Section | Contents |
|---------|----------|
| **Scan Status** | Scanning state, render mode, integration count, keyframe count, render info |
| **Server Training** | Editable server URL, training state, progress bar, iteration count, elapsed time, backend name, status message |
| **Persistence** | Saved scan info, GSExport directory status |
| **Actions** | Toggle Scan, Cycle Render Mode, Save Scan, Load Scan, Export Point Cloud, Start GS Training, Cancel Training, Clear All Data |
| **Footer** | Live FPS counter |

### Default Controller Bindings

| Button | Action |
|--------|--------|
| Left Thumbstick Click | Toggle Debug Menu |
| One (Y/B) | Freeze In View |
| Two (X/A) | Unfreeze In View |
| Three (A/X) | Cycle Render Mode |
| Four (B/Y) | Start Server Training (disabled by default) |

Bindings are fully configurable via `RoomScanInputHandler` — add, remove, or remap any `ScanAction` to any `OVRInput.Button`.

## Memory Budget (Quest 3)

Default values — all configurable per-component in the Inspector.

| Component | Default | Memory |
|-----------|---------|--------|
| TSDF volume (RG8_SNorm) | 256 x 256 x 256 | ~32 MB |
| Color volume (RGBA8) | 256 x 256 x 256 | ~64 MB |
| GPU Surface Nets (coord map, vertices, indices, smoothing, temporal 3D texture) | 256³ derived | ~83 MB |
| Triplanar textures (3x RGBA8) | 3 x 4096 x 4096 | ~192 MB |
| **Total GPU** | | **~371 MB** |

Keyframes are written as JPEGs to disk (not held in GPU memory). To reduce GPU memory on constrained devices, lower `VolumeIntegrator.voxelCount` and `TriplanarCache.textureResolution` in the Inspector.

## Comparison with Hyperscape

[Meta Horizon Hyperscape](https://www.meta.com/help/quest/1088536553019177/) is Meta's first-party room scanning app for Quest 3. It produces stunning photorealistic Gaussian Splat captures — significantly higher visual quality than what QuestRoomScan currently achieves. If your goal is purely the best-looking scan, Hyperscape is the better choice today.

QuestRoomScan exists for a different reason: it's **open source, fully on-device, and gives you complete control over the pipeline**.

| | Hyperscape | QuestRoomScan |
|-|------------|---------------|
| **Processing** | Cloud (1-8 hours after capture) | Real-time textured mesh on-device, GS training on local PC |
| **Output quality** | Photorealistic Gaussian Splats | Textured mesh (real-time) + on-device GS rendering via UGS |
| **Data access** | No raw file export | Full export: PLY point cloud, JPEG keyframes, camera poses |
| **Extensibility** | Closed, no API | MIT open source, every parameter exposed |
| **GS training** | Handled by Meta's cloud | Your hardware, your choice of backend (msplat/gsplat/3DGS) |
| **Offline use** | Requires upload + cloud processing | Works entirely offline (except GS training on PC) |
| **Integration** | Standalone app | Unity package — embed scanning in your own app |

QuestRoomScan is best suited for developers who need to integrate room scanning into their own applications, want full control over the reconstruction pipeline, or need to work with the raw scan data directly.

## Credits & Prior Art

The TSDF volume integration and Surface Nets meshing approach draws inspiration from [anaglyphs/lasertag](https://github.com/anaglyphs/lasertag) by Julian Triveri & Hazel Roeder (MIT), which demonstrated real-time room reconstruction on Quest 3 inside a mixed reality game.

QuestRoomScan builds on that foundation with significant extensions:

| | lasertag | QuestRoomScan |
|-|----------|---------------|
| **Mesh extraction** | CPU marching cubes from GPU volume | Fully GPU-driven Surface Nets via compute shaders — zero CPU readback, single indirect draw call |
| **Texturing** | Geometry only — no camera RGB texturing | Camera-based texturing: triplanar world-space cache (~8mm/texel) and vertex colors (~5cm) — sourced from passthrough camera RGB |
| **Persistence** | None — mesh lost on restart | Save/load of TSDF + color volumes + triplanar textures to disk, auto-save on quit |
| **Mesh quality** | Basic TSDF blending | Quality² modulation, confidence-gated Surface Nets, warmup clearing, pruning, body exclusion zones, GPU temporal stabilization, RANSAC plane detection & snapping |
| **Gaussian Splatting** | — | Full pipeline: on-device capture → PC server training → on-device UGS rendering with render mode switching |
| **VR UI** | — | World-space debug menu with controller ray interaction, live status, and training controls |
| **Packaging** | Embedded in a game | Standalone Unity package with one-click editor setup wizard |

## License

[MIT](LICENSE.md) — see [LICENSE.md](LICENSE.md) for full text and attribution.
