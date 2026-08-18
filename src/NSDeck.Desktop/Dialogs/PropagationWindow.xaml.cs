using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using NSDeck.Core.Services;

namespace NSDeck.Desktop.Dialogs;

public sealed record DnsPropagationTarget(string Name, string Type, string ExpectedValue);

public sealed record DnsPropagationRow(
    string Name,
    string Type,
    string Resolver,
    string Status,
    string Answer,
    string TtlDisplay);

public partial class PropagationWindow : Window
{
    private readonly PublicDnsResolverService _resolverService;
    private readonly IReadOnlyList<DnsPropagationTarget> _targets;
    private readonly ObservableCollection<DnsPropagationRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _isRefreshing;

    public PropagationWindow(PublicDnsResolverService resolverService, IReadOnlyList<DnsPropagationTarget> targets)
    {
        InitializeComponent();
        _resolverService = resolverService;
        _targets = targets.Distinct().ToArray();
        ResultsGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) =>
        {
            if (AutoRefreshBox.IsChecked == true) await RefreshAsync();
        };
        _timer.Start();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            StatusText.Text = $"Checking {_targets.Count} record{(_targets.Count == 1 ? string.Empty : "s")}…";
            var nextRows = new List<DnsPropagationRow>();
            foreach (var target in _targets)
            {
                var results = await _resolverService.ResolveAsync(target.Name, target.Type);
                foreach (var result in results)
                {
                    var answers = result.Answers.Select(answer => answer.Data).ToArray();
                    var answerText = answers.Length == 0 ? "—" : string.Join(" | ", answers);
                    var status = result.Error is not null
                        ? "Error"
                        : result.ResponseCode != 0
                            ? $"DNS code {result.ResponseCode}"
                            : answers.Any(answer => ValuesMatch(answer, target.ExpectedValue)) ? "Match" : "Not matched";
                    var ttl = result.Answers.Count == 0 ? "—" : $"{result.Answers.Min(answer => answer.Ttl)} sec";
                    nextRows.Add(new DnsPropagationRow(target.Name, target.Type, result.Resolver, status,
                        result.Error ?? answerText, ttl));
                }
            }

            _rows.Clear();
            foreach (var row in nextRows) _rows.Add(row);
            var matches = nextRows.Count(row => row.Status == "Match");
            StatusText.Text = $"Last checked {DateTime.Now:t} — {matches} of {nextRows.Count} resolver answers match the expected values.";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static bool ValuesMatch(string answer, string expected) =>
        Normalize(answer).Equals(Normalize(expected), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) => value.Trim().Trim('"').TrimEnd('.');

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
