using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NSDeck.Desktop.Services;
using Microsoft.Win32;
using NSDeck.Providers.Windows;

namespace NSDeck.Desktop.Dialogs;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        NamecheapEnabled.IsChecked = settings.Namecheap.Enabled;
        NamecheapApiUser.Text = settings.Namecheap.ApiUser;
        NamecheapUserName.Text = settings.Namecheap.UserName;
        NamecheapApiKey.Password = settings.Namecheap.ApiKey;
        NamecheapClientIp.Text = settings.Namecheap.ClientIp;
        NamecheapSandbox.IsChecked = settings.Namecheap.UseSandbox;
        AzureEnabled.IsChecked = settings.Azure.Enabled;
        AzureSubscriptionId.Text = settings.Azure.SubscriptionId;
        AzureTenantId.Text = settings.Azure.TenantId;
        AzureClientId.Text = settings.Azure.ClientId;
        AzureClientSecret.Password = settings.Azure.ClientSecret;
        GoDaddyEnabled.IsChecked = settings.GoDaddy.Enabled;
        GoDaddyToken.Password = settings.GoDaddy.Token;
        CloudflareEnabled.IsChecked = settings.Cloudflare.Enabled;
        CloudflareToken.Password = settings.Cloudflare.Token;
        Route53Enabled.IsChecked = settings.Route53.Enabled;
        AwsAccessKeyId.Text = settings.Route53.AccessKeyId;
        AwsSecretAccessKey.Password = settings.Route53.SecretAccessKey;
        AwsSessionToken.Password = settings.Route53.SessionToken;
        GoogleEnabled.IsChecked = settings.Google.Enabled;
        GoogleProjectId.Text = settings.Google.ProjectId;
        GoogleCredentialsPath.Text = settings.Google.ServiceAccountJsonPath;
        WindowsDnsEnabled.IsChecked = settings.WindowsDns.Enabled;
        WindowsDnsServers.Text = settings.WindowsDns.Servers;
        WindowsDnsEndpointName.Text = string.IsNullOrWhiteSpace(settings.WindowsDns.EndpointName)
            ? "NSDeck.Dns"
            : settings.WindowsDns.EndpointName;
        WindowsDnsPublicAuthoritative.IsChecked = settings.WindowsDns.SupportsPublicDnsPropagation;
        UpdatesAutomatic.IsChecked = settings.Updates.CheckAutomatically;
        UpdateManifestUrl.Text = settings.Updates.ManifestUrl;
    }

    public AppSettings? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (NamecheapEnabled.IsChecked == true)
        {
            if (AnyBlank(NamecheapApiUser.Text, NamecheapUserName.Text, NamecheapApiKey.Password, NamecheapClientIp.Text))
            { Warn("Namecheap requires the API user, account username, API key, and whitelisted public IPv4 address."); return; }
            if (!IPAddress.TryParse(NamecheapClientIp.Text.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            { Warn("Enter a valid public IPv4 address for Namecheap."); return; }
        }
        if (AzureEnabled.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(AzureSubscriptionId.Text)) { Warn("Azure DNS requires a subscription ID."); return; }
            var azureCredentialCount = new[] { AzureTenantId.Text, AzureClientId.Text, AzureClientSecret.Password }.Count(value => !string.IsNullOrWhiteSpace(value));
            if (azureCredentialCount is > 0 and < 3) { Warn("Supply all Azure service-principal fields, or leave all three blank to use your existing Azure sign-in."); return; }
        }
        if (GoDaddyEnabled.IsChecked == true && string.IsNullOrWhiteSpace(GoDaddyToken.Password)) { Warn("GoDaddy requires a Personal Access Token."); return; }
        if (CloudflareEnabled.IsChecked == true && string.IsNullOrWhiteSpace(CloudflareToken.Password)) { Warn("Cloudflare requires an API token."); return; }
        if (Route53Enabled.IsChecked == true && AnyBlank(AwsAccessKeyId.Text, AwsSecretAccessKey.Password)) { Warn("Route 53 requires an access key ID and secret access key."); return; }
        if (GoogleEnabled.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(GoogleProjectId.Text)) { Warn("Google Cloud DNS requires a project ID."); return; }
            if (!string.IsNullOrWhiteSpace(GoogleCredentialsPath.Text) && !File.Exists(GoogleCredentialsPath.Text.Trim())) { Warn("The selected Google service-account JSON file does not exist."); return; }
        }
        if (WindowsDnsEnabled.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(WindowsDnsServers.Text)) { Warn("Windows DNS requires at least one DNS server name."); return; }
            if (string.IsNullOrWhiteSpace(WindowsDnsEndpointName.Text)) { Warn("Windows DNS requires the JEA endpoint name."); return; }
        }
        if (UpdatesAutomatic.IsChecked == true &&
            (!Uri.TryCreate(UpdateManifestUrl.Text.Trim(), UriKind.Absolute, out var updateUri) || updateUri.Scheme != Uri.UriSchemeHttps))
        { Warn("Automatic update checks require a valid HTTPS manifest address."); return; }

        Result = new AppSettings
        {
            Namecheap = new NamecheapConnectionSettings { Enabled = NamecheapEnabled.IsChecked == true, ApiUser = NamecheapApiUser.Text.Trim(), UserName = NamecheapUserName.Text.Trim(), ApiKey = NamecheapApiKey.Password.Trim(), ClientIp = NamecheapClientIp.Text.Trim(), UseSandbox = NamecheapSandbox.IsChecked == true },
            Azure = new AzureConnectionSettings { Enabled = AzureEnabled.IsChecked == true, SubscriptionId = AzureSubscriptionId.Text.Trim(), TenantId = AzureTenantId.Text.Trim(), ClientId = AzureClientId.Text.Trim(), ClientSecret = AzureClientSecret.Password.Trim() },
            GoDaddy = new TokenConnectionSettings { Enabled = GoDaddyEnabled.IsChecked == true, Token = GoDaddyToken.Password.Trim() },
            Cloudflare = new TokenConnectionSettings { Enabled = CloudflareEnabled.IsChecked == true, Token = CloudflareToken.Password.Trim() },
            Route53 = new AwsConnectionSettings { Enabled = Route53Enabled.IsChecked == true, AccessKeyId = AwsAccessKeyId.Text.Trim(), SecretAccessKey = AwsSecretAccessKey.Password.Trim(), SessionToken = AwsSessionToken.Password.Trim() },
            Google = new GoogleConnectionSettings { Enabled = GoogleEnabled.IsChecked == true, ProjectId = GoogleProjectId.Text.Trim(), ServiceAccountJsonPath = GoogleCredentialsPath.Text.Trim() },
            WindowsDns = new WindowsDnsConnectionSettings { Enabled = WindowsDnsEnabled.IsChecked == true, Servers = WindowsDnsServers.Text.Trim(), EndpointName = WindowsDnsEndpointName.Text.Trim(), SupportsPublicDnsPropagation = WindowsDnsPublicAuthoritative.IsChecked == true },
            Updates = new UpdateSettings { CheckAutomatically = UpdatesAutomatic.IsChecked == true, ManifestUrl = UpdateManifestUrl.Text.Trim() }
        };
        DialogResult = true;
    }

    private void BrowseGoogleCredentials_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select Google service-account JSON", Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) GoogleCredentialsPath.Text = dialog.FileName;
    }

    private async void TestWindowsDns_Click(object sender, RoutedEventArgs e)
    {
        var servers = ParseServers(WindowsDnsServers.Text);
        if (servers.Count == 0) { Warn("Enter at least one Windows DNS server name first."); return; }
        if (string.IsNullOrWhiteSpace(WindowsDnsEndpointName.Text)) { Warn("Enter the JEA endpoint name first."); return; }

        TestWindowsDns.IsEnabled = false;
        WindowsDnsTestStatus.Text = "Testing the constrained endpoint with your current Windows account…";
        try
        {
            var results = new List<string>();
            foreach (var server in servers)
            {
                var provider = new WindowsDnsProvider(new WindowsDnsOptions(server, WindowsDnsEndpointName.Text.Trim()));
                var zones = await provider.GetDomainsAsync();
                results.Add($"{server}: {zones.Count} zone{(zones.Count == 1 ? string.Empty : "s")}");
            }
            WindowsDnsTestStatus.Text = $"Connected successfully — {string.Join("; ", results)}";
        }
        catch (Exception exception)
        {
            WindowsDnsTestStatus.Text = "Connection failed.";
            MessageBox.Show(this, exception.Message, "Windows DNS connection test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestWindowsDns.IsEnabled = true;
        }
    }

    private void SaveJeaSetupScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Windows DNS JEA setup script",
            FileName = "Install-NSDeckJea.ps1",
            DefaultExt = ".ps1",
            Filter = "PowerShell scripts (*.ps1)|*.ps1|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        using var source = typeof(SettingsWindow).Assembly.GetManifestResourceStream("NSDeck.Install-NSDeckJea.ps1")
            ?? throw new InvalidOperationException("The embedded JEA setup script was not found.");
        using var destination = File.Create(dialog.FileName);
        source.CopyTo(destination);
        WindowsDnsTestStatus.Text = $"Setup script saved to {dialog.FileName}";
    }

    private void OpenSetupPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Warn($"Windows could not open the setup page.\n\n{exception.Message}");
        }
    }

    private static bool AnyBlank(params string[] values) => values.Any(string.IsNullOrWhiteSpace);
    private static IReadOnlyList<string> ParseServers(string value) => value
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private void Warn(string message) => MessageBox.Show(this, message, "Incomplete provider settings", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
