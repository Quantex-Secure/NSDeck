using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NSDeck.Core.Models;
using NSDeck.Core.Services;

namespace NSDeck.Desktop.Dialogs;

public sealed class BestPracticesWindow : Window
{
    private readonly string _domain;
    private readonly IReadOnlyList<DnsRecord> _records;
    private readonly Func<IReadOnlyList<DnsRecord>, ZoneValidationResult> _validate;
    private readonly ComboBox _template = new() { ItemsSource = new[] { new KeyValuePair<DnsSetupTemplate, string>(DnsSetupTemplate.WebsiteAddress, "Website address (A / AAAA)"), new(DnsSetupTemplate.WebsiteAlias, "Website alias (CNAME)"), new(DnsSetupTemplate.MailExchanger, "Mail exchanger (MX)"), new(DnsSetupTemplate.Spf, "Authorized email senders (SPF)"), new(DnsSetupTemplate.DmarcMonitoring, "Email monitoring (DMARC)"), new(DnsSetupTemplate.Dkim, "Email signing record (DKIM)"), new(DnsSetupTemplate.Caa, "Approved certificate authority (CAA)"), new(DnsSetupTemplate.NoMail, "Domain with no email") }, DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedIndex = 0, MinWidth = 170 };
    private readonly TextBox _name = new() { Text = "@" };
    private readonly TextBox _value = new();
    private readonly TextBox _ttl = new() { Text = "3600" };
    private readonly TextBox _priority = new() { Text = "10" };
    private readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
    private readonly CheckBox _noMail = new() { Content = "I confirm this domain neither sends nor receives email.", Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 105 };
    private IReadOnlyList<DnsRecord>? _proposed;
    private readonly Button _stage = new() { Content = "Stage reviewed records", IsEnabled = false, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0) };
    private readonly bool _readOnly;
    public IReadOnlyList<DnsRecord>? Result { get; private set; }

    public BestPracticesWindow(string domain, string target, IReadOnlyList<DnsRecord> records, bool readOnly, Func<IReadOnlyList<DnsRecord>, ZoneValidationResult> validate)
    {
        _domain = domain; _records = records; _readOnly = readOnly; _validate = validate;
        Title = "DNS best practices — " + domain; Width = 1100; Height = 900; MinWidth = 850; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Best-practice scanner & guided setup", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = target, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Scans the loaded zone, including staged edits. Findings are guidance, not a security certification. No public DNS queries are sent. Inheritance, recursive SPF lookups, DNSSEC, delegation, and actual mail delivery need separate verification.", TextWrapping = TextWrapping.Wrap });
        var findings = DnsBestPracticeScanner.Scan(domain, records);
        panel.Children.Add(new TextBlock { Text = $"{findings.Count(f => f.Severity == DnsFindingSeverity.Critical)} critical · {findings.Count(f => f.Severity == DnsFindingSeverity.Warning)} warnings · {findings.Count(f => f.Severity == DnsFindingSeverity.Information)} informational", Margin = new Thickness(0, 10, 0, 8) });
        var grid = new DataGrid { ItemsSource = findings, AutoGenerateColumns = false, IsReadOnly = true, Height = 280, RowHeight = double.NaN, MinRowHeight = 64, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Severity", Binding = new Binding("Severity"), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Finding", Binding = new Binding("Title"), Width = 220 });
        var wrapping = new Style(typeof(TextBlock)); wrapping.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        grid.Columns.Add(new DataGridTextColumn { Header = "Why it matters / next step", Binding = new Binding("Explanation"), Width = 650, ElementStyle = wrapping });
        grid.Columns[1].Width = 260;
        ((DataGridTextColumn)grid.Columns[1]).ElementStyle = wrapping;
        grid.SizeChanged += (_, e) => grid.Columns[2].Width = Math.Max(350, e.NewSize.Width - 400);
        panel.Children.Add(grid); grid.SelectedIndex = findings.Count > 0 ? 0 : -1;
        var source = new Button { Content = "Read the selected finding’s source", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 7, 0, 12) };
        source.Click += (_, _) => { if (grid.SelectedItem is DnsPracticeFinding finding) try { Process.Start(new ProcessStartInfo(finding.SourceUrl) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        panel.Children.Add(source);
        var setup = new Button { Content = "Set up selected finding", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        setup.Click += (_, _) =>
        {
            if (grid.SelectedItem is not DnsPracticeFinding finding) return;
            _template.SelectedValue = finding.Rule.Split('-')[0] switch
            { "SPF" => DnsSetupTemplate.Spf, "DMARC" => DnsSetupTemplate.DmarcMonitoring, "DKIM" => DnsSetupTemplate.Dkim, "CAA" => DnsSetupTemplate.Caa, "MX" => DnsSetupTemplate.MailExchanger, _ => DnsSetupTemplate.WebsiteAddress };
            _template.BringIntoView();
        };
        panel.Children.Add(setup);
        panel.Children.Add(new TextBlock { Text = "Create records with guided setup", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_help);
        var fields = new Grid(); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); fields.ColumnDefinitions.Add(new ColumnDefinition());
        void Field(string label, Control control)
        {
            var row = fields.RowDefinitions.Count; fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
            control.Margin = new Thickness(0, 0, 0, 6); Grid.SetRow(text, row); Grid.SetRow(control, row); Grid.SetColumn(control, 1); fields.Children.Add(text); fields.Children.Add(control);
        }
        Field("Template", _template); Field("Record name / selector", _name); Field("Address, target, or policy", _value); Field("TTL (seconds)", _ttl); Field("MX priority", _priority); panel.Children.Add(fields); panel.Children.Add(_noMail); panel.Children.Add(_preview);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(buttons);
        var preview = new Button { Content = "Preview records", Padding = new Thickness(14, 8, 14, 8) }; preview.Style = TryFindResource("ToolbarButtonStyle") as Style; _stage.Style = TryFindResource("PrimaryButtonStyle") as Style; buttons.Children.Add(preview); buttons.Children.Add(_stage);
        var close = new Button { Content = "Close", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0) }; close.Click += (_, _) => DialogResult = false; buttons.Children.Add(close);
        preview.Click += (_, _) => Preview();
        _stage.Click += (_, _) => { if (_proposed is not null && !_readOnly) { Result = _proposed; DialogResult = true; } };
        void Invalidate() { _stage.IsEnabled = false; _proposed = null; _preview.Text = "Preview again after changing the setup values."; }
        foreach (var box in new[] { _name, _value, _ttl, _priority }) box.TextChanged += (_, _) => Invalidate();
        _noMail.Checked += (_, _) => Invalidate(); _noMail.Unchecked += (_, _) => Invalidate();
        _template.SelectionChanged += (_, _) => { Invalidate(); UpdateHelp(); };
        var scroller = (ScrollViewer)Content;
        var shell = new DockPanel(); Content = shell;
        panel.Children.Remove(buttons); buttons.Margin = new Thickness(24, 12, 24, 18);
        DockPanel.SetDock(buttons, Dock.Bottom); shell.Children.Add(buttons); shell.Children.Add(scroller);
        UpdateHelp();
    }

    private void Preview()
    {
        try
        {
            _stage.IsEnabled = false; _proposed = null;
            if (!int.TryParse(_ttl.Text, out var ttl) || !int.TryParse(_priority.Text, out var priority)) throw new InvalidOperationException("TTL and MX priority must be whole numbers.");
            var proposed = DnsBestPracticeScanner.Create((DnsSetupTemplate)_template.SelectedValue, _domain, _name.Text, _value.Text, ttl, priority, _noMail.IsChecked == true);
            var desired = DnsBestPracticeScanner.Stage(_records, proposed);
            var validation = _validate(desired);
            if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
            _preview.Text = string.Join(Environment.NewLine, proposed.Select(r => $"ADD  {r.Name}  {r.Type}  {r.Priority?.ToString() ?? ""} {r.Value}  (TTL {r.TtlSeconds})")) +
                Environment.NewLine + (_readOnly ? "Read-only account: preview is available; staging is disabled." : "These records will be staged locally. Review the main window’s changes and click Apply to publish.");
            _proposed = proposed; _stage.IsEnabled = !_readOnly;
            _preview.BringIntoView();
        }
        catch (Exception ex) { _preview.Text = ex.Message; _preview.BringIntoView(); }
    }

    private void UpdateHelp()
    {
        var template = (DnsSetupTemplate)_template.SelectedValue;
        _noMail.Visibility = template == DnsSetupTemplate.NoMail ? Visibility.Visible : Visibility.Collapsed;
        _priority.IsEnabled = template == DnsSetupTemplate.MailExchanger;
        _value.IsEnabled = template != DnsSetupTemplate.NoMail;
        _name.IsEnabled = template is not (DnsSetupTemplate.NoMail or DnsSetupTemplate.DmarcMonitoring);
        _help.Text = template switch
        {
            DnsSetupTemplate.WebsiteAddress => "Enter the host name (@ for the apex) and the IPv4 or IPv6 address supplied by your hosting service.",
            DnsSetupTemplate.WebsiteAlias => "Enter a host such as www and the canonical host supplied by your website service. Apex CNAME setup requires provider-specific flattening and is not generated here.",
            DnsSetupTemplate.MailExchanger => "Use your mail provider’s exact MX host and priority. Add each required exchanger separately.",
            DnsSetupTemplate.Spf => "Paste one complete v=spf1 policy covering all authorized sending services. Existing policies must be edited, not duplicated. Nested DNS lookup limits need separate verification.",
            DnsSetupTemplate.DmarcMonitoring => "Enter a working aggregate-report email address. Generates p=none at _dmarc; review reports and alignment before enforcement. An external reporting destination requires its operator’s DNS authorization.",
            DnsSetupTemplate.Dkim => "Enter the exact selector._domainkey name and TXT public-key policy or CNAME target supplied by your mail provider. NSDeck never creates a signing key or guesses a selector.",
            DnsSetupTemplate.Caa => "Enter the CA domain approved for certificate issuance. Check your CDN and automated renewal services before adding a restriction. This template creates an issue record.",
            _ => "Only for a domain that neither sends nor receives email: generates null MX, SPF -all, and DMARC reject. Existing conflicting policies or mail routing must be reviewed explicitly first."
        };
    }
}
