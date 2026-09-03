using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services.Remote;

namespace Lumi.ViewModels;

public sealed record RemotePairedDeviceItem(
    string DeviceId,
    string DeviceName,
    string LastSeenText);

internal enum MobileSetupKind
{
    Web,
    Android
}

/// <summary>
/// Settings for the mobile companion. Kept in its own partial so the phone feature adds no noise
/// to the main settings view model.
/// </summary>
public partial class SettingsViewModel
{
    private LumiRemoteServer? _remoteServer;
    private IDisposable? _remotePairingExpiryRegistration;
    private bool _attachingRemoteServer;
    private MobileSetupKind _activeMobileSetupKind;

    [ObservableProperty] private bool _remoteAccessEnabled;
    [ObservableProperty] private bool _useLocalNetworkForMobile;
    [ObservableProperty] private string _remotePairingCode = "";
    [ObservableProperty] private bool _isRemotePairing;
    [ObservableProperty] private string _remotePairActionText = Loc.Get("Remote_PairButton");
    [ObservableProperty] private string _remoteStatusText = "";
    [ObservableProperty] private string _remoteDevicesText = "";
    [ObservableProperty] private bool _canManageRemoteSecurity = true;
    [ObservableProperty] private bool _isMobileSetupActive;
    [ObservableProperty] private bool _isMobileSetupChoiceEnabled;
    [ObservableProperty] private bool _isMobileWebSetup;
    [ObservableProperty] private bool _isMobileAndroidSetup;
    [ObservableProperty] private bool _isMobileSetupReady;
    [ObservableProperty] private bool _isMobileTailscaleAvailable;
    [ObservableProperty] private string _mobileTransportDescription = "";
    [ObservableProperty] private string _mobileSetupTitle = "";
    [ObservableProperty] private string _mobileSetupDescription = "";
    [ObservableProperty] private string _mobileSetupConnectionText = "";
    [ObservableProperty] private string _mobileSetupInstructions = "";
    [ObservableProperty] private string _mobileSetupUrl = "";
    [ObservableProperty] private string _mobileSetupQrValue = "";

    public ObservableCollection<RemotePairedDeviceItem> RemoteDevices { get; } = [];

    public bool IsMobileTailscaleSelected =>
        IsMobileTailscaleAvailable && !UseLocalNetworkForMobile;

    public bool IsMobileLocalNetworkSelected => UseLocalNetworkForMobile;

    internal void AttachRemoteServer(LumiRemoteServer server)
    {
        if (_remoteServer is { } previous)
            previous.StateChanged -= OnRemoteServerStateChanged;

        _remotePairingExpiryRegistration?.Dispose();
        _remotePairingExpiryRegistration = null;
        _remoteServer = server;
        // App startup owns the guarded listener start. Reflect persisted state without letting the
        // generated setter callback start the server before that guard.
        _attachingRemoteServer = true;
        try
        {
            RemoteAccessEnabled = _dataStore.Data.Settings.RemoteAccessEnabled;
            UseLocalNetworkForMobile = _dataStore.Data.Settings.RemoteAllowInsecureLan;
        }
        finally
        {
            _attachingRemoteServer = false;
        }
        server.StateChanged += OnRemoteServerStateChanged;
        RefreshRemoteState();
    }

    private void OnRemoteServerStateChanged()
    {
        if (_remoteServer is not { } source)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RefreshRemoteState();
        else
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(_remoteServer, source))
                        RefreshRemoteState();
                },
                DispatcherPriority.Background);
        }
    }

    private void RefreshRemoteState() => RefreshRemoteState(DateTimeOffset.UtcNow);

    internal void RefreshRemoteState(DateTimeOffset now)
    {
        var server = _remoteServer;
        CanManageRemoteSecurity = server is null
            || server.CanManageSecurityState && server.IsSecurityStateReady;
        var devices = _dataStore.SnapshotRemotePairedDevices();
        _attachingRemoteServer = true;
        try
        {
            RemoteAccessEnabled = _dataStore.Data.Settings.RemoteAccessEnabled;
            UseLocalNetworkForMobile = _dataStore.Data.Settings.RemoteAllowInsecureLan;
        }
        finally
        {
            _attachingRemoteServer = false;
        }
        IsMobileTailscaleAvailable = server is { IsRunning: true, IsTailscaleAvailable: true };
        MobileTransportDescription = Loc.Get(
            IsMobileTailscaleAvailable
                ? "Remote_TransportDetected"
                : "Remote_TransportUnavailable");
        var pairing = server is null
            ? (Code: (string?)null, ExpiresAt: (DateTimeOffset?)null)
            : server.GetPairingDisplayState(now);

        RemoteStatusText = server switch
        {
            { IsRunning: true } when server.ListenAddresses.Count > 0 =>
                Loc.Get(
                    "Remote_ListeningOn",
                    PreferredRemoteAddress(server, UseLocalNetworkForMobile)),
            { IsRunning: true } => Loc.Get("Remote_WaitingForConnection"),
            _ => Loc.Get("Remote_NotRunning")
        };

        RemoteDevicesText = devices.Count == 0
            ? Loc.Get("Remote_NoDevices")
            : Loc.Get("Remote_DeviceCount", devices.Count.ToString());
        RemoteDevices.Clear();
        foreach (var device in devices.OrderBy(static device => device.DeviceName, StringComparer.CurrentCultureIgnoreCase))
        {
            RemoteDevices.Add(new RemotePairedDeviceItem(
                device.DeviceId,
                string.IsNullOrWhiteSpace(device.DeviceName) ? Loc.Get("Remote_UnknownDevice") : device.DeviceName,
                device.LastSeenAt is { } lastSeen
                    ? Loc.Get("Remote_LastSeen", lastSeen.ToLocalTime().ToString("g"))
                    : Loc.Get("Remote_LastSeenNever")));
        }

        RemotePairingCode = pairing.Code ?? "";
        IsRemotePairing = RemotePairingCode.Length > 0;
        RemotePairActionText = Loc.Get(IsRemotePairing ? "Remote_PairStop" : "Remote_PairButton");
        IsMobileSetupChoiceEnabled =
            RemoteAccessEnabled
            && server is { IsRunning: true }
            && server.ListenAddresses.Count > 0;
        RefreshMobileOnboarding(server);
        ScheduleRemotePairingExpiry(pairing.ExpiresAt, now);
    }

    private void ScheduleRemotePairingExpiry(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        _remotePairingExpiryRegistration?.Dispose();
        _remotePairingExpiryRegistration = null;

        if (!IsRemotePairing || expiresAt is not { } expiry)
            return;

        var delay = expiry - now;
        if (delay <= TimeSpan.Zero)
            return;

        _remotePairingExpiryRegistration = DispatcherTimer.RunOnce(
            () =>
            {
                _remotePairingExpiryRegistration = null;
                RefreshRemoteState();
            },
            delay,
            DispatcherPriority.Background);
    }

    private static string PreferredRemoteAddress(
        LumiRemoteServer server,
        bool useLocalNetwork) =>
        MobileOnboardingLinks.SelectEndpoint(
            server.ListenAddresses,
            useLocalNetwork
                ? MobileOnboardingTransport.LocalNetwork
                : MobileOnboardingTransport.Tailscale)?.BaseUrl
        ?? "http://127.0.0.1";

    partial void OnRemoteAccessEnabledChanged(bool value)
    {
        if (_attachingRemoteServer)
            return;
        if (_remoteServer is { CanManageSecurityState: false })
        {
            RefreshRemoteState();
            return;
        }

        if (_dataStore.Data.Settings.RemoteAccessEnabled == value && _remoteServer?.IsRunning == value)
            return;

        _dataStore.Data.Settings.RemoteAccessEnabled = value;
        _dataStore.MarkRemoteSecurityChanged();
        _ = PersistRemoteSettingsAsync();

        if (value)
            _remoteServer?.Start();
        else
        {
            ResetMobileOnboarding();
            _remoteServer?.Stop();
        }

        RefreshRemoteState();
    }

    partial void OnUseLocalNetworkForMobileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMobileTailscaleSelected));
        OnPropertyChanged(nameof(IsMobileLocalNetworkSelected));
        if (_attachingRemoteServer)
            return;
        if (_remoteServer is { CanManageSecurityState: false })
        {
            RefreshRemoteState();
            return;
        }

        if (_dataStore.Data.Settings.RemoteAllowInsecureLan == value)
            return;

        _dataStore.Data.Settings.RemoteAllowInsecureLan = value;
        _dataStore.MarkRemoteSecurityChanged();
        _ = PersistRemoteSettingsAsync();
        _remoteServer?.RefreshNetworkPolicy();
        RefreshRemoteState();
    }

    partial void OnIsMobileTailscaleAvailableChanged(bool value) =>
        OnPropertyChanged(nameof(IsMobileTailscaleSelected));

    private async Task PersistRemoteSettingsAsync()
    {
        try
        {
            await _dataStore.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Remote] Failed to persist phone settings: {ex.Message}");
        }
    }

    [RelayCommand]
    private void StartMobileWebSetup() =>
        StartMobileSetup(MobileSetupKind.Web);

    [RelayCommand]
    private void StartMobileAndroidSetup() =>
        StartMobileSetup(MobileSetupKind.Android);

    private void StartMobileSetup(MobileSetupKind kind)
    {
        if (!RemoteAccessEnabled || _remoteServer is not { IsRunning: true } server)
            return;

        _activeMobileSetupKind = kind;
        IsMobileSetupActive = true;
        IsMobileWebSetup = kind == MobileSetupKind.Web;
        IsMobileAndroidSetup = kind == MobileSetupKind.Android;
        server.BeginPairing();
        RefreshMobileOnboarding(server);
    }

    [RelayCommand]
    private void SelectMobileTailscale()
    {
        if (RemoteAccessEnabled && IsMobileTailscaleAvailable)
            UseLocalNetworkForMobile = false;
    }

    [RelayCommand]
    private void SelectMobileLocalNetwork()
    {
        if (RemoteAccessEnabled)
            UseLocalNetworkForMobile = true;
    }

    [RelayCommand]
    private async Task CopyMobileSetupLinkAsync()
    {
        if (MobileSetupUrl.Length == 0)
            return;

        await Services.ClipboardHelper.CopyTextAsync(MobileSetupUrl);
        MobileSetupDescription = Loc.Get("Remote_SetupCopied");
    }

    private void RefreshMobileOnboarding(LumiRemoteServer? server)
    {
        if (!IsMobileSetupActive)
            return;

        MobileSetupTitle = Loc.Get(
            _activeMobileSetupKind == MobileSetupKind.Web
                ? "Remote_SetupWebPanelTitle"
                : "Remote_SetupAndroidPanelTitle");
        MobileSetupInstructions = Loc.Get(
            _activeMobileSetupKind == MobileSetupKind.Web
                ? "Remote_SetupWebInstructions"
                : "Remote_SetupAndroidInstructions");

        if (server is not { IsRunning: true })
        {
            SetMobileSetupUnavailable(Loc.Get("Remote_WebSetupStarting"));
            return;
        }

        if (!server.IsWebAppAvailable)
        {
            SetMobileSetupUnavailable(Loc.Get("Remote_WebAppUnavailable"));
            return;
        }

        var endpoint = MobileOnboardingLinks.SelectEndpoint(
            server.ListenAddresses,
            UseLocalNetworkForMobile
                ? MobileOnboardingTransport.LocalNetwork
                : MobileOnboardingTransport.Tailscale);
        if (endpoint is null)
        {
            SetMobileSetupUnavailable(Loc.Get("Remote_SetupNoAddress"));
            return;
        }

        MobileSetupUrl = _activeMobileSetupKind == MobileSetupKind.Web
            ? MobileOnboardingLinks.BuildWebAppUrl(endpoint.BaseUrl)
            : MobileOnboardingLinks.BuildAndroidInstallUrl(
                endpoint.BaseUrl,
                AppVersion);
        MobileSetupQrValue = MobileSetupUrl;
        MobileSetupDescription = Loc.Get(
            _activeMobileSetupKind == MobileSetupKind.Web
                ? "Remote_SetupWebPanelDesc"
                : "Remote_SetupAndroidPanelDesc");
        MobileSetupConnectionText = Loc.Get(
            endpoint.Transport == MobileOnboardingTransport.LocalNetwork
                ? "Remote_SetupUsingWifi"
                : "Remote_SetupUsingTailscale");
        IsMobileSetupReady = true;
    }

    private void SetMobileSetupUnavailable(string description)
    {
        IsMobileSetupReady = false;
        MobileSetupDescription = description;
        MobileSetupConnectionText = "";
        MobileSetupUrl = "";
        MobileSetupQrValue = "";
    }

    private void ResetMobileOnboarding()
    {
        IsMobileSetupActive = false;
        IsMobileSetupChoiceEnabled = false;
        IsMobileWebSetup = false;
        IsMobileAndroidSetup = false;
        IsMobileSetupReady = false;
        IsMobileTailscaleAvailable = false;
        MobileTransportDescription = "";
        MobileSetupTitle = "";
        MobileSetupDescription = "";
        MobileSetupConnectionText = "";
        MobileSetupInstructions = "";
        MobileSetupUrl = "";
        MobileSetupQrValue = "";
    }

    [RelayCommand]
    private void ToggleRemotePairing()
    {
        if (_remoteServer is not { IsRunning: true } server)
            return;

        if (IsRemotePairing)
            server.CancelPairing();
        else
            server.BeginPairing();
    }

    [RelayCommand]
    private async Task RevokeRemoteDeviceAsync(RemotePairedDeviceItem? device)
    {
        if (device is null || _remoteServer is not { CanManageSecurityState: true } server)
            return;

        await server.RevokeDeviceAsync(device.DeviceId);
        RefreshRemoteState();
    }

    private void DisposeRemoteState()
    {
        _remotePairingExpiryRegistration?.Dispose();
        _remotePairingExpiryRegistration = null;

        if (_remoteServer is { } server)
            server.StateChanged -= OnRemoteServerStateChanged;
        _remoteServer = null;
    }
}
