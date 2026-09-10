using System.Windows;
using System.Windows.Controls;
using NSDeck.Core.Models;
using NSDeck.Core.Services;

namespace NSDeck.Desktop.Dialogs;

public partial class RecordEditorWindow : Window
{
    private readonly DnsRecord? _source;

    public RecordEditorWindow(DnsRecord? source = null)
    {
        InitializeComponent();
        _source = source;
        TypeBox.ItemsSource = DnsRecordTypes.All;
        TtlBox.ItemsSource = new[] { 1, 60, 300, 600, 1200, 1800, 3600, 86400 };

        if (source is null)
        {
            TypeBox.SelectedItem = "A";
            TtlBox.Text = "1800";
            NameBox.Text = "@";
        }
        else
        {
            HeadingText.Text = "Edit DNS record";
            SaveButton.Content = "Save Changes";
            NameBox.Text = source.Name;
            TypeBox.SelectedItem = source.Type;
            ValueBox.Text = source.Value;
            TtlBox.Text = source.TtlSeconds.ToString();
            PriorityBox.Text = source.Priority?.ToString() ?? string.Empty;
        }
        UpdatePriorityState();
    }

    public DnsRecord? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var type = TypeBox.SelectedItem as string;
        var value = ValueBox.Text.Trim();
        if (!int.TryParse(TtlBox.Text, out var ttl) || ttl <= 0) { MessageBox.Show(this, "Enter a positive TTL in seconds."); return; }
        int? priority = null;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value))
        {
            MessageBox.Show(this, "Name, type, and value are required.", "Incomplete record", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (type == "MX")
        {
            if (!int.TryParse(PriorityBox.Text.Trim(), out var parsedPriority) || parsedPriority is < 0 or > 65535)
            {
                MessageBox.Show(this, "MX priority must be a number from 0 through 65535.", "Invalid priority", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            priority = parsedPriority;
        }
        Result = new DnsRecord
        {
            LocalId = _source?.LocalId ?? Guid.NewGuid(), ProviderRecordId = _source?.ProviderRecordId, ProviderMetadata = _source?.ProviderMetadata,
            Name = name, Type = type, Value = value, TtlSeconds = ttl, Priority = priority
        };
        DialogResult = true;
    }

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePriorityState();
    private void UpdatePriorityState()
    {
        if (PriorityBox is null || TypeBox is null) return;
        var isMx = string.Equals(TypeBox.SelectedItem as string, "MX", StringComparison.OrdinalIgnoreCase);
        PriorityBox.IsEnabled = isMx;
        if (!isMx) PriorityBox.Text = string.Empty;
        else if (string.IsNullOrWhiteSpace(PriorityBox.Text)) PriorityBox.Text = "10";
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private sealed record TtlOption(string Label, int Seconds);
}
