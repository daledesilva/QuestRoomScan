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
        private const float MinFreezeIntervalSeconds = 2.5f;
        private const float StopAutoFreezeFrozenFraction = 0.85f;

        private RoomScanner _roomScanner;
        private RoomScanSession _roomScanSession;
        private float _lastAutoFreezeTime;

        private void Awake()
        {
            _roomScanner = GetComponent<RoomScanner>();
            _roomScanSession = GetComponent<RoomScanSession>();
        }

        private void Update()
        {
            if (_roomScanner == null || !_roomScanner.IsScanning) return;

            ScanProgress progress = _roomScanner.CurrentProgress;
            if (progress.Coverage.FrozenFraction >= StopAutoFreezeFrozenFraction) return;
            if (progress.Phase < ScanPhase.Refining) return;
            if (Time.time - _lastAutoFreezeTime < MinFreezeIntervalSeconds) return;

            bool shouldFreeze = progress.Coverage.IsStabilized || progress.Phase >= ScanPhase.Stabilized;
            if (!shouldFreeze) return;

            if (_roomScanSession != null)
                _roomScanSession.FreezeInView();
            else
                _roomScanner.FreezeInView();

            _lastAutoFreezeTime = Time.time;
        }
    }
}
