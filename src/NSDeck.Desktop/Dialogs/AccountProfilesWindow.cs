using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NSDeck.Desktop.Services;

namespace NSDeck.Desktop.Dialogs;

public sealed class AccountProfilesWindow : Window
{
    private readonly ObservableCollection<AccountProfile> _profiles;
    private readonly ListBox _list = new() { MinHeight = 180, Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly CheckBox _readOnly = new() { Content = "Read-only: allow viewing and scanning; block all DNS writes", Margin = new Thickness(0, 0, 0, 16) };
    private UpdateSettings _updates;
    public AppSettings? Result { get; private set; }

    public AccountProfilesWindow(AppSettings settings)
    {
        Title = "DNS account profiles"; Width = 700; Height = 580; MinWidth = 600; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _updates = settings.Updates;
        _profiles = new(settings.Profiles.Count > 0 ? settings.Profiles : [new AccountProfile { Id = "legacy", Name = "Default", Connections = settings }]);
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Account profiles", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Keep each customer or environment in a named profile. Credentials are encrypted for this Windows account.", TextWrapping = TextWrapping.Wrap });
        _list.ItemsSource = _profiles; panel.Children.Add(_list);
        panel.Children.Add(new TextBlock { Text = "Profile name" }); panel.Children.Add(_name); panel.Children.Add(_readOnly);
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is AccountProfile p) { _name.Text = p.Name; _readOnly.IsChecked = p.ReadOnly; } };
        var actions = new WrapPanel(); panel.Children.Add(actions);
        AddButton(actions, "Add profile", () => { var p = new AccountProfile { Name = "New profile", ReadOnly = true }; _profiles.Add(p); _list.SelectedItem = p; });
        AddButton(actions, "Save name / access", SaveLabel);
        AddButton(actions, "Configure providers…", () =>
        {
            SaveLabel();
            if (_list.SelectedItem is not AccountProfile p) return;
            var dialog = new SettingsWindow(p.Connections) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is not null)
            {
                var next = new AccountProfile { Id = p.Id, Name = p.Name, ReadOnly = p.ReadOnly, Connections = dialog.Result };
                _profiles[_profiles.IndexOf(p)] = next; _list.SelectedItem = next; _updates = dialog.Result.Updates;
            }
        });
        AddButton(actions, "Remove", () => { if (_list.SelectedItem is AccountProfile p && MessageBox.Show(this, $"Remove profile {p.Name}?", "Remove account profile", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _profiles.Remove(p); });
        var footer = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) }; panel.Children.Add(footer);
        AddButton(footer, "Save and connect", () =>
        {
            SaveLabel();
            if (_profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _profiles.Count)
            { MessageBox.Show(this, "Use a different name for each profile."); return; }
            Result = new AppSettings { Profiles = _profiles.ToList(), Updates = _updates }; DialogResult = true;
        });
        AddButton(footer, "Cancel", () => DialogResult = false);
        _list.SelectedIndex = 0;
    }

    private void SaveLabel()
    {
        if (_list.SelectedItem is not AccountProfile p) return;
        var next = new AccountProfile { Id = p.Id, Name = string.IsNullOrWhiteSpace(_name.Text) ? p.Name : _name.Text.Trim(), ReadOnly = _readOnly.IsChecked == true, Connections = p.Connections };
        _profiles[_profiles.IndexOf(p)] = next; _list.SelectedItem = next;
    }
    private static void AddButton(Panel panel, string label, Action action)
    { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 7, 12, 7) }; button.Click += (_, _) => action(); panel.Children.Add(button); }
}
