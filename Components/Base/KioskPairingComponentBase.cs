using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Components.Helpers;

/// <summary>
/// Base component for wall-mounted kiosk pages (the workshop TV screens).
/// These are AllowAnonymous — nobody logs into a wall-mounted screen every
/// session — so instead of JmsComponentBase's cookie-claim check, a staff
/// member approves the device once via DevicePairingStore (see that class, and
/// /pair-device for the approval side). The device id lives in this browser's
/// localStorage so a refresh/reboot doesn't lose pairing. JMS data comes from
/// the shared service-account session (GetJmsAsync), not the approver's.
///
/// Usage in pages:
///     @inherits KioskPairingComponentBase
///
///     @if (Initializing) { ... }
///     else if (!IsPaired) { ... show PairingCode ... }
///     else { ... dashboard, using GetJmsAsync()/PairedUsername ... }
///
///     @code {
///         protected override async Task OnPairedAsync()
///         {
///             // runs once, right after pairing completes (or immediately if
///             // already paired on first load) — e.g. initialize a chart
///         }
///     }
/// </summary>
public abstract class KioskPairingComponentBase : ComponentBase, IDisposable
{
    [Inject] protected DevicePairingStore PairingStore { get; set; } = default!;
    [Inject] protected JmsReadSession JmsRead { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    protected bool Initializing { get; private set; } = true;
    protected bool IsPaired { get; private set; }
    protected string? PairingCode { get; private set; }
    protected string? PairedUsername { get; private set; }

    /// <summary>Null until paired, or while JMS is unreachable.</summary>
    protected Task<IJmsReader?> GetJmsAsync() =>
        IsPaired ? JmsRead.GetClientAsync() : Task.FromResult<IJmsReader?>(null);

    private string _deviceId = string.Empty;
    private System.Threading.Timer? _pollTimer;
    private bool _pairedTransitionHandled;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _deviceId = await GetOrCreateDeviceIdAsync();
            RefreshPairingStatus();
            Initializing = false;

            if (!IsPaired)
            {
                _pollTimer = new System.Threading.Timer(async _ => await InvokeAsync(() =>
                {
                    RefreshPairingStatus();
                    StateHasChanged();
                }), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            }

            StateHasChanged();
        }

        if (IsPaired && !_pairedTransitionHandled)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            _pairedTransitionHandled = true;
            await OnPairedAsync();

            // OnAfterRenderAsync doesn't auto-render when it completes (unlike
            // OnInitializedAsync) — without this, whatever OnPairedAsync loaded
            // just sits in memory until something else happens to trigger a
            // render (e.g. the next 30s clock tick on pages that have one).
            StateHasChanged();
        }
    }

    /// <summary>Runs once, right after the device becomes paired.</summary>
    protected virtual Task OnPairedAsync() => Task.CompletedTask;

    private void RefreshPairingStatus()
    {
        var approvedBy = PairingStore.GetApprovedBy(_deviceId);
        if (approvedBy != null)
        {
            IsPaired = true;
            PairedUsername = approvedBy;
        }
        else
        {
            IsPaired = false;
            PairingCode = PairingStore.GetOrCreateCode(_deviceId);
        }
    }

    private async Task<string> GetOrCreateDeviceIdAsync()
    {
        var existing = await JS.InvokeAsync<string?>("localStorage.getItem", "kiosk-device-id");
        if (!string.IsNullOrEmpty(existing)) return existing;

        var id = Guid.NewGuid().ToString("N");
        await JS.InvokeVoidAsync("localStorage.setItem", "kiosk-device-id", id);
        return id;
    }

    public virtual void Dispose() => _pollTimer?.Dispose();
}
