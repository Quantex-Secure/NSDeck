using System.Text.Json;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NSDeck.Core.Models;
using NSDeck.Core.Services;
using NSDeck.Core.Storage;
using NSDeck.Desktop.Dialogs;
using NSDeck.Desktop.Services;
using NSDeck.Desktop.ViewModels;
using Microsoft.Win32;

namespace NSDeck.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AuditLogService _auditLog;
    private readonly DnsChangeLabService _changeLabService;
    private readonly PublicDnsResolverService _publicDnsResolver = new();
    private readonly UpdateService _updateService = new(currentVersion: typeof(MainWindow).Assembly.GetName().Version);
    private readonly bool _designPreview;
    private readonly bool _changeLabPreview;
    private bool _ignoreTreeSelection;

    public MainWindow(bool designPreview = false, bool changeLabPreview = false)
    {
        InitializeComponent();
        _designPreview = designPreview;
        _changeLabPreview = changeLabPreview;
        if (_designPreview)
        {
            Width = 1600;
            Height = 1000;
        }
        var appRoot = AppDataMigration.PrepareApplicationRoot();
        _auditLog = new AuditLogService(appRoot);
        var snapshotStore = new JsonZoneSnapshotStore(Path.Combine(appRoot, "snapshots"));
        _changeLabService = new DnsChangeLabService(snapshotStore, _auditLog.WriteAsync);
        _viewModel = new MainViewModel(
            new SettingsStore(appRoot),
            snapshotStore,
            _auditLog);
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _publicDnsResolver.Dispose();
            _updateService.Dispose();
            _viewModel.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            if (_designPreview)
            {
                await _viewModel.UseDemoAsync();
                if (!_changeLabPreview) StagePreviewChanges();
            }
            else
            {
                await _viewModel.InitializeAsync();
            }
            SelectCurrentDomainInTree();
            if (_changeLabPreview) _ = Dispatcher.BeginInvoke(new Action(() => ChangeLab_Click(this, new RoutedEventArgs())));
            else if (!_designPreview && _viewModel.Settings.Updates.CheckAutomatically)
                _ = Dispatcher.BeginInvoke(new Action(async () => await CheckForUpdatesAsync(showCurrentMessage: false)));
        }, "Unable to load domains");
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_viewModel.Settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        await ExecuteUiAsync(async () =>
        {
            await _viewModel.ConfigureAsync(dialog.Result);
            SelectCurrentDomainInTree();
        }, "Unable to connect to DNS providers");
    }

    private async void DomainsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_ignoreTreeSelection || e.NewValue is not DomainSummary domain || domain == _viewModel.SelectedDomain) return;
        if (_viewModel.HasPendingChanges)
        {
            var choice = MessageBox.Show(this,
                $"Discard {_viewModel.PendingChanges.Count} pending change{(_viewModel.PendingChanges.Count == 1 ? string.Empty : "s")} and open {domain.Name}?",
                "Pending changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                SelectCurrentDomainInTree();
                return;
            }
        }
        await ExecuteUiAsync(() => _viewModel.SelectDomainAsync(domain), $"Unable to load {domain.Name}");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPendingChanges && MessageBox.Show(this,
                "Refresh will discard all pending changes. Continue?", "Refresh zone",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteUiAsync(() => _viewModel.RefreshZoneAsync(), "Unable to refresh the zone");
    }

    private void NewRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSelectedDomain) return;
        var dialog = new RecordEditorWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null) _viewModel.AddRecord(dialog.Result);
    }

    private void EditRecord_Click(object sender, RoutedEventArgs e) => EditSelectedRecord();
    private void RecordsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedRecord();

    private void EditSelectedRecord()
    {
        if (_viewModel.SelectedRecord is null || RecordsGrid.SelectedItems.Count != 1) return;
        var dialog = new RecordEditorWindow(_viewModel.SelectedRecord.Model.Clone()) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
            _viewModel.UpdateRecord(_viewModel.SelectedRecord, dialog.Result);
    }

    private void DeleteRecord_Click(object sender, RoutedEventArgs e) => DeleteSelectedRecords();

    private void RecordsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        DeleteSelectedRecords();
    }

    private void DeleteSelectedRecords()
    {
        var records = RecordsGrid.SelectedItems.OfType<DnsRecordViewModel>().ToArray();
        if (records.Length == 0) return;

        var prompt = records.Length == 1
            ? $"Stage deletion of {records[0].Name} {records[0].Type}?"
            : $"Stage deletion of {records.Length} selected DNS records?";

        if (MessageBox.Show(this, $"{prompt}\n\nThe deletion{(records.Length == 1 ? string.Empty : "s")} will remain pending until you click Apply Changes.",
                "Delete DNS records", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _viewModel.DeleteRecords(records);
        }
    }

    private async void ApplyChanges_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasPendingChanges) return;
        var validation = _viewModel.ValidateCurrentZone();
        if (!validation.IsValid)
        {
            MessageBox.Show(this, validation.ErrorSummary, "Zone validation failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var risk = _viewModel.AnalyzeCurrentRisks();
        var riskReview = risk.HasWarnings ? $"\n\nRisk review:\n{risk.Summary}" : string.Empty;
        if (MessageBox.Show(this,
                $"Apply {_viewModel.PendingChanges.Count} pending change{(_viewModel.PendingChanges.Count == 1 ? string.Empty : "s")} to {_viewModel.CurrentDomainName}?\n\nA pre-change snapshot will be saved first.{riskReview}",
                "Apply complete zone", MessageBoxButton.YesNo, risk.HasWarnings ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (risk.HasCriticalRisks && MessageBox.Show(this,
                $"This plan contains critical DNS risks for {_viewModel.CurrentDomainName}. Are you certain you want to continue?",
                "Confirm critical DNS change", MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;

        var propagationTargets = _viewModel.SupportsPublicDnsPropagation
            ? _viewModel.PendingChanges
                .Where(change => change.Kind is ZoneChangeKind.Add or ZoneChangeKind.Update)
                .Select(change => ToPropagationTarget(_viewModel.CurrentDomainName, change.Record))
                .Distinct()
                .ToArray()
            : [];

        await ExecuteUiAsync(async () =>
        {
            var result = await _viewModel.ApplyChangesAsync();
            if (result == ApplyZoneResult.ExternalChangesDetected)
                MessageBox.Show(this, "The provider zone changed after it was loaded. No data was written. Refresh the zone and reapply your intended changes.",
                    "Outside change detected", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (result == ApplyZoneResult.Applied)
            {
                var openRadar = propagationTargets.Length > 0 && MessageBox.Show(this,
                    "The zone was updated and verified successfully.\n\nOpen the public DNS propagation radar?",
                    "DNS update complete", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
                if (openRadar) new PropagationWindow(_publicDnsResolver, propagationTargets) { Owner = this }.Show();
            }
        }, "Unable to apply the zone");
    }

    private void PublicDnsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.SupportsPublicDnsPropagation || _viewModel.SelectedRecord is null || _viewModel.SelectedDomain is null) return;
        var target = ToPropagationTarget(_viewModel.SelectedDomain.Name, _viewModel.SelectedRecord.Model);
        new PropagationWindow(_publicDnsResolver, [target]) { Owner = this }.Show();
    }

    private void ChangeLab_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPendingChanges)
        {
            MessageBox.Show(this, "Apply or clear the current zone's pending changes before opening the Change Lab.",
                "Pending changes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new ChangeLabWindow(_changeLabService, _viewModel.GetProviderScopes(), _publicDnsResolver) { Owner = this }.ShowDialog();
    }

    private void ClearChanges_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasPendingChanges) return;
        if (MessageBox.Show(this, "Discard all pending changes?", "Clear changes", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _viewModel.ClearChanges();
    }

    private void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SearchText = string.Empty;
        _viewModel.SelectedType = "All";
        _viewModel.ShowOnlyModified = false;
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            var snapshots = await _viewModel.GetSnapshotsAsync();
            var dialog = new HistoryWindow(snapshots) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedSnapshot is not null)
                _viewModel.StageSnapshot(dialog.SelectedSnapshot);
        }, "Unable to load zone history");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedDomain is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export DNS zone snapshot", Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"{_viewModel.SelectedDomain.Name}-{DateTime.Now:yyyyMMdd-HHmm}.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var snapshot = new ZoneSnapshot(_viewModel.SelectedDomain.Name, _viewModel.ProviderDisplay, DateTimeOffset.Now,
            ZoneComparer.Fingerprint(_viewModel.GetCurrentRecords()), _viewModel.GetCurrentRecords());
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedDomain is null) return;
        var dialog = new OpenFileDialog { Title = "Import DNS zone snapshot", Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<ZoneSnapshot>(File.ReadAllText(dialog.FileName), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (snapshot is null) throw new InvalidDataException("The file does not contain a zone snapshot.");
            if (!snapshot.Domain.Equals(_viewModel.SelectedDomain.Name, StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(this, $"This snapshot is for {snapshot.Domain}, but {_viewModel.SelectedDomain.Name} is open. Stage it anyway?",
                    "Domain mismatch", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _viewModel.StageSnapshot(snapshot);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Unable to import snapshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export shareable diagnostic report",
            Filter = "ZIP archives (*.zip)|*.zip",
            FileName = $"NSDeck-shareable-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        await ExecuteUiAsync(() => DiagnosticsExporter.ExportAsync(dialog.FileName, _auditLog, _viewModel.GetEnabledProviderNames()),
            "Unable to export diagnostics");
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"NSDeck {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\nA Quantex Secure product\n\nEvery zone. One deck.\n\n.NET 10 WPF administration console for Namecheap, Azure DNS, GoDaddy, Cloudflare, AWS Route 53, Google Cloud DNS, and Windows DNS Server.\n\nIncludes guarded zone editing, DNS Change Lab multi-zone transactions, rollback, dependency analysis, shareable diagnostics, and public resolver propagation checks.\n\nCopyright © 2026 Quantex Secure\nLicensed under Apache License 2.0.",
        "About NSDeck", MessageBoxButton.OK, MessageBoxImage.Information);

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(showCurrentMessage: true);

    private async Task CheckForUpdatesAsync(bool showCurrentMessage)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.Settings.Updates.ManifestUrl))
        {
            if (showCurrentMessage) MessageBox.Show(this, "Configure an HTTPS update manifest address on the Updates tab in Settings first.",
                "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = await _updateService.CheckAsync(_viewModel.Settings.Updates.ManifestUrl);
            if (result.UpdateAvailable && result.DownloadUri is not null)
            {
                if (MessageBox.Show(this, $"{result.Message}\n\nOpen the secure download location?",
                        "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(result.DownloadUri.AbsoluteUri) { UseShellExecute = true });
            }
            else if (showCurrentMessage)
            {
                MessageBox.Show(this, result.Message, "Check for updates", MessageBoxButton.OK,
                    result.AvailableVersion is null ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            if (showCurrentMessage) MessageBox.Show(this, exception.Message, "Unable to check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async Task ExecuteUiAsync(Func<Task> operation, string title)
    {
        try { await operation(); }
        catch (Exception exception)
        {
            _viewModel.ReportError(exception.Message);
            await _auditLog.WriteAsync(new DnsAuditEntry(DateTimeOffset.Now, "ui-error", _viewModel.ProviderDisplay,
                _viewModel.SelectedDomain?.Name ?? string.Empty, "failed", Detail: $"{title}: {exception.Message}"), CancellationToken.None);
            MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static DnsPropagationTarget ToPropagationTarget(string domain, DnsRecord record)
    {
        var relative = record.Name.Trim().TrimEnd('.');
        var name = relative is "" or "@"
            ? domain
            : relative.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase) ? relative : $"{relative}.{domain}";
        var expected = record.Type.Equals("MX", StringComparison.OrdinalIgnoreCase)
            ? $"{record.Priority ?? 0} {record.Value}"
            : record.Value;
        return new DnsPropagationTarget(name, record.Type, expected);
    }

    private void SelectCurrentDomainInTree()
    {
        if (_viewModel.SelectedDomain is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var account in _viewModel.Accounts)
            {
                if (!account.Domains.Contains(_viewModel.SelectedDomain)) continue;
                if (DomainsTree.ItemContainerGenerator.ContainerFromItem(account) is not TreeViewItem accountItem) continue;
                accountItem.IsExpanded = true;
                accountItem.UpdateLayout();
                if (accountItem.ItemContainerGenerator.ContainerFromItem(_viewModel.SelectedDomain) is TreeViewItem item)
                {
                    _ignoreTreeSelection = true;
                    item.IsSelected = true;
                    item.BringIntoView();
                    _ignoreTreeSelection = false;
                }
                break;
            }
        });
    }

    private void StagePreviewChanges()
    {
        _viewModel.AddRecord(new DnsRecord { Name = "new", Type = "A", Value = "198.51.100.25", TtlSeconds = 300 });
        var dmarc = _viewModel.Records.FirstOrDefault(record => record.Name == "_dmarc");
        if (dmarc is not null)
        {
            var update = dmarc.Model.Clone();
            update.Value = "v=DMARC1; p=reject";
            _viewModel.UpdateRecord(dmarc, update);
        }
    }
}
