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
            }
        }
    }

    public bool IsNotBusy => !IsBusy;
    public bool IsDemoMode => _provider is DemoDnsProvider;
    public bool HasSelectedDomain => SelectedDomain is not null;
    public bool HasSelectedRecord => SelectedRecord is not null;
    public bool SupportsPublicDnsPropagation => _provider.SupportsPublicDnsPropagation;
    public bool CanCheckPublicDns => HasSelectedRecord && SupportsPublicDnsPropagation;
    public bool HasPendingChanges => PendingChanges.Count > 0;
    public string PendingChangesText => $"Pending Changes ({PendingChanges.Count})";
    public string CurrentDomainName => SelectedDomain?.Name ?? "Select a domain";
    public string ProviderDisplay => _provider.ProviderName;
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
        await SetConfiguredProvidersAsync(_settings, cancellationToken);
        await LoadDomainsAsync(cancellationToken);
    }

    public async Task ConfigureAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _settingsStore.SaveAsync(settings, cancellationToken);
        _settings = settings;
        await SetConfiguredProvidersAsync(settings, cancellationToken);
        OnPropertyChanged(nameof(Settings));
        await LoadDomainsAsync(cancellationToken);
    }

    public async Task UseDemoAsync(CancellationToken cancellationToken = default)
    {
        DisposeProviders();
        _providers.Add(new DemoDnsProvider());
        ActivateProvider(_providers[0]);
        await LoadDomainsAsync(cancellationToken);
    }

    public async Task LoadDomainsAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
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
                catch (Exception exception)
                {
                    errors.Add($"{provider.ProviderName}: {exception.Message}");
                    Accounts.Add(new ProviderAccountViewModel(provider, []));
                }
            }

            ConnectionStatus = IsDemoMode ? "Demo mode — configure providers" : $"{_providers.Count} provider{(_providers.Count == 1 ? string.Empty : "s")} configured";
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
                StatusMessage = errors.Count == 0 ? "No domains were returned by the configured providers." : string.Join(" | ", errors);
            }
        });
    }

    public Task SelectDomainAsync(DomainSummary domain, CancellationToken cancellationToken = default) =>
        RunBusyAsync(() => SelectDomainCoreAsync(domain, cancellationToken));

    public Task RefreshZoneAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedDomain is null)
        {
            return Task.CompletedTask;
        }

        return RunBusyAsync(() => SelectDomainCoreAsync(SelectedDomain, cancellationToken));
    }

    public void AddRecord(DnsRecord record)
    {
        Records.Add(new DnsRecordViewModel(record));
        SelectedRecord = Records.Last();
        RefreshChanges();
        SetPendingStatus();
    }

    public void UpdateRecord(DnsRecordViewModel target, DnsRecord updated)
    {
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
        var recordsToDelete = records
            .Distinct()
            .Where(Records.Contains)
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
        StatusMessage = "Pending changes cleared.";
    }

    public ZoneValidationResult ValidateCurrentZone() => ZoneValidator.Validate(CurrentRecords());

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

        var validation = ValidateCurrentZone();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorSummary);
        }

        ApplyZoneResult result = ApplyZoneResult.NoChanges;
        await RunBusyAsync(async () =>
        {
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
                _originalRecords.Select(record => record.Clone()).ToArray());
            await _snapshotStore.SaveAsync(snapshot, cancellationToken);

            var desiredRecords = CurrentRecords();
            var changeCount = PendingChanges.Count;
            StatusMessage = $"Applying {changeCount} change{(changeCount == 1 ? string.Empty : "s")}…";
            await _auditLog.WriteAsync(new DnsAuditEntry(DateTimeOffset.Now, "zone-apply", _provider.ProviderName,
                currentDomain, "started", changeCount, ZoneComparer.Fingerprint(desiredRecords)), cancellationToken);
            try
            {
                await _provider.ReplaceZoneAsync(currentDomain, desiredRecords, cancellationToken);

                var verifiedZone = await WaitForVerifiedZoneAsync(currentDomain, desiredRecords, cancellationToken);
                if (verifiedZone is null)
                {
                    throw new InvalidOperationException($"{_provider.ProviderName} accepted the request, but the updated records did not become visible after several checks over 30 seconds. The update may still be propagating; refresh the zone before trying again. The pre-change snapshot was retained.");
                }

                SetRecords(verifiedZone.Records);
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
                throw;
            }
        });

        return result;
    }

    public Task<IReadOnlyList<ZoneSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default) =>
        SelectedDomain is null
            ? Task.FromResult<IReadOnlyList<ZoneSnapshot>>([])
            : _snapshotStore.GetRecentAsync(SelectedDomain.Name, cancellationToken: cancellationToken);

    public void StageSnapshot(ZoneSnapshot snapshot)
    {
        SetCurrentRecords(snapshot.Records);
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
        ActivateProvider(account.Provider);
        StatusMessage = $"Loading {domain.Name}…";
        var zone = await _provider.GetZoneAsync(domain.Name, cancellationToken);
        if (!zone.IsUsingProviderDns)
        {
            throw new InvalidOperationException($"{domain.Name} is not using {_provider.ProviderName} authoritative DNS. Its records cannot be enumerated through this provider.");
        }

        SelectedDomain = domain;
        SetRecords(zone.Records);
        _lastRefreshed = zone.RetrievedAt;
        OnPropertyChanged(nameof(LastRefreshedText));
        StatusMessage = $"Loaded {Records.Count} records for {domain.Name}.";
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
        RefreshChanges();
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

    private void RefreshChanges()
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
            record.Status = changedIds.TryGetValue(record.LocalId, out var status) ? status : "Unchanged";
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

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await operation();
        }
        finally
        {
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
    }

    private async Task SetConfiguredProvidersAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        DisposeProviders();
        if (settings.Namecheap.Enabled)
            _providers.Add(new NamecheapDnsProvider(new NamecheapOptions(settings.Namecheap.ApiUser, settings.Namecheap.UserName, settings.Namecheap.ApiKey, settings.Namecheap.ClientIp, settings.Namecheap.UseSandbox)));
        if (settings.Azure.Enabled)
            _providers.Add(new AzureDnsProvider(new AzureDnsOptions(settings.Azure.SubscriptionId, settings.Azure.TenantId, settings.Azure.ClientId, settings.Azure.ClientSecret)));
        if (settings.GoDaddy.Enabled)
            _providers.Add(new GoDaddyDnsProvider(new GoDaddyDnsOptions(settings.GoDaddy.Token)));
        if (settings.Cloudflare.Enabled)
            _providers.Add(new CloudflareDnsProvider(new CloudflareDnsOptions(settings.Cloudflare.Token)));
        if (settings.Route53.Enabled)
            _providers.Add(new Route53DnsProvider(new Route53DnsOptions(settings.Route53.AccessKeyId, settings.Route53.SecretAccessKey, settings.Route53.SessionToken)));
        if (settings.Google.Enabled)
            _providers.Add(await GoogleCloudDnsProvider.CreateAsync(new GoogleCloudDnsOptions(settings.Google.ProjectId, settings.Google.ServiceAccountJsonPath), cancellationToken));
        if (settings.WindowsDns.Enabled)
        {
            _windowsDnsRunner = new PowerShellJeaCommandRunner();
            foreach (var server in ParseWindowsDnsServers(settings.WindowsDns.Servers))
                _providers.Add(new WindowsDnsProvider(
                    new WindowsDnsOptions(server, settings.WindowsDns.EndpointName, settings.WindowsDns.SupportsPublicDnsPropagation),
                    _windowsDnsRunner));
        }
        if (_providers.Count == 0) _providers.Add(new DemoDnsProvider());
        ActivateProvider(_providers[0]);
    }

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
