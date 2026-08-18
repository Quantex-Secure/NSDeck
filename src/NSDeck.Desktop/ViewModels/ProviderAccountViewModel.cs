using System.Collections.ObjectModel;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;

namespace NSDeck.Desktop.ViewModels;

public sealed class ProviderAccountViewModel
{
    public ProviderAccountViewModel(IDnsProvider provider, IEnumerable<DomainSummary> domains)
    {
        Provider = provider;
        Name = provider.ProviderName;
        Initial = Name.Length == 0 ? "?" : Name[..1].ToUpperInvariant();
        Domains = new ObservableCollection<DomainSummary>(domains);
    }

    internal IDnsProvider Provider { get; }
    public string Name { get; }
    public string Initial { get; }
    public ObservableCollection<DomainSummary> Domains { get; }
}
