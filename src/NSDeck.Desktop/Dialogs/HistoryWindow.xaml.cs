using System.Windows;
using NSDeck.Core.Models;

namespace NSDeck.Desktop.Dialogs;

public partial class HistoryWindow : Window
{
    public HistoryWindow(IReadOnlyList<ZoneSnapshot> snapshots)
    {
        InitializeComponent();
        DataContext = snapshots;
    }

    public ZoneSnapshot? SelectedSnapshot { get; private set; }

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsGrid.SelectedItem is not ZoneSnapshot snapshot)
        {
            MessageBox.Show(this, "Select a snapshot first.", "Zone history", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedSnapshot = snapshot;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
