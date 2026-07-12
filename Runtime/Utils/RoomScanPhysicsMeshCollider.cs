using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Keeps a static MeshCollider in sync with the live GPU scan mesh (or refined mesh when available)
    /// so gameplay objects can bounce off the reconstructed room surface.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomScanPhysicsMeshCollider : MonoBehaviour
    {
        private const int GpuVertexStride = 32;
        private const int SyncEveryMeshExtractions = 20;

        private RoomScanner _roomScanner;
        private MeshCollider _meshCollider;
        private Mesh _collisionMesh;
        private int _extractionsSinceSync;
        private bool _syncInProgress;

        private void Awake()
        {
            // Visual-only Live HDR scenes must never build a room MeshCollider — even an
            // empty host that later fills will eject the XR rig and can drop FPS near zero.
            if (IsLiveHdrTestingScene())
            {
                enabled = false;
                return;
            }

            _roomScanner = GetComponent<RoomScanner>() ?? RoomScanner.Instance;
            EnsureColliderObjects();
        }

        private static bool IsLiveHdrTestingScene()
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            return sceneName == "LiveHdrTesting" || sceneName == "LiveHdrTestingTriplanar";
        }

        private void OnEnable()
        {
            if (_roomScanner == null) return;

            _roomScanner.MeshExtracted += OnMeshExtracted;
            _roomScanner.RefinedMeshReady += OnRefinedMeshReady;
        }

        private void OnDisable()
        {
            if (_roomScanner == null) return;

            _roomScanner.MeshExtracted -= OnMeshExtracted;
            _roomScanner.RefinedMeshReady -= OnRefinedMeshReady;
        }

        private void OnDestroy()
        {
            if (_collisionMesh != null)
                Destroy(_collisionMesh);
        }

        private void OnRefinedMeshReady(Mesh mesh, Texture2D atlas)
        {
            if (mesh == null) return;
            ApplyMeshToCollider(mesh);
        }

        private void OnMeshExtracted()
        {
            _extractionsSinceSync++;
            if (_syncInProgress || _extractionsSinceSync < SyncEveryMeshExtractions)
                return;

            _extractionsSinceSync = 0;
            RequestLiveMeshSync();
        }

        private void EnsureColliderObjects()
        {
            if (_collisionMesh == null)
            {
                _collisionMesh = new Mesh
                {
                    name = "RoomScanCollisionMesh",
                    indexFormat = IndexFormat.UInt32,
                };
            }

            if (_meshCollider == null)
            {
                var colliderObject = new GameObject("RoomScanPhysicsCollider");
                colliderObject.transform.SetParent(transform, false);
                _meshCollider = colliderObject.AddComponent<MeshCollider>();
                _meshCollider.convex = false;
            }
        }

        /// <summary>
        /// Removes the live MeshCollider host. Live HDR / visual-only scenes call this so an
        /// empty-then-filled room collider cannot eject the XR rig or stall physics.
        /// </summary>
        public void DestroyCollisionHost()
        {
            enabled = false;

            if (_meshCollider != null)
            {
                _meshCollider.sharedMesh = null;
                Destroy(_meshCollider.gameObject);
                _meshCollider = null;
            }

            Transform colliderHost = transform.Find("RoomScanPhysicsCollider");
            if (colliderHost != null)
                Destroy(colliderHost.gameObject);

            if (_collisionMesh != null)
            {
                Destroy(_collisionMesh);
                _collisionMesh = null;
            }
        }

        private void RequestLiveMeshSync()
        {
            GPUSurfaceNets gpuSurfaceNets = MeshExtractor.Instance?.GpuSurfaceNets;
            if (gpuSurfaceNets == null || gpuSurfaceNets.VertexBuffer == null || gpuSurfaceNets.IndexBuffer == null)
                return;

            _syncInProgress = true;

            AsyncGPUReadback.Request(gpuSurfaceNets.CountersBuffer, countersRequest =>
            {
                if (countersRequest.hasError)
                {
                    _syncInProgress = false;
                    return;
                }

                var counterData = countersRequest.GetData<uint>();
                int vertexCount = counterData.Length > 0 ? (int)counterData[0] : 0;
                int indexCount = counterData.Length > 1 ? (int)counterData[1] : 0;
                if (vertexCount <= 0 || indexCount <= 0)
                {
                    _syncInProgress = false;
                    return;
                }

                AsyncGPUReadback.Request(gpuSurfaceNets.VertexBuffer, vertexRequest =>
                {
                    if (vertexRequest.hasError)
                    {
                        _syncInProgress = false;
                        return;
                    }

                    AsyncGPUReadback.Request(gpuSurfaceNets.IndexBuffer, indexRequest =>
                    {
                        if (indexRequest.hasError)
                        {
                            _syncInProgress = false;
                            return;
                        }

                        try
                        {
                            BuildMeshFromGpuReadback(vertexRequest, indexRequest, vertexCount, indexCount);
                        }
                        catch (Exception exception)
                        {
                            Logger.Warning($"RoomScanPhysicsMeshCollider: mesh sync failed: {exception.Message}");
                        }
                        finally
                        {
                            _syncInProgress = false;
                        }
                    });
                });
            });
        }

        private void BuildMeshFromGpuReadback(
            AsyncGPUReadbackRequest vertexRequest,
            AsyncGPUReadbackRequest indexRequest,
            int vertexCount,
            int indexCount)
        {
            var vertexFloats = vertexRequest.GetData<float>();
            var indexData = indexRequest.GetData<uint>();

            int floatsPerVertex = GpuVertexStride / sizeof(float);
            int safeVertexCount = Math.Min(vertexCount, vertexFloats.Length / floatsPerVertex);
            int safeIndexCount = Math.Min(indexCount, indexData.Length);
            if (safeVertexCount <= 0 || safeIndexCount < 3)
                return;

            var vertices = new Vector3[safeVertexCount];
            for (int vertexIndex = 0; vertexIndex < safeVertexCount; vertexIndex++)
            {
                int floatIndex = vertexIndex * floatsPerVertex;
                vertices[vertexIndex] = new Vector3(
                    vertexFloats[floatIndex],
                    vertexFloats[floatIndex + 1],
                    vertexFloats[floatIndex + 2]);
            }

            var triangles = new int[safeIndexCount];
            for (int index = 0; index < safeIndexCount; index++)
                triangles[index] = (int)indexData[index];

            _collisionMesh.Clear();
            _collisionMesh.SetVertices(vertices);
            _collisionMesh.SetTriangles(triangles, 0);
            _collisionMesh.RecalculateBounds();

            _meshCollider.sharedMesh = null;
            _meshCollider.sharedMesh = _collisionMesh;
        }

        private void ApplyMeshToCollider(Mesh mesh)
        {
            if (mesh == null || _meshCollider == null) return;

            _meshCollider.sharedMesh = null;
            _meshCollider.sharedMesh = mesh;
        }
    }
}
