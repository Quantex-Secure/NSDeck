using System.IO;
using System.Windows.Threading;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Storage;
using NSDeck.Desktop.Services;
using NSDeck.Desktop.ViewModels;

namespace NSDeck.Desktop.Tests;

public sealed class DesktopSafetyTests
{
    [Fact]
    public Task Failed_provider_switch_keeps_the_original_write_target() => Sta(async directory =>
    {
        using var vm = Create(directory);
        var first = new Provider("first"); var second = new Provider("second") { FailRead = true };
        var one = new DomainSummary("example.com", "first"); var two = new DomainSummary("example.com", "second");
        vm.Accounts.Add(new(first, [one])); vm.Accounts.Add(new(second, [two]));
        await vm.SelectDomainAsync(one);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.SelectDomainAsync(two));
        Assert.Equal(one, vm.SelectedDomain); Assert.Equal("first", vm.ProviderDisplay);
        vm.AddRecord(new() { Name = "new", Type = "A", Value = "198.51.100.1", TtlSeconds = 300 });
        await vm.ApplyChangesAsync(); Assert.Equal(1, first.Writes); Assert.Equal(0, second.Writes);
    });

    [Fact]
    public Task Encrypted_drafts_restore_only_for_the_matching_target() => Sta(async directory =>
    {
        var provider = new Provider("first"); var zone = new DomainSummary("example.com", "first");
        using (var vm = Create(directory))
        {
            vm.Accounts.Add(new(provider, [zone])); await vm.SelectDomainAsync(zone);
            vm.AddRecord(new() { Name = "draft", Type = "TXT", Value = "private draft text", TtlSeconds = 600 });
        }
        var bytes = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(directory, "drafts"))));
        Assert.DoesNotContain("private draft text", System.Text.Encoding.UTF8.GetString(bytes));
        using var restored = Create(directory); restored.Accounts.Add(new(provider, [zone])); await restored.SelectDomainAsync(zone);
        Assert.Single(restored.PendingChanges); Assert.Contains(restored.GetCurrentRecords(), r => r.Name == "draft" && r.TtlSeconds == 600);
    });

    [Fact]
    public Task Read_only_accounts_allow_scanning_but_block_staging() => Sta(async directory =>
    {
        using var vm = Create(directory); using var provider = new AccountDnsProvider(new Provider("test"), "id", "Read only", true);
        var zone = new DomainSummary("example.com", provider.ProviderName); vm.Accounts.Add(new(provider, [zone])); await vm.SelectDomainAsync(zone);
        Assert.False(vm.CanEdit); Assert.NotEmpty(NSDeck.Core.Services.DnsBestPracticeScanner.Scan(zone.Name, vm.GetCurrentRecords()));
        Assert.Throws<InvalidOperationException>(() => vm.AddRecord(new() { Name = "www", Type = "A", Value = "192.0.2.3" }));
    });

    [Fact]
    public Task Cancelling_a_zone_read_preserves_current_selection() => Sta(async directory =>
    {
        using var vm = Create(directory); var first = new Provider("first"); var second = new Provider("second") { WaitForCancellation = true };
        var one = new DomainSummary("example.com", "first"); var two = new DomainSummary("example.net", "second");
        vm.Accounts.Add(new(first, [one])); vm.Accounts.Add(new(second, [two])); await vm.SelectDomainAsync(one);
        var pending = vm.SelectDomainAsync(two); Assert.True(vm.IsBusy); vm.CancelCurrentOperation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.False(vm.IsBusy); Assert.Equal(one, vm.SelectedDomain);
    });

    [Fact]
    public Task Selecting_an_empty_profile_does_not_connect_default_or_load_demo_and_survives_restart() => Sta(async directory =>
    {
        var settings = new AppSettings
        {
            ActiveProfileId = "second",
            Profiles = [new() { Id = "default", Name = "Default", Connections = new() { Cloudflare = new() { Enabled = true, Token = "" } } }, new() { Id = "second", Name = "Second" }]
        };
        using (var vm = Create(directory))
        {
            // Constructing the inactive provider would throw for its empty token.
            await vm.ConfigureAsync(settings);
            Assert.Equal("second", vm.ActiveProfileId); Assert.Equal("Second", vm.ProviderDisplay);
            Assert.Empty(vm.Accounts); Assert.Empty(vm.Domains); Assert.False(vm.IsDemoMode);
            Assert.Contains("Second has no enabled providers", vm.StatusMessage);
        }
        using var reopened = Create(directory); await reopened.InitializeAsync();
        Assert.Equal("second", reopened.ActiveProfileId); Assert.Empty(reopened.Accounts); Assert.False(reopened.IsDemoMode);
    });

    [Fact]
    public Task Switching_profiles_persists_selection_and_preserves_all_saved_profiles() => Sta(async directory =>
    {
        using var vm = Create(directory);
        await vm.ConfigureAsync(new AppSettings { ActiveProfileId = "one", Profiles = [new() { Id = "one", Name = "One" }, new() { Id = "two", Name = "Two" }] });
        await vm.SwitchProfileAsync("two");
        Assert.Equal("two", vm.ActiveProfileId); Assert.Contains("Two", vm.ConnectionStatus);
        var saved = await new SettingsStore(directory).LoadAsync();
        Assert.Equal("two", saved.ActiveProfileId); Assert.Equal(2, saved.Profiles.Count);
        await vm.SwitchProfileAsync("one"); Assert.Equal("one", vm.ActiveProfileId);
    });

    private static MainViewModel Create(string directory) => new(new SettingsStore(directory), new JsonZoneSnapshotStore(Path.Combine(directory, "snapshots")), new AuditLogService(directory));
    private static Task Sta(Func<string, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "NSDeckDesktopTests-" + Guid.NewGuid().ToString("N"));
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(directory); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private sealed class Provider(string name) : IDnsProvider
    {
        public string ProviderName => name;
        public bool FailRead { get; init; }
        public bool WaitForCancellation { get; init; }
        public int Writes { get; private set; }
        private IReadOnlyList<DnsRecord> _records = [new() { Name = "@", Type = "A", Value = "192.0.2.1", TtlSeconds = 300 }];
        public Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DomainSummary>>([new("example.com", name)]);
        public async Task<DnsZone> GetZoneAsync(string domain, CancellationToken ct = default)
        {
            if (FailRead) throw new InvalidOperationException("Provider unavailable.");
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            return new(domain, name, _records.Select(r => r.Clone()).ToArray(), DateTimeOffset.Now);
        }
        public Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken ct = default)
        { Writes++; _records = records.Select(r => r.Clone()).ToArray(); return Task.CompletedTask; }
    }
}
