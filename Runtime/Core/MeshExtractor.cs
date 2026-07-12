using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Orchestrates GPU Surface Nets mesh extraction from the TSDF volume.
    /// Dispatches GPUSurfaceNets compute shaders and manages the GPUMeshRenderer.
    /// </summary>
    public class MeshExtractor : MonoBehaviour
    {
        public static MeshExtractor Instance { get; private set; }

        [Header("Mesh Smoothing")]
        [SerializeField, Tooltip("Post-extraction vertex smoothing iterations. 0 = disabled.")]
        [Range(0, 8)] private int meshSmoothIterations = 1;
        [SerializeField, Tooltip("Laplacian blend strength per iteration.")]
        [Range(0.1f, 1f)] private float meshSmoothLambda = 0.33f;
        [SerializeField, Tooltip("HC back-projection strength to prevent volume shrinkage.")]
        [Range(0f, 1f)] private float meshSmoothBeta = 0.5f;

        [Header("Temporal Stability")]
        [SerializeField, Tooltip("Alpha for large displacements (fast convergence).")]
        [Range(0.1f, 1f)] private float temporalAlphaMax = 0.85f;
        [SerializeField, Tooltip("Alpha for long-stable vertices (strong resistance to change).")]
        [Range(0.01f, 0.5f)] private float temporalAlphaMin = 0.1f;
        [SerializeField, Tooltip("How quickly alpha decays from max to min as vertex stabilizes.")]
        [Range(0.01f, 1f)] private float temporalDecayRate = 0.15f;
        [SerializeField, Tooltip("Displacement threshold (meters) to consider a vertex still converging.")]
        [Range(0.001f, 0.02f)] private float convergenceThreshold = 0.005f;
        [SerializeField, Tooltip("Position changes below this (meters) are suppressed entirely.")]
        [Range(0f, 0.01f)] private float temporalDeadzone = 0.001f;

        [Header("Rendering")]
        [SerializeField] private Material scanMeshMaterial;

        [Header("Compute")]
        [SerializeField] public ComputeShader surfaceNetsCompute;
        [SerializeField, Tooltip("Max vertex fraction of total voxels (0.01-0.10).")]
        [Range(0.01f, 0.10f)] private float gpuVertexBudgetPercent = 0.08f;

        private GPUSurfaceNets _gpuSurfaceNets;
        private GPUMeshRenderer _gpuRenderer;
        private int _extractCount;

        internal GPUSurfaceNets GpuSurfaceNets => _gpuSurfaceNets;
        public bool IsInitialized => _gpuSurfaceNets != null;

        // Env-map / gameplay callers need the AABB of actual surface verts, not the fixed TSDF volume cube.
        private const int GpuVertexStrideBytes = 32;
        private const int LiveBoundsMaxSamples = 8192;
        private bool _liveBoundsReadbackInProgress;

        /// <summary>Current GPU mesh vertex count (updated after each extraction via async readback).</summary>
        public int LastVertexCount { get; private set; }
        /// <summary>Current GPU mesh index count (updated after each extraction via async readback).</summary>
        public int LastIndexCount { get; private set; }

        /// <summary>
        /// Reduces mesh extraction smoothing and vertex budget before Surface Nets buffers allocate.
        /// </summary>
        public void ApplyGameplayPerformancePreset()
        {
            if (_gpuSurfaceNets != null) return;

            gpuVertexBudgetPercent = 0.05f;
            meshSmoothIterations = 0;
        }

        private VolumeIntegrator _volume;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            _volume = VolumeIntegrator.Instance;
            if (_volume == null)
                throw new Exception("[RoomScan] VolumeIntegrator not found");

            if (surfaceNetsCompute == null)
                throw new Exception("[RoomScan] surfaceNetsCompute not assigned on MeshExtractor");

            // GPU Surface Nets buffers (~480 MB at the default 256³ voxel grid)
            // are allocated lazily — first scan via RoomScanner.StartScanning,
            // or full-load via RoomScanPersistence.LoadPackageAsync. Pure
            // refined-mesh-only replay paths never trigger this. Keep the
            // pipeline log here so device traces still show RP/stereo/material
            // state at scene load, but defer the heavy Init().
            var rpAsset = GraphicsSettings.currentRenderPipeline;
            Logger.Info($"MeshExtractor Start (Surface Nets buffers deferred): " +
                $"mat={scanMeshMaterial?.name ?? "NULL"}, " +
                $"shader={scanMeshMaterial?.shader?.name ?? "NULL"}, " +
                $"rp={rpAsset?.name ?? "NULL"}, " +
                $"stereoMode={UnityEngine.XR.XRSettings.stereoRenderingMode}");
        }

        /// <summary>
        /// Lazy initializer. Brings up GPU Surface Nets buffers + the renderer
        /// component if they aren't already up. Idempotent. Existing callers
        /// (<see cref="Reinitialize"/>, <see cref="RoomScanner"/>'s update loop
        /// guard, the Start of every scan) all funnel through here so the
        /// allocation cost only appears when a scan or full reload actually
        /// needs it.
        /// </summary>
        public void EnsureInitialized()
        {
            if (_gpuSurfaceNets != null) return;
            Init();
        }

        private void OnDestroy()
        {
            _gpuSurfaceNets?.Dispose();
            _gpuSurfaceNets = null;
        }

        private void Init()
        {
            _gpuSurfaceNets = new GPUSurfaceNets(surfaceNetsCompute)
            {
                MinMeshWeight = _volume.MinMeshWeight,
                SmoothIterations = meshSmoothIterations,
                SmoothLambda = meshSmoothLambda,
                SmoothBeta = meshSmoothBeta,
                TemporalAlphaMax = temporalAlphaMax,
                TemporalAlphaMin = temporalAlphaMin,
                TemporalDecayRate = temporalDecayRate,
                ConvergenceThreshold = convergenceThreshold,
                TemporalDeadzone = temporalDeadzone
            };

            _gpuSurfaceNets.EnsureBuffers(_volume.VoxelCount, gpuVertexBudgetPercent);

            _gpuRenderer = gameObject.AddComponent<GPUMeshRenderer>();
            _gpuRenderer.GpuMeshMaterial = scanMeshMaterial;
            _gpuRenderer.Initialize(_gpuSurfaceNets, _gpuSurfaceNets.GetVolumeBounds(_volume.VoxelSize));

            Logger.Info($"GPU Surface Nets initialized lazily: voxels={_volume.VoxelCount}, " +
                      $"voxSize={_volume.VoxelSize}");
        }

        /// <summary>
        /// Run one GPU mesh extraction pass from the current TSDF volume state.
        /// Called by RoomScanner at the configured mesh extraction rate.
        /// </summary>
        public void Extract()
        {
            if (_gpuSurfaceNets == null) return;

            _extractCount++;
            _gpuSurfaceNets.MinMeshWeight = _volume.MinMeshWeight;

            _gpuSurfaceNets.Extract(_volume.Volume, _volume.ColorVolume, _volume.VoxelSize);

            // Keep volume AABB for frustum culling — live geometry can grow inside it.
            if (_gpuRenderer != null)
                _gpuRenderer.UpdateBounds(_gpuSurfaceNets.GetVolumeBounds(_volume.VoxelSize));

            var counters = _gpuSurfaceNets.CountersBuffer;
            if (counters != null)
            {
                AsyncGPUReadback.Request(counters, (req) =>
                {
                    if (req.hasError) return;
                    var data = req.GetData<uint>();
                    if (data.Length >= 2)
                    {
                        LastVertexCount = (int)data[0];
                        LastIndexCount = (int)data[1];
                    }
                });
            }
        }

        /// <summary>
        /// Async AABB of current Surface Nets vertices (sparse sample). Use for env-map capture
        /// origin as the scanned room grows — <see cref="GPUMeshRenderer.WorldBounds"/> stays
        /// the fixed TSDF volume and does not track live geometry.
        /// Always invokes <paramref name="onComplete"/> once with success=false on failure so
        /// callers can clear pending flags.
        /// </summary>
        public void RequestLiveMeshWorldBounds(Action<bool, Bounds> onComplete, int maxSamples = LiveBoundsMaxSamples)
        {
            if (onComplete == null)
                return;
            // Caller already has a request in flight — do not invoke onComplete (would clear their pending flag).
            if (_liveBoundsReadbackInProgress)
                return;
            if (_gpuSurfaceNets == null || _gpuSurfaceNets.VertexBuffer == null || _gpuSurfaceNets.CountersBuffer == null)
            {
                onComplete(false, default);
                return;
            }

            _liveBoundsReadbackInProgress = true;
            int sampleCap = Mathf.Max(64, maxSamples);

            AsyncGPUReadback.Request(_gpuSurfaceNets.CountersBuffer, countersRequest =>
            {
                if (countersRequest.hasError)
                {
                    _liveBoundsReadbackInProgress = false;
                    onComplete(false, default);
                    return;
                }

                var counterData = countersRequest.GetData<uint>();
                int vertexCount = counterData.Length > 0 ? (int)counterData[0] : 0;
                if (vertexCount <= 0)
                {
                    _liveBoundsReadbackInProgress = false;
                    onComplete(false, default);
                    return;
                }

                int readBytes = vertexCount * GpuVertexStrideBytes;
                AsyncGPUReadback.Request(_gpuSurfaceNets.VertexBuffer, readBytes, 0, vertexRequest =>
                {
                    try
                    {
                        if (vertexRequest.hasError)
                        {
                            onComplete(false, default);
                            return;
                        }

                        var vertexFloats = vertexRequest.GetData<float>();
                        int floatsPerVertex = GpuVertexStrideBytes / sizeof(float);
                        int safeVertexCount = Math.Min(vertexCount, vertexFloats.Length / floatsPerVertex);
                        if (safeVertexCount <= 0)
                        {
                            onComplete(false, default);
                            return;
                        }

                        int sampleStride = Math.Max(1, safeVertexCount / sampleCap);
                        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                        int used = 0;
                        for (int vertexIndex = 0; vertexIndex < safeVertexCount; vertexIndex += sampleStride)
                        {
                            int floatIndex = vertexIndex * floatsPerVertex;
                            Vector3 position = new Vector3(
                                vertexFloats[floatIndex],
                                vertexFloats[floatIndex + 1],
                                vertexFloats[floatIndex + 2]);
                            if (!IsFinite(position))
                                continue;

                            min = Vector3.Min(min, position);
                            max = Vector3.Max(max, position);
                            used++;
                        }

                        if (used <= 0)
                        {
                            onComplete(false, default);
                            return;
                        }

                        Vector3 size = max - min;
                        // Reject empty / degenerate samples (e.g. single-point early mesh).
                        if (size.sqrMagnitude < 1e-6f)
                        {
                            onComplete(false, default);
                            return;
                        }

                        onComplete(true, new Bounds((min + max) * 0.5f, size));
                    }
                    finally
                    {
                        _liveBoundsReadbackInProgress = false;
                    }
                });
            });
        }

        private static bool IsFinite(Vector3 value)
        {
            return !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z)
                || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
        }

        /// <summary>
        /// Release GPU resources without re-creating them.
        /// Used by ClearAllData to avoid a heavy re-alloc while the GPU may
        /// still be referencing the old buffers from the previous frame's draw.
        /// Call <see cref="Reinitialize"/> when resources are needed again.
        /// </summary>
        public void DisposeOnly()
        {
            if (_gpuRenderer != null)
            {
                _gpuRenderer.RenderVisible = false;
                Destroy(_gpuRenderer);
                _gpuRenderer = null;
            }
            _gpuSurfaceNets?.Dispose();
            _gpuSurfaceNets = null;
        }

        /// <summary>
        /// Dispose GPU resources and reinitialize. Used after loading a saved scan.
        /// </summary>
        public void Reinitialize()
        {
            if (_gpuRenderer != null)
            {
                _gpuRenderer.RenderVisible = false;
                Destroy(_gpuRenderer);
                _gpuRenderer = null;
            }
            _gpuSurfaceNets?.Dispose();
            _gpuSurfaceNets = null;
            Init();
        }
    }
}
