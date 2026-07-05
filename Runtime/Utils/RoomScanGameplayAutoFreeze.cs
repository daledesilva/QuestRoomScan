using UnityEngine;

namespace Genesis.RoomScan
{
    /// <summary>
    /// Periodically freezes voxels in view once the scan geometry has stabilized,
    /// reducing ongoing integration cost during gameplay scans.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoomScanGameplayAutoFreeze : MonoBehaviour
    {
        private const float MinScanDurationBeforeAutoFreezeSeconds = 60f;
        private const float MinFreezeIntervalSeconds = 10f;
        private const float StopAutoFreezeFrozenFraction = 0.85f;

        private RoomScanner _roomScanner;
        private RoomScanSession _roomScanSession;
        private float _lastAutoFreezeTime;
        private float _scanStartTime;

        private void Awake()
        {
            _roomScanner = GetComponent<RoomScanner>();
            _roomScanSession = GetComponent<RoomScanSession>();
        }

        private void Update()
        {
            if (_roomScanner == null || !_roomScanner.IsScanning) return;

            if (_scanStartTime <= 0f)
                _scanStartTime = Time.time;
            if (Time.time - _scanStartTime < MinScanDurationBeforeAutoFreezeSeconds) return;

            ScanProgress progress = _roomScanner.CurrentProgress;
            if (progress.Coverage.FrozenFraction >= StopAutoFreezeFrozenFraction) return;
            if (progress.Phase < ScanPhase.Stabilized) return;
            if (Time.time - _lastAutoFreezeTime < MinFreezeIntervalSeconds) return;

            if (!progress.Coverage.IsStabilized && progress.Phase < ScanPhase.Complete) return;

            if (_roomScanSession != null)
                _roomScanSession.FreezeInView();
            else
                _roomScanner.FreezeInView();

            _lastAutoFreezeTime = Time.time;
        }
    }
}
