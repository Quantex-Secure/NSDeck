using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NSDeck.Core.Models;
using NSDeck.Core.Services;

namespace NSDeck.Desktop.Dialogs;

public sealed class ChangeLabRecordRow(DnsInventoryZone zone, DnsRecord record)
{
    public DnsInventoryZone Zone { get; } = zone;
    public DnsRecord Record { get; } = record;
    public string Provider => Zone.ProviderName;
    public string Domain => Zone.Domain;
    public string Name => Record.Name;
    public string Type => Record.Type;
    public string Value => Record.Value;
    public int Ttl => Record.TtlSeconds;
    public string Key => $"{Provider}|{Domain}|{Record.LocalId}";
    public DnsInventoryRecord InventoryRecord => new(Provider, Domain, Record);
}

public sealed class ChangeLabPlanRow(DnsPlannedChange change)
{
    public DnsPlannedChange Change { get; } = change;
    public string Provider => Change.Zone.ProviderName;
    public string Domain => Change.Zone.Domain;
    public string Name => Change.Original.Name;
    public string Type => Change.Original.Type;
    public string Before => Change.Original.Value;
    public string After => Change.Updated.Value;
    public string Key => $"{Provider}|{Domain}|{Change.Original.LocalId}";
}

public partial class ChangeLabWindow : Window
{
    private readonly DnsChangeLabService _changeLabService;
    private readonly IReadOnlyList<DnsProviderScope> _scopes;
    private readonly PublicDnsResolverService _resolverService;
    private readonly ObservableCollection<ChangeLabRecordRow> _inventoryRows = [];
    private readonly ObservableCollection<ChangeLabPlanRow> _planRows = [];
    private ICollectionView _inventoryView;
    private DnsInventoryLoadResult? _inventory;
    private bool _isBusy;

    public ChangeLabWindow(
        DnsChangeLabService changeLabService,
        IReadOnlyList<DnsProviderScope> scopes,
        PublicDnsResolverService resolverService)
    {
        InitializeComponent();
        _changeLabService = changeLabService;
        _scopes = scopes;
        _resolverService = resolverService;
        InventoryGrid.ItemsSource = _inventoryRows;
        PlanGrid.ItemsSource = _planRows;
        _inventoryView = CollectionViewSource.GetDefaultView(_inventoryRows);
        _inventoryView.Filter = FilterInventory;
        Loaded += async (_, _) => await LoadInventoryAsync();
    }

    private async void RefreshInventory_Click(object sender, RoutedEventArgs e) => await LoadInventoryAsync();

    private async Task LoadInventoryAsync()
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            _inventory = await _changeLabService.LoadInventoryAsync(_scopes, progress);
            _inventoryRows.Clear();
            foreach (var zone in _inventory.Zones)
                foreach (var record in zone.Records)
                    _inventoryRows.Add(new ChangeLabRecordRow(zone, record));
            _inventoryView.Refresh();
            StatusText.Text = $"Indexed {_inventoryRows.Count} records across {_inventory.Zones.Count} zones and {_scopes.Count} providers" +
                              (_inventory.Errors.Count == 0 ? "." : $"; {_inventory.Errors.Count} zones reported errors.");
            UpdateImpact();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "Unable to load global DNS inventory", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool FilterInventory(object item)
    {
        if (item is not ChangeLabRecordRow row) return false;
        var search = SearchBox.Text.Trim();
        return search.Length == 0 || new[] { row.Provider, row.Domain, row.Name, row.Type, row.Value }
            .Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _inventoryView?.Refresh();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) FindValueBox.Text = SearchBox.Text.Trim();
    }

    private void SelectMatches_Click(object sender, RoutedEventArgs e)
    {
        InventoryGrid.SelectedItems.Clear();
        foreach (var item in _inventoryView.Cast<object>()) InventoryGrid.SelectedItems.Add(item);
        InventoryGrid.Focus();
        UpdateImpact();
    }

    private void InventoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateImpact();

    private void UpdateImpact()
    {
        ImpactTree.Items.Clear();
        var selected = InventoryGrid.SelectedItems.Cast<ChangeLabRecordRow>().ToArray();
        if (selected.Length == 0 || _inventory is null)
        {
            ImpactSummaryText.Text = "Select one or more records to analyze.";
            return;
        }

        var selectedRecords = selected.Select(row => row.InventoryRecord).ToArray();
        var dependencies = DnsDependencyAnalyzer.Analyze(_inventory.Records, selectedRecords);
        ImpactSummaryText.Text = $"{selected.Length} selected; {dependencies.Count} direct dependencies or shared values found.";

        foreach (var row in selected.Take(30))
        {
            var root = new TreeViewItem { Header = $"{row.Name}.{row.Domain}  {row.Type}" };
            var related = dependencies.Where(dependency =>
                dependency.Source.Record.LocalId == row.Record.LocalId ||
                dependency.RelatedRecord?.Record.LocalId == row.Record.LocalId).ToArray();
            if (related.Length == 0) root.Items.Add(new TreeViewItem { Header = "No direct dependencies found" });
            foreach (var dependency in related.Take(25))
                root.Items.Add(new TreeViewItem { Header = $"{dependency.Relationship}: {dependency.Source.Fqdn} ({dependency.Source.Record.Type}) → {dependency.Target}" });
            root.IsExpanded = true;
            ImpactTree.Items.Add(root);
        }
    }

    private void StageReplacement_Click(object sender, RoutedEventArgs e)
    {
        var selected = InventoryGrid.SelectedItems.Cast<ChangeLabRecordRow>().ToArray();
        var find = FindValueBox.Text;
        var replacement = ReplacementBox.Text;
        if (selected.Length == 0 || string.IsNullOrEmpty(find) || string.IsNullOrEmpty(replacement))
        {
            MessageBox.Show(this, "Select records and enter both the existing text and its replacement.", "Change Lab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var staged = 0;
        foreach (var row in selected)
        {
            if (!row.Value.Contains(find, StringComparison.OrdinalIgnoreCase)) continue;
            var updated = row.Record.Clone();
            updated.Value = updated.Value.Replace(find, replacement, StringComparison.OrdinalIgnoreCase);
            var planned = new ChangeLabPlanRow(new DnsPlannedChange(row.Zone, row.Record.Clone(), updated));
            var existing = _planRows.FirstOrDefault(item => item.Key == planned.Key);
            if (existing is not null) _planRows.Remove(existing);
            _planRows.Add(planned);
            staged++;
        }

        PlanHeading.Text = $"Coordinated Change Plan ({_planRows.Count})";
        ApplyPlanButton.IsEnabled = _planRows.Count > 0 && !_isBusy;
        StatusText.Text = staged == 0
            ? "None of the selected record values contained the text to replace."
            : $"Added {staged} record{(staged == 1 ? string.Empty : "s")} to a {_planRows.Select(row => row.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count()}-zone transaction.";
    }

    private void ClearPlan_Click(object sender, RoutedEventArgs e)
    {
        _planRows.Clear();
        PlanHeading.Text = "Coordinated Change Plan (0)";
        ApplyPlanButton.IsEnabled = false;
        StatusText.Text = "Change plan cleared.";
    }

    private async void ApplyPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_planRows.Count == 0 || _isBusy) return;
        var changes = _planRows.Select(row => row.Change).ToArray();
        var riskText = BuildRiskSummary(changes);
        var message = $"Apply {changes.Length} coordinated changes across {changes.Select(change => change.Zone.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count()} zones?\n\n" +
                      "Every zone will be rechecked and snapshotted first. If a write or verification fails, completed writes will be rolled back."
                      + (riskText.Length == 0 ? string.Empty : $"\n\nRisk review:\n{riskText}");
        if (MessageBox.Show(this, message, "Apply DNS Change Lab plan", MessageBoxButton.YesNo,
                riskText.Length == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(status => StatusText.Text = status);
            var result = await _changeLabService.ApplyAsync(changes, progress);
            var details = string.Join(Environment.NewLine, result.Operations.Select(operation =>
                $"• {operation.Provider} {operation.Domain}: {operation.Status}{(string.IsNullOrWhiteSpace(operation.Detail) ? string.Empty : " — " + operation.Detail)}"));
            if (result.Succeeded)
            {
                MessageBox.Show(this, $"The coordinated DNS transaction completed successfully.\n\n{details}",
                    "Change Lab complete", MessageBoxButton.OK, MessageBoxImage.Information);
                var targets = changes
                    .Where(change => change.Zone.Provider.SupportsPublicDnsPropagation)
                    .Select(ToPropagationTarget)
                    .Distinct()
                    .ToArray();
                if (targets.Length > 0)
                {
                    var radar = new PropagationWindow(_resolverService, targets) { Owner = this };
                    radar.Show();
                }
                _planRows.Clear();
                PlanHeading.Text = "Coordinated Change Plan (0)";
                await LoadInventoryAfterApplyAsync();
            }
            else
            {
                MessageBox.Show(this, $"The coordinated transaction did not complete.\n\n{details}\n\n" +
                                      (result.RollbackAttempted
                                          ? result.RollbackSucceeded ? "Completed writes were rolled back and verified." : "One or more rollback checks failed; review the provider zones immediately."
                                          : "No provider writes were made."),
                    "Change Lab stopped", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Change Lab failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadInventoryAfterApplyAsync()
    {
        SetBusy(false);
        await LoadInventoryAsync();
        SetBusy(true);
    }

    private static string BuildRiskSummary(IReadOnlyCollection<DnsPlannedChange> changes)
    {
        var warnings = new List<string>();
        foreach (var group in changes.GroupBy(change => change.Zone))
        {
            var desired = group.Key.Records.Select(record => record.Clone()).ToList();
            foreach (var change in group)
            {
                var index = desired.FindIndex(record => DnsRecord.ContentEquals(record, change.Original));
                if (index >= 0) desired[index] = change.Updated.Clone();
            }
            var report = ZoneRiskAnalyzer.Analyze(group.Key.Records, desired);
            warnings.AddRange(report.Risks.Select(risk => $"{group.Key.Domain}: {risk.Message}"));
        }
        return string.Join(Environment.NewLine, warnings.Distinct(StringComparer.Ordinal).Take(10).Select(warning => "• " + warning));
    }

    private static DnsPropagationTarget ToPropagationTarget(DnsPlannedChange change)
    {
        var relative = change.Updated.Name.Trim().TrimEnd('.');
        var name = relative is "" or "@"
            ? change.Zone.Domain
            : relative.EndsWith($".{change.Zone.Domain}", StringComparison.OrdinalIgnoreCase)
                ? relative
                : $"{relative}.{change.Zone.Domain}";
        var expected = change.Updated.Type.Equals("MX", StringComparison.OrdinalIgnoreCase)
            ? $"{change.Updated.Priority ?? 0} {change.Updated.Value}"
            : change.Updated.Value;
        return new DnsPropagationTarget(name, change.Updated.Type, expected);
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        ApplyPlanButton.IsEnabled = !busy && _planRows.Count > 0;
        InventoryGrid.IsEnabled = !busy;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
