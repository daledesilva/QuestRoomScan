The core data structures inside the compute shaders require raw textures and transformation matrices. Because graphics memory arrays and floating-point matrices are universal, we only need to change *how* we obtain these arrays and pointers.

---

## 2. Core Dependencies to Remove

Every file in the runtime directory must be stripped of references to the following namespaces:
* `using Meta.XR.Depth;`
* `using Meta.XR.Util;`
* `OVRPlugin` handles and singletons.

Any editor or manifest dependencies pointing to `com.meta.xr.sdk.core` will be entirely removed once the abstraction layer is refactored.

---

## 3. Step-by-Step Refactoring Strategy (Conceptual Level)

### Phase A: Re-Routing the Real-Time Depth Stream
`QuestRoomScan` samples per-frame depth information to fill the TSDF (Truncated Signed Distance Function) block grid. It currently requests this map via Meta's tracking managers.

* **The Current Approach:** The script hooks into a custom update loop, pulling a depth texture handle directly from the Meta depth engine.
* **The Migration Target:** Replace the Meta manager reference with Unity's `AROcclusionManager`. 
* **The Implementation Concept:** 1. Query `AROcclusionManager.TryAcquireEnvironmentDepthCpuImage(out XrCpuImage image)` during the frame update loop.
  2. Extract the raw pixel pointer or hardware texture reference natively provided by the OpenXR subsystem.
  3. Feed this neutral texture buffer directly into the existing structural extraction compute pass (`AtlasBakeCompute.compute` / TSDF shaders).

### Phase B: Standardizing Camera Tracking & View Matrices
To accurately project physical pixel information onto the newly generated meshes on the fly, the texturing pipeline needs to know exactly where the camera is and its field of view (Projection and View matrices). Currently, these coordinates are transformed relative to Meta's spatial camera rig anchors.

* **The Current Approach:** The project pulls matrix arrays from `OVRCameraRig` components or custom Meta coordinate spaces.
* **The Migration Target:** Transition entirely to Unity's standard `ARCameraManager` and `Camera.main` tracking space.
* **The Implementation Concept:**
  1. Subscribe to the `ARCameraManager.frameReceived` event delegate.
  2. In the event callback, intercept the open `ARCameraFrameEventArgs.projectionMatrix` and `ARCameraFrameEventArgs.displayMatrix`.
  3. Extract the clean $4\\times4$ transformation matrices and assign them globally to the texturing shader parameters (`Shader.SetGlobalMatrix`), swapping out the custom Meta calculation hooks.

### Phase C: Isolating Content-Delivery Components
The texturing subsystem (`KeyframeCollector.cs`) evaluates tracking telemetry to gate when a new image frame should be projected onto the voxel network to prevent texture smearing.

* **The Current Approach:** Frame gating is evaluated based on motion thresholds measured by Meta's internal pose structures.
* **The Migration Target:** Extract spatial transforms using standard Unity `Transform` objects attached to the standard `XR Origin` tracking camera.
* **The Implementation Concept:**
  1. Calculate distance delta and angular rotation delta between the current frame and the last integrated keyframe using `Vector3.Distance` and `Quaternion.Angle` on the standard main camera transform.
  2. Use these standardized float variables to feed the logic that determines if a frame is stable enough to bake into the persistent vertex texture cache.

---

## 4. Git and Commit Workflow

Because this package is maintained inside the project root under `LocalPackages/QuestRoomScan` as a Git Submodule pointing to our custom fork, code modifications can be developed and validated in real time without polluting the host game's codebase:

1. **Iterate Locally:** Write and test the AR Foundation wrappers directly inside the Unity Editor layout.
2. **Commit to Submodule Fork:** Open a terminal inside `LocalPackages/QuestRoomScan`, commit the refactored, Meta-free scripts, and push them to your custom remote repository repository (`origin`).
3. **Keep Host Lean:** The main game repository tracks only the lightweight submodule commit pointer, leaving your core asset structure decoupled and production-ready.
"""

os.makedirs("output", exist_ok=True)
with open("output/QuestRoomScan_Migration_Strategy.md", "w") as f:
    f.write(markdown_content.strip())

print("Markdown documentation generated successfully.")
