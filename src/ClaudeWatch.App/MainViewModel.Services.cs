using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using ClaudeWatch.Core;

namespace ClaudeWatch.App;

/// <summary>One entry in the service switcher.</summary>
public sealed class ServiceTab
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Selected { get; init; }
    public bool Enabled { get; init; }
    public Brush Accent { get; init; } = Brushes.Gray;
}

public sealed partial class MainViewModel
{
    private readonly LicenceClient _licence = new();

    private bool _licenceBusy;

    // ============================================================== services

    public ObservableCollection<ServiceTab> ServiceTabs { get; } = new();

    public string ActiveServiceName => _settings.Active.Name;

    public string ActiveServiceKey => _settings.Active.Key;

    /// <summary>The colour the window takes on, which follows the service.</summary>
    public string EffectiveAccent => string.IsNullOrWhiteSpace(_settings.Active.Accent)
        ? _settings.AccentColor
        : _settings.Active.Accent;

    /// <summary>Only worth showing the switcher when there is something to switch to.</summary>
    public bool ShowServiceSwitch => _settings.Services.Count(s => s.Enabled) > 1;

    public void RefreshServiceTabs()
    {
        ServiceTabs.Clear();

        foreach (var profile in _settings.Services)
        {
            ServiceTabs.Add(new ServiceTab
            {
                Key = profile.Key,
                Name = profile.Name,
                Enabled = profile.Enabled,
                Selected = string.Equals(profile.Key, _settings.ActiveServiceKey, StringComparison.OrdinalIgnoreCase),
                Accent = Brush(profile.Accent)
            });
        }

        Raise(nameof(ShowServiceSwitch));
        Raise(nameof(ActiveServiceName));
        Raise(nameof(ActiveServiceKey));
    }

    /// <summary>
    /// Moves the window, the clock rule and the whole palette to another service.
    /// Everything the user is looking at changes with it, which is the point:
    /// there should never be any doubt about which app is being guarded.
    /// </summary>
    public void SwitchService(string? key)
    {
        var profile = _settings.ByKey(key);

        if (profile is null || string.Equals(profile.Key, _settings.ActiveServiceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.ActiveServiceKey = profile.Key;
        Edit.ActiveServiceKey = profile.Key;
        L.ServiceName = profile.Name;

        _store.Save(_settings);

        RefreshServiceTabs();
        ThemeChanged?.Invoke(this, EventArgs.Empty);

        // The engine's clock target comes from the active profile, so a fresh
        // poll rather than a stale snapshot.
        Guard.PollNow();

        Raise(nameof(L));
        RaiseAllSettings();
        RefreshStatusText();
        RefreshBrushes();
    }

    // ============================================================== licence

    public LicenceStatus Licence => _licence.Status;

    public string LicenceKeyInput { get; set; } = string.Empty;

    public bool LicenceBusy => _licenceBusy;

    public bool LicenceOk => LicenceClient.Allowed(_licence.Status, DateTimeOffset.UtcNow);

    public string LicenceHeadline
    {
        get
        {
            var status = _licence.Status;

            return status.State switch
            {
                LicenceState.NotRequired => L["Lic_Active"],
                LicenceState.Licensed => L["Lic_Active"],
                LicenceState.Trial => L["Lic_Trial"],
                LicenceState.Expired => L["Lic_Expired"],
                LicenceState.WrongDevice => L["Lic_WrongDevice"],
                LicenceState.Revoked => L["Lic_Revoked"],
                _ => L["Lic_NeedsKey"]
            };
        }
    }

    public string LicenceDetail
    {
        get
        {
            var status = _licence.Status;
            var now = DateTimeOffset.UtcNow;

            return status.State switch
            {
                LicenceState.Licensed when status.ExpiresAt is { } ends =>
                    $"{status.DaysLeft} {L["Lic_DaysLeft"]} · {L["Lic_Until"]} {ends.ToLocalTime():yyyy/MM/dd}",
                LicenceState.Trial =>
                    $"{status.TrialDaysLeft(now)} {L["Lic_TrialLeft"]}",
                _ => string.Empty
            };
        }
    }

    public Brush LicenceBrush => LicenceOk ? Palette("Good") : Palette("Alert");

    public string LicenceError { get; private set; } = string.Empty;

    public bool LicenceHasError => !string.IsNullOrWhiteSpace(LicenceError);

    /// <summary>
    /// Loads what is on disk, then asks the server in the background. The guard
    /// starts from the cached answer so a slow or missing server never delays
    /// protection at startup.
    /// </summary>
    public async Task StartLicenceAsync()
    {
        _licence.Load();
        _licence.Refresh(DateTimeOffset.UtcNow);

        LicenceKeyInput = _settings.LicenceKey;
        Guard.Licensed = LicenceOk;
        RaiseLicence();

        if (!OrdersConfigured)
        {
            return;
        }

        await _licence.FetchTermsAsync(_settings.OrdersBaseUrl).ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(_settings.LicenceKey))
        {
            await _licence.CheckAsync(
                _settings.OrdersBaseUrl,
                _settings.LicenceKey,
                LicenceClient.DeviceId(),
                LicenceClient.DeviceName()).ConfigureAwait(true);
        }

        _licence.Refresh(DateTimeOffset.UtcNow);
        Guard.Licensed = LicenceOk;
        RaiseLicence();
    }

    public async Task ActivateAsync()
    {
        if (_licenceBusy)
        {
            return;
        }

        LicenceError = string.Empty;

        if (!OrdersConfigured)
        {
            LicenceError = L["Lic_NoServer"];
            RaiseLicence();
            return;
        }

        _licenceBusy = true;
        RaiseLicence();

        var typed = (LicenceKeyInput ?? string.Empty).Trim();

        var (ok, error) = await _licence.CheckAsync(
            _settings.OrdersBaseUrl,
            typed,
            LicenceClient.DeviceId(),
            LicenceClient.DeviceName()).ConfigureAwait(true);

        if (ok)
        {
            _settings.LicenceKey = typed;
            Edit.LicenceKey = typed;
            _store.Save(_settings);
            _log.Add(ActivityKind.Good, "Key activated", LicenceDetail);
        }
        else
        {
            LicenceError = error switch
            {
                "unknown_key" => L["Lic_Unknown"],
                "bad_shape" => L["Lic_Unknown"],
                "wrong_device" => L["Lic_WrongDevice"],
                "expired" => L["Lic_Expired"],
                "revoked" => L["Lic_Revoked"],
                "offline" => L["Lic_Offline"],
                "no_server" => L["Lic_NoServer"],
                _ => L["Lic_Unknown"]
            };
        }

        _licence.Refresh(DateTimeOffset.UtcNow);
        Guard.Licensed = LicenceOk;

        _licenceBusy = false;
        RaiseLicence();
        RefreshStatusText();
    }

    /// <summary>Re-checks with the server on a slow schedule while the app runs.</summary>
    public async Task RecheckLicenceAsync()
    {
        if (!OrdersConfigured || string.IsNullOrWhiteSpace(_settings.LicenceKey))
        {
            _licence.Refresh(DateTimeOffset.UtcNow);
            Guard.Licensed = LicenceOk;
            RaiseLicence();
            return;
        }

        await _licence.CheckAsync(
            _settings.OrdersBaseUrl,
            _settings.LicenceKey,
            LicenceClient.DeviceId(),
            LicenceClient.DeviceName()).ConfigureAwait(true);

        _licence.Refresh(DateTimeOffset.UtcNow);
        Guard.Licensed = LicenceOk;
        RaiseLicence();
    }

    private void RaiseLicence()
    {
        Raise(nameof(Licence));
        Raise(nameof(LicenceOk));
        Raise(nameof(LicenceBusy));
        Raise(nameof(LicenceHeadline));
        Raise(nameof(LicenceDetail));
        Raise(nameof(LicenceBrush));
        Raise(nameof(LicenceError));
        Raise(nameof(LicenceHasError));
        Raise(nameof(LicenceKeyInput));
    }

    // ==================================================== settings bindings

    public bool ClaudeEnabled
    {
        get => Edit.ByKey(ServiceProfile.ClaudeKey)?.Enabled ?? false;
        set => SetServiceEnabled(ServiceProfile.ClaudeKey, value, nameof(ClaudeEnabled));
    }

    public bool ChatGptEnabled
    {
        get => Edit.ByKey(ServiceProfile.ChatGptKey)?.Enabled ?? false;
        set => SetServiceEnabled(ServiceProfile.ChatGptKey, value, nameof(ChatGptEnabled));
    }

    public string ClaudePath => PathOf(ServiceProfile.ClaudeKey);

    public string ChatGptPath => PathOf(ServiceProfile.ChatGptKey);

    private string PathOf(string key)
    {
        var path = Edit.ByKey(key)?.ExecutablePath;
        return string.IsNullOrWhiteSpace(path) ? L["Claude_Missing"] : path;
    }

    private void SetServiceEnabled(string key, bool value, string name)
    {
        if (Edit.ByKey(key) is not { } profile)
        {
            return;
        }

        // Switching everything off would leave a guard with nothing to guard,
        // so the last one on stays on.
        if (!value && Edit.Services.Count(s => s.Enabled) <= 1)
        {
            Raise(name);
            return;
        }

        profile.Enabled = value;
        Raise(name);
    }

    // ================================================================ wizard

    /// <summary>True until the first-run questions have been answered.</summary>
    public bool NeedsSetup => !_settings.SetupCompleted;

    /// <summary>
    /// Applies what the wizard collected. Separate from the normal Save so the
    /// wizard can land its answers without the Settings page being involved.
    /// </summary>
    public void CompleteSetup()
    {
        SaveSettings();

        L.Language = _settings.Language;
        L.ServiceName = _settings.Active.Name;

        RefreshServiceTabs();
        ThemeChanged?.Invoke(this, EventArgs.Empty);

        Raise(nameof(L));
        Raise(nameof(Flow));
        Raise(nameof(NeedsSetup));
        RaiseAllSettings();

        // Find the apps they just ticked, and register the privileged pieces.
        _ = RunAutoSetupAsync(force: true);

        if (!string.IsNullOrWhiteSpace(LicenceKeyInput))
        {
            _ = ActivateAsync();
        }
    }

    private static Brush Brush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return Brushes.Gray;
        }
    }
}
