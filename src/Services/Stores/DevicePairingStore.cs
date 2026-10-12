namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  DevicePairingStore
//
//  Lets an unattended kiosk device (e.g. the workshop TV dashboard, which has
//  no keyboard and can't log in itself) be approved by a staff member instead.
//  Flow:
//    1. The kiosk calls GetOrCreateCode(deviceId) and displays the code.
//    2. A staff member, already logged into the main app, visits /pair-device
//       and enters the code — that page calls TryApprove with their username.
//    3. The kiosk polls GetApprovedBy(deviceId) and, once set, shows its
//       dashboard. JMS data comes from the shared JmsReadSession, so the
//       kiosk doesn't depend on the approver's own JMS session.
//
//  In-memory and single-instance, matching every other *Store singleton in
//  this app (ScopeJobStore, HighlightedJobsStore, ...) — a restart clears all
//  pairings and every kiosk just re-displays a fresh code.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class DevicePairingStore
{
    private sealed class Pairing
    {
        public string? Code { get; set; }
        public DateTime CodeExpiresAtUtc { get; set; }
        public string? ApprovedByUsername { get; set; }
    }

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Pairing> _byDeviceId = new();
    private readonly Dictionary<string, string> _deviceIdByCode = new();
    private readonly object _lock = new();

    /// Returns the device's current pairing code, minting a new one if there
    /// isn't a live one. Once a device is approved this keeps returning null —
    /// callers should check GetApproval first.
    public string? GetOrCreateCode(string deviceId)
    {
        lock (_lock)
        {
            if (!_byDeviceId.TryGetValue(deviceId, out var pairing))
            {
                pairing = new Pairing();
                _byDeviceId[deviceId] = pairing;
            }

            if (pairing.ApprovedByUsername != null) return null;

            if (pairing.Code != null && pairing.CodeExpiresAtUtc > DateTime.UtcNow)
                return pairing.Code;

            if (pairing.Code != null) _deviceIdByCode.Remove(pairing.Code);

            var code = GenerateCode();
            pairing.Code = code;
            pairing.CodeExpiresAtUtc = DateTime.UtcNow.Add(CodeLifetime);
            _deviceIdByCode[code] = deviceId;
            return code;
        }
    }

    /// Called from /pair-device with the approving staff member's username.
    /// Codes are single-use — approving consumes it immediately.
    public bool TryApprove(string code, string username)
    {
        lock (_lock)
        {
            if (!_deviceIdByCode.TryGetValue(code, out var deviceId)) return false;
            _deviceIdByCode.Remove(code);

            if (!_byDeviceId.TryGetValue(deviceId, out var pairing)) return false;
            if (pairing.Code != code || pairing.CodeExpiresAtUtc < DateTime.UtcNow) return false;

            pairing.ApprovedByUsername = username;
            pairing.Code = null;
            return true;
        }
    }

    /// Username of whoever approved the device, or null if it isn't paired.
    public string? GetApprovedBy(string deviceId)
    {
        lock (_lock)
        {
            return _byDeviceId.TryGetValue(deviceId, out var pairing) ? pairing.ApprovedByUsername : null;
        }
    }

    public void Unpair(string deviceId)
    {
        lock (_lock)
        {
            _byDeviceId.Remove(deviceId);
        }
    }

    private static string GenerateCode() => Random.Shared.Next(100_000, 999_999).ToString();
}
