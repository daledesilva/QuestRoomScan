using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Renders the GPU Surface Nets mesh via Graphics.RenderPrimitivesIndirect.
    /// Replaces per-chunk MeshFilter+MeshRenderer with a single indirect draw call.
    /// </summary>
    public class GPUMeshRenderer : MonoBehaviour
    {
        [SerializeField] private Material gpuMeshMaterial;

        private GPUSurfaceNets _surfaceNets;
        private MaterialPropertyBlock _props;
        private bool _ready;
        private Bounds _bounds;

        private static readonly int ID_SurfaceVerts = Shader.PropertyToID("_SurfaceVerts");
        private static readonly int ID_SurfaceIndices = Shader.PropertyToID("_SurfaceIndices");
        private static readonly int ID_EnvMapViewProj = Shader.PropertyToID("_RSEnvMapViewProj");

        private bool _renderVisible = true;

        /// <summary>
        /// Toggle rendering without disabling the component (which destroys state).
        /// </summary>
        public bool RenderVisible
        {
            get => _renderVisible;
            set => _renderVisible = value;
        }

        public Material GpuMeshMaterial
        {
            get => gpuMeshMaterial;
            set => gpuMeshMaterial = value;
        }

        internal void Initialize(GPUSurfaceNets surfaceNets, Bounds volumeBounds)
        {
            _surfaceNets = surfaceNets;
            _bounds = volumeBounds;
            _props = new MaterialPropertyBlock();
            _ready = true;
        }

        public void UpdateBounds(Bounds bounds)
        {
            _bounds = bounds;
        }

        /// <summary>
        /// World-space bounds used for frustum culling — the fixed TSDF volume AABB, not the
        /// live scanned geometry. Env-map capture must use MeshExtractor.RequestLiveMeshWorldBounds.
        /// </summary>
        public Bounds WorldBounds => _bounds;

        private void LateUpdate()
        {
            TryRenderIndirect();
        }

        /// <summary>
        /// Draws the GPU Surface Nets mesh when buffers and material are ready.
        /// Caller should set <see cref="RenderTexture.active"/> before calling when targeting an offscreen buffer.
        /// </summary>
        public bool TryRenderIndirect()
        {
            return TryRenderIndirect(camera: null);
        }

        /// <summary>
        /// Draws the GPU mesh using an explicit camera for view-projection (cubemap / offscreen captures).
        /// </summary>
        public bool TryRenderIndirect(Camera camera)
        {
            if (!_ready || !_renderVisible || _surfaceNets == null || gpuMeshMaterial == null)
                return false;

            var vertBuf = _surfaceNets.VertexBuffer;
            var idxBuf = _surfaceNets.IndexBuffer;
            var argsBuf = _surfaceNets.DrawIndirectArgs;

            if (vertBuf == null || idxBuf == null || argsBuf == null)
                return false;

            _props.SetBuffer(ID_SurfaceVerts, vertBuf);
            _props.SetBuffer(ID_SurfaceIndices, idxBuf);

            var rp = new RenderParams(gpuMeshMaterial)
            {
                camera = camera,
                worldBounds = _bounds,
                matProps = _props,
                receiveShadows = false,
                shadowCastingMode = ShadowCastingMode.Off,
                layer = gameObject.layer
            };

            Graphics.RenderPrimitivesIndirect(rp, MeshTopology.Triangles, argsBuf, 1);
            return true;
        }

        /// <summary>
        /// Draws the GPU mesh into an explicit color/depth target with the camera's view-projection,
        /// recorded in one command buffer so the draw cannot miss the intended render target.
        /// Offscreen env-map captures pass forceVisible so a main-view hide flag cannot skip the draw.
        /// </summary>
        public bool TryRenderIndirectToTarget(
            Camera camera,
            RenderTexture colorTarget,
            RenderTexture depthTarget,
            bool forceVisible = false)
        {
            if (!_ready || _surfaceNets == null || gpuMeshMaterial == null || camera == null)
                return false;
            if (!forceVisible && !_renderVisible)
                return false;
            if (colorTarget == null)
                return false;

            var vertBuf = _surfaceNets.VertexBuffer;
            var idxBuf = _surfaceNets.IndexBuffer;
            var argsBuf = _surfaceNets.DrawIndirectArgs;

            if (vertBuf == null || idxBuf == null || argsBuf == null)
                return false;

            _props.SetBuffer(ID_SurfaceVerts, vertBuf);
            _props.SetBuffer(ID_SurfaceIndices, idxBuf);

            // renderIntoTexture=true so GL.GetGPUProjectionMatrix matches RenderTexture Y conventions.
            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            Matrix4x4 viewProjection = gpuProjection * camera.worldToCameraMatrix;
            // Shader reads this explicitly — URP TransformWorldToHClip ignores CB SetViewProjectionMatrices.
            _props.SetMatrix(ID_EnvMapViewProj, viewProjection);
            Shader.SetGlobalMatrix(ID_EnvMapViewProj, viewProjection);

            var cmd = new CommandBuffer { name = "GPUMeshRenderer.TryRenderIndirectToTarget" };
            // Prefer an explicit depth target; otherwise use the color RT's embedded depth buffer.
            if (depthTarget != null)
                cmd.SetRenderTarget(colorTarget, depthTarget);
            else
                cmd.SetRenderTarget(colorTarget);
            cmd.ClearRenderTarget(true, true, Color.black);
            RenderingUtils.SetViewAndProjectionMatrices(cmd, camera.worldToCameraMatrix, gpuProjection, true);
            cmd.DrawProceduralIndirect(
                Matrix4x4.identity,
                gpuMeshMaterial,
                0,
                MeshTopology.Triangles,
                argsBuf,
                0,
                _props);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            return true;
        }

        /// <summary>
        /// Compact readiness bitmask for env-map capture diagnostics (bit0 ready,1 visible,2 nets,3 mat,4 verts,5 idx,6 args).
        /// </summary>
        public int GetIndirectDrawReadinessMask()
        {
            int mask = 0;
            if (_ready) mask |= 1;
            if (_renderVisible) mask |= 2;
            if (_surfaceNets != null) mask |= 4;
            if (gpuMeshMaterial != null) mask |= 8;
            if (_surfaceNets != null && _surfaceNets.VertexBuffer != null) mask |= 16;
            if (_surfaceNets != null && _surfaceNets.IndexBuffer != null) mask |= 32;
            if (_surfaceNets != null && _surfaceNets.DrawIndirectArgs != null) mask |= 64;
            return mask;
        }

        private void OnDisable()
        {
            _ready = false;
        }
    }
}
