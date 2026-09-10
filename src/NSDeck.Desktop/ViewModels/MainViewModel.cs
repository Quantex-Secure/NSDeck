using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;
using NSDeck.Core.Storage;
using NSDeck.Desktop.Services;
using NSDeck.Providers.Namecheap;
using NSDeck.Providers.Cloud;
using NSDeck.Providers.Windows;

namespace NSDeck.Desktop.ViewModels;

public enum ApplyZoneResult
{
    Applied,
    NoChanges,
    ExternalChangesDetected
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan[] VerificationRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15)
    ];

    private readonly SettingsStore _settingsStore;
    private readonly DraftStore _drafts;
    private bool _demoMode = true;
    private CancellationTokenSource? _operationCancellation;
    public void CancelCurrentOperation() => _operationCancellation?.Cancel();
    private readonly IZoneSnapshotStore _snapshotStore;
    private readonly AuditLogService _auditLog;
    private IDnsProvider _provider = new DemoDnsProvider();
    private readonly List<IDnsProvider> _providers = [];
    private PowerShellJeaCommandRunner? _windowsDnsRunner;
    private IReadOnlyList<DnsRecord> _originalRecords = [];
    private AppSettings _settings = new();
    private DomainSummary? _selectedDomain;
    private DnsRecordViewModel? _selectedRecord;
    private bool _isBusy;
    private bool _showOnlyModified;
    private string _searchText = string.Empty;
    private string _selectedType = "All";
    private string _statusMessage = "Starting…";
    private string _connectionStatus = "Demo mode";
    private DateTimeOffset? _lastRefreshed;

    public MainViewModel(SettingsStore settingsStore, IZoneSnapshotStore snapshotStore, AuditLogService auditLog)
    {
        _settingsStore = settingsStore;
        _drafts = new DraftStore(settingsStore.RootPath);
        _snapshotStore = snapshotStore;
        _auditLog = auditLog;
        RecordsView = CollectionViewSource.GetDefaultView(Records);
        RecordsView.Filter = FilterRecord;
    }

    public ObservableCollection<DomainSummary> Domains { get; } = [];
    public ObservableCollection<ProviderAccountViewModel> Accounts { get; } = [];
    public ObservableCollection<DnsRecordViewModel> Records { get; } = [];
    public ObservableCollection<ZoneChange> PendingChanges { get; } = [];
    public ICollectionView RecordsView { get; }
    public IReadOnlyList<string> RecordTypes { get; } = ["All", .. DnsRecordTypes.All];

    public AppSettings Settings => _settings;
    public IReadOnlyList<AccountProfile> Profiles => _settings.Profiles.Count > 0 ? _settings.Profiles : [new AccountProfile { Id = "legacy", Name = "Default" }];
    public string ActiveProfileId => _settings.ActiveProfile?.Id ?? "legacy";

    public Task SwitchProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == ActiveProfileId) return Task.CompletedTask;
        if (!_settings.Profiles.Any(p => p.Id == profileId)) throw new InvalidOperationException("The selected profile is no longer available.");
        return ConfigureAsync(new AppSettings { Profiles = _settings.Profiles, ActiveProfileId = profileId, Updates = _settings.Updates }, cancellationToken);
    }

    private void NotifyProfileSettings()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(Profiles));
        OnPropertyChanged(nameof(ActiveProfileId));
        OnPropertyChanged(nameof(ProviderDisplay));
        OnPropertyChanged(nameof(TargetDisplay));
    }

    public DomainSummary? SelectedDomain
    {
        get => _selectedDomain;
        private set
        {
            if (SetProperty(ref _selectedDomain, value))
            {
                OnPropertyChanged(nameof(CurrentDomainName));
                OnPropertyChanged(nameof(HasSelectedDomain));
            }
        }
    }

    public DnsRecordViewModel? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                OnPropertyChanged(nameof(HasSelectedRecord));
                OnPropertyChanged(nameof(CanCheckPublicDns));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;
    public bool IsDemoMode => _demoMode;
    public bool CanEdit => HasSelectedDomain && !_provider.IsReadOnly && !IsBusy;
    public bool IsReadOnly => _provider.IsReadOnly;
    public string TargetDisplay => SelectedDomain is null ? ProviderDisplay : $"{ProviderDisplay} / {SelectedDomain.DisplayName}{(IsReadOnly ? " — read-only" : "")}";
    public ZoneValidationResult ValidateRecords(IReadOnlyList<DnsRecord> records) => _provider.ValidateRecords(records);
    public bool HasSelectedDomain => SelectedDomain is not null;
    public bool HasSelectedRecord => SelectedRecord is not null;
    public bool SupportsPublicDnsPropagation => _provider.SupportsPublicDnsPropagation;
    public bool CanCheckPublicDns => HasSelectedRecord && SupportsPublicDnsPropagation;
    public bool HasPendingChanges => PendingChanges.Count > 0;
    public string PendingChangesText => $"Pending Changes ({PendingChanges.Count})";
    public string CurrentDomainName => SelectedDomain?.Name ?? "Select a domain";
    public string ProviderDisplay => !IsDemoMode && _providers.Count == 0 && _settings.ActiveProfile is { } profile ? profile.Name : _provider.ProviderName;
    public string RecordCountText => $"{Records.Count} record{(Records.Count == 1 ? string.Empty : "s")}";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string LastRefreshedText => _lastRefreshed is null
        ? "Not refreshed"
        : $"Last refreshed {_lastRefreshed:hh:mm tt}";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RecordsView.Refresh();
            }
        }
    }

    public string SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetProperty(ref _selectedType, value))
            {
                RecordsView.Refresh();
            }
        }
    }

    public bool ShowOnlyModified
    {
        get => _showOnlyModified;
        set
        {
            if (SetProperty(ref _showOnlyModified, value))
            {
                RecordsView.Refresh();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        NotifyProfileSettings();
        await SetConfiguredProvidersAsync(_settings, cancellationToken);
        await LoadDomainsAsync(cancellationToken);
    }

    public async Task ConfigureAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async ct =>
        {
            await SetConfiguredProvidersAsync(settings, ct);
            await _settingsStore.SaveAsync(settings, ct);
            _settings = settings;
            NotifyProfileSettings();
            await LoadDomainsCoreAsync(ct);
        }, cancellationToken);
    }

    public async Task UseDemoAsync(CancellationToken cancellationToken = default)
    {
        DisposeProviders();
        _demoMode = true;
        _providers.Add(new DemoDnsProvider());
        ActivateProvider(_providers[0]);
        await LoadDomainsAsync(cancellationToken);
    }

    public Task LoadDomainsAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(LoadDomainsCoreAsync, cancellationToken);

    private async Task LoadDomainsCoreAsync(CancellationToken cancellationToken)
    {
        StatusMessage = "Loading domains from configured providers…";
        Domains.Clear();
        Accounts.Clear();
        var errors = new List<string>();
        foreach (var provider in _providers)
        {
            try
            {
                var domains = await provider.GetDomainsAsync(cancellationToken);
                Accounts.Add(new ProviderAccountViewModel(provider, domains));
                foreach (var domain in domains) Domains.Add(domain);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                errors.Add($"{provider.ProviderName}: {exception.Message}");
                Accounts.Add(new ProviderAccountViewModel(provider, []));
            }
        }

        ConnectionStatus = IsDemoMode ? "Demo mode — configure providers" : $"{_settings.ActiveProfile?.Name ?? "Default"} — {_providers.Count} provider{(_providers.Count == 1 ? string.Empty : "s")} configured";
        OnPropertyChanged(nameof(IsDemoMode));
        OnPropertyChanged(nameof(ProviderDisplay));

        if (Domains.Count > 0)
        {
            await SelectDomainCoreAsync(Domains[0], cancellationToken);
            if (errors.Count > 0) StatusMessage = $"Loaded {Domains.Count} domains. Some providers failed: {string.Join(" | ", errors)}";
        }
        else
        {
            SelectedDomain = null;
            Records.Clear();
            _originalRecords = [];
            RefreshChanges();
            StatusMessage = errors.Count > 0 ? string.Join(" | ", errors)
                : _providers.Count == 0 && _settings.ActiveProfile is { } profile
                    ? $"{profile.Name} has no enabled providers. Open File → DNS Provider Accounts, select this profile, and choose Configure providers."
                    : "No domains were returned by the configured providers.";
        }
    }

    public Task SelectDomainAsync(DomainSummary domain, CancellationToken cancellationToken = default) =>
        RunBusyAsync(ct => SelectDomainCoreAsync(domain, ct), cancellationToken);

    public Task RefreshZoneAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedDomain is null)
        {
            return Task.CompletedTask;
        }

        return RunBusyAsync(ct => SelectDomainCoreAsync(SelectedDomain, ct), cancellationToken);
    }

    public void AddRecord(DnsRecord record)
    {
        EnsureEditable();
        Records.Add(new DnsRecordViewModel(record));
        SelectedRecord = Records.Last();
        RefreshChanges();
        SetPendingStatus();
    }

    public void UpdateRecord(DnsRecordViewModel target, DnsRecord updated)
    {
        EnsureEditable();
        if (target.Model.IsReadOnly) throw new InvalidOperationException(target.Model.ReadOnlyReason);
        target.Model.Name = updated.Name;
        target.Model.Type = updated.Type;
        target.Model.Value = updated.Value;
        target.Model.TtlSeconds = updated.TtlSeconds;
        target.Model.Priority = updated.Priority;
        target.RefreshBindings();
        RefreshChanges();
        SetPendingStatus();
    }

    public void DeleteRecord(DnsRecordViewModel record) => DeleteRecords([record]);

    public void DeleteRecords(IEnumerable<DnsRecordViewModel> records)
    {
        EnsureEditable();
        var recordsToDelete = records
            .Distinct()
            .Where(r => Records.Contains(r) && !r.Model.IsReadOnly)
            .ToArray();
        if (recordsToDelete.Length == 0) return;

        foreach (var record in recordsToDelete)
        {
            Records.Remove(record);
        }

        SelectedRecord = null;
        RefreshChanges();
        SetPendingStatus();
    }

    public void ClearChanges()
    {
        SetRecords(_originalRecords);
        DeleteDraft();
        StatusMessage = "Pending changes cleared.";
    }

    public ZoneValidationResult ValidateCurrentZone() => _provider.ValidateRecords(CurrentRecords());

    public DnsRiskReport AnalyzeCurrentRisks() => ZoneRiskAnalyzer.Analyze(_originalRecords, CurrentRecords());

    public IReadOnlyList<DnsProviderScope> GetProviderScopes() => Accounts
        .Select(account => new DnsProviderScope(account.Provider, account.Domains.ToArray()))
        .ToArray();

    public IReadOnlyList<string> GetEnabledProviderNames() => Accounts.Select(account => account.Name).ToArray();

    public async Task<ApplyZoneResult> ApplyChangesAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedDomain is null || PendingChanges.Count == 0)
        {
            return ApplyZoneResult.NoChanges;
        }

        EnsureEditable();
        var validation = ValidateCurrentZone();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorSummary);
        }

        ApplyZoneResult result = ApplyZoneResult.NoChanges;
        await RunBusyAsync(async operationToken =>
        {
            cancellationToken = operationToken;
            var currentDomain = SelectedDomain.Name;
            StatusMessage = $"Checking {currentDomain} for outside changes…";
            var latestZone = await _provider.GetZoneAsync(currentDomain, cancellationToken);
            if (ZoneComparer.Fingerprint(latestZone.Records) != ZoneComparer.Fingerprint(_originalRecords))
            {
                StatusMessage = "Apply stopped because the provider zone changed after it was loaded.";
                result = ApplyZoneResult.ExternalChangesDetected;
                return;
            }

            var snapshot = new ZoneSnapshot(
                currentDomain,
                _provider.ProviderName,
                DateTimeOffset.Now,
                ZoneComparer.Fingerprint(_originalRecords),
                _originalRecords.Select(record => record.Clone()).ToArray(), _provider.AccountId, _provider.ZoneId);
            await _snapshotStore.SaveAsync(snapshot, cancellationToken);

            var desiredRecords = CurrentRecords();
            var changeCount = PendingChanges.Count;
            StatusMessage = $"Applying {changeCount} change{(changeCount == 1 ? string.Empty : "s")}…";
            await _auditLog.WriteAsync(new DnsAuditEntry(DateTimeOffset.Now, "zone-apply", _provider.ProviderName,
                currentDomain, "started", changeCount, ZoneComparer.Fingerprint(desiredRecords)), cancellationToken);
            try
            {
                await _provider.ReplaceZoneGuardedAsync(currentDomain, _originalRecords, desiredRecords, cancellationToken);

                var verifiedZone = await WaitForVerifiedZoneAsync(currentDomain, desiredRecords, cancellationToken);
                if (verifiedZone is null)
                {
                    throw new InvalidOperationException($"{_provider.ProviderName} accepted the request, but the updated records did not become visible after several checks over 30 seconds. The update may still be propagating; refresh the zone before trying again. The pre-change snapshot was retained.");
                }

                SetRecords(verifiedZone.Records);
                DeleteDraft();
                _lastRefreshed = verifiedZone.RetrievedAt;
                OnPropertyChanged(nameof(LastRefreshedText));
                StatusMessage = $"{currentDomain} was updated and verified.";
                result = ApplyZoneResult.Applied;
                await _auditLog.WriteAsync(new DnsAuditEntry(DateTimeOffset.Now, "zone-apply", _provider.ProviderName,
                    currentDomain, "verified", changeCount, ZoneComparer.Fingerprint(verifiedZone.Records)), cancellationToken);
            }
            catch (Exception exception)
            {
                await _auditLog.WriteAsync(new DnsAuditEntry(DateTimeOffset.Now, "zone-apply", _provider.ProviderName,
                    currentDomain, "failed", changeCount, ZoneComparer.Fingerprint(desiredRecords), exception.Message), CancellationToken.None);
                throw new InvalidOperationException("The DNS operation did not complete verification. Changes may already have reached the provider. Refresh before retrying; the pre-change snapshot and saved draft are retained. " + exception.Message, exception);
            }
        }, cancellationToken);

        return result;
    }

    public Task<IReadOnlyList<ZoneSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default) =>
        SelectedDomain is null
            ? Task.FromResult<IReadOnlyList<ZoneSnapshot>>([])
            : _snapshotStore.GetRecentForTargetAsync(SelectedDomain.Name, _provider.AccountId, _provider.ZoneId, cancellationToken: cancellationToken);

    public void StageSnapshot(ZoneSnapshot snapshot)
    {
        EnsureEditable();
        if (snapshot.AccountId is not null && (snapshot.AccountId != _provider.AccountId || snapshot.ZoneId != _provider.ZoneId))
            throw new InvalidOperationException("This snapshot belongs to a different account or zone. Export its records and review a migration separately.");
        var desired = snapshot.Records.Where(r => !r.IsReadOnly).Concat(_originalRecords.Where(r => r.IsReadOnly)).ToArray();
        var validation = _provider.ValidateRecords(desired);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        SetCurrentRecords(desired);
        StatusMessage = $"Snapshot from {snapshot.CreatedAt:g} is staged. Review the changes before applying.";
    }

    public void ReportError(string message)
    {
        StatusMessage = message;
    }

    public IReadOnlyList<DnsRecord> GetCurrentRecords() => CurrentRecords();

    public void Dispose()
    {
        DisposeProviders();
    }

    private async Task SelectDomainCoreAsync(DomainSummary domain, CancellationToken cancellationToken)
    {
        var account = Accounts.FirstOrDefault(item => item.Domains.Contains(domain))
            ?? throw new InvalidOperationException($"The provider account for {domain.Name} is no longer available.");
        var candidate = account.Provider.ForZone(domain);
        StatusMessage = $"Loading {domain.Name}…";
        var zone = await candidate.GetZoneAsync(domain.Name, cancellationToken);
        if (!zone.IsUsingProviderDns)
        {
            throw new InvalidOperationException($"{domain.Name} is not using {candidate.ProviderName} authoritative DNS. Its records cannot be enumerated through this provider.");
        }

        // Commit the target and its records together only after a successful read.
        var draft = _drafts.Load(domain.Name, candidate.AccountId, candidate.ZoneId);
        ActivateProvider(candidate);
        SelectedDomain = domain;
        SetRecords(zone.Records);
        OnPropertyChanged(nameof(TargetDisplay));
        OnPropertyChanged(nameof(CanEdit));
        _lastRefreshed = zone.RetrievedAt;
        OnPropertyChanged(nameof(LastRefreshedText));
        StatusMessage = $"Loaded {Records.Count} records for {domain.Name}.";
        if (draft is not null)
        {
            if (draft.OriginalFingerprint == ZoneComparer.Fingerprint(_originalRecords) && !IsReadOnly)
            { SetCurrentRecords(draft.Records); StatusMessage = "Restored your saved draft. Review the pending changes before applying."; }
            else StatusMessage = "A saved draft exists, but this zone changed or the account is read-only. Use Action → Restore Saved Draft to review it.";
        }
    }

    private void SetRecords(IEnumerable<DnsRecord> records)
    {
        Records.Clear();
        var clones = records.Select(record => record.Clone()).ToArray();
        foreach (var record in clones)
        {
            Records.Add(new DnsRecordViewModel(record));
        }

        _originalRecords = clones.Select(record => record.Clone()).ToArray();
        SelectedRecord = null;
        RefreshChanges(persistDraft: false);
        OnPropertyChanged(nameof(RecordCountText));
    }

    private void SetCurrentRecords(IEnumerable<DnsRecord> records)
    {
        Records.Clear();
        foreach (var record in records.Select(record => record.Clone()))
        {
            Records.Add(new DnsRecordViewModel(record));
        }
        SelectedRecord = null;
        RefreshChanges();
    }

    private void RefreshChanges(bool persistDraft = true)
    {
        var changes = ZoneComparer.Diff(_originalRecords, CurrentRecords());
        PendingChanges.Clear();
        foreach (var change in changes)
        {
            PendingChanges.Add(change);
        }

        var changedIds = changes.ToDictionary(change => change.Record.LocalId, change => change.Kind.ToString());
        foreach (var record in Records)
        {
            record.Status = record.Model.IsReadOnly ? "Read-only" : changedIds.TryGetValue(record.LocalId, out var status) ? status : "Unchanged";
        }

        if (persistDraft && SelectedDomain is not null)
        {
            if (PendingChanges.Count == 0) DeleteDraft();
            else _drafts.Save(new ZoneDraft(SelectedDomain.Name, _provider.AccountId, _provider.ZoneId, ZoneComparer.Fingerprint(_originalRecords), CurrentRecords()));
        }
        RecordsView.Refresh();
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingChangesText));
        OnPropertyChanged(nameof(RecordCountText));
    }

    private void SetPendingStatus()
    {
        StatusMessage = PendingChanges.Count == 0
            ? "No pending changes."
            : $"{PendingChanges.Count} pending change{(PendingChanges.Count == 1 ? string.Empty : "s")}.";
    }

    private IReadOnlyList<DnsRecord> CurrentRecords() => Records.Select(viewModel => viewModel.Model.Clone()).ToArray();

    private async Task<DnsZone?> WaitForVerifiedZoneAsync(
        string domain,
        IReadOnlyList<DnsRecord> desiredRecords,
        CancellationToken cancellationToken)
    {
        var desiredFingerprint = ZoneComparer.Fingerprint(desiredRecords);

        for (var attempt = 0; attempt < VerificationRetryDelays.Length; attempt++)
        {
            var delay = VerificationRetryDelays[attempt];
            if (delay > TimeSpan.Zero)
            {
                StatusMessage = $"Waiting for {_provider.ProviderName} to publish the update… verification {attempt + 1} of {VerificationRetryDelays.Length}";
                await Task.Delay(delay, cancellationToken);
            }

            var zone = await _provider.GetZoneAsync(domain, cancellationToken);
            if (ZoneComparer.Fingerprint(zone.Records) == desiredFingerprint)
            {
                return zone;
            }
        }

        return null;
    }

    private bool FilterRecord(object item)
    {
        if (item is not DnsRecordViewModel record)
        {
            return false;
        }

        if (ShowOnlyModified && record.Status == "Unchanged")
        {
            return false;
        }

        if (SelectedType != "All" && !record.Type.Equals(SelectedType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return record.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               record.Type.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               record.Value.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        _operationCancellation = cancellation;
        try
        {
            await operation(cancellation.Token);
        }
        finally
        {
            _operationCancellation = null;
            IsBusy = false;
        }
    }

    private void ActivateProvider(IDnsProvider provider)
    {
        _provider = provider;
        OnPropertyChanged(nameof(IsDemoMode));
        OnPropertyChanged(nameof(ProviderDisplay));
        OnPropertyChanged(nameof(SupportsPublicDnsPropagation));
        OnPropertyChanged(nameof(CanCheckPublicDns));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(TargetDisplay));
        OnPropertyChanged(nameof(CanEdit));
    }

    private async Task SetConfiguredProvidersAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var next = new List<IDnsProvider>();
        AccountProfile[] profiles = [settings.ActiveProfile ?? new AccountProfile { Id = "legacy", Name = "Default", Connections = settings }];
        try
        {
            foreach (var profile in profiles)
            {
                var c = profile.Connections;
                void Add(IDnsProvider provider) => next.Add(new AccountDnsProvider(provider, profile.Id, profile.Name, profile.ReadOnly));
                if (c.Namecheap.Enabled) Add(new NamecheapDnsProvider(new NamecheapOptions(c.Namecheap.ApiUser, c.Namecheap.UserName, c.Namecheap.ApiKey, c.Namecheap.ClientIp, c.Namecheap.UseSandbox)));
                if (c.Azure.Enabled) Add(new AzureDnsProvider(new AzureDnsOptions(c.Azure.SubscriptionId, c.Azure.TenantId, c.Azure.ClientId, c.Azure.ClientSecret)));
                if (c.GoDaddy.Enabled) Add(new GoDaddyDnsProvider(new GoDaddyDnsOptions(c.GoDaddy.Token)));
                if (c.Cloudflare.Enabled) Add(new CloudflareDnsProvider(new CloudflareDnsOptions(c.Cloudflare.Token)));
                if (c.Route53.Enabled) Add(new Route53DnsProvider(new Route53DnsOptions(c.Route53.AccessKeyId, c.Route53.SecretAccessKey, c.Route53.SessionToken)));
                if (c.Google.Enabled) Add(await GoogleCloudDnsProvider.CreateAsync(new GoogleCloudDnsOptions(c.Google.ProjectId, c.Google.ServiceAccountJsonPath), cancellationToken));
                if (c.WindowsDns.Enabled)
                    foreach (var server in ParseWindowsDnsServers(c.WindowsDns.Servers))
                        Add(new WindowsDnsProvider(new WindowsDnsOptions(server, c.WindowsDns.EndpointName, c.WindowsDns.SupportsPublicDnsPropagation)));
            }
        }
        catch { foreach (var p in next.OfType<IDisposable>()) p.Dispose(); throw; }
        DisposeProviders();
        _demoMode = next.Count == 0 && settings.Profiles.Count == 0;
        _providers.AddRange(next);
        if (_demoMode) _providers.Add(new DemoDnsProvider());
        SelectedDomain = null;
        SetRecords([]);
        _lastRefreshed = null;
        OnPropertyChanged(nameof(LastRefreshedText));
        ActivateProvider(_providers.FirstOrDefault() ?? new DemoDnsProvider());
    }

    public void RestoreSavedDraft()
    {
        EnsureEditable();
        var draft = _drafts.Load(SelectedDomain!.Name, _provider.AccountId, _provider.ZoneId);
        if (draft is null) { StatusMessage = "No saved draft exists for this target."; return; }
        StageSnapshot(new ZoneSnapshot(draft.Domain, _provider.ProviderName, DateTimeOffset.Now, draft.OriginalFingerprint, draft.Records, draft.AccountId, draft.ZoneId));
        StatusMessage = "Saved draft staged against the current zone. Review every difference before applying.";
    }
    private void DeleteDraft() { if (SelectedDomain is not null) _drafts.Delete(SelectedDomain.Name, _provider.AccountId, _provider.ZoneId); }
    private void EnsureEditable() { if (!CanEdit) throw new InvalidOperationException("Select a writable zone and wait for the current operation to finish."); }

    private static IReadOnlyList<string> ParseWindowsDnsServers(string value) => value
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void DisposeProviders()
    {
        foreach (var disposable in _providers.OfType<IDisposable>()) disposable.Dispose();
        _providers.Clear();
        _windowsDnsRunner?.Dispose();
        _windowsDnsRunner = null;
        Accounts.Clear();
    }
}
