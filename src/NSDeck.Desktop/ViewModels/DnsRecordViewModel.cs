using NSDeck.Core.Models;

namespace NSDeck.Desktop.ViewModels;

public sealed class DnsRecordViewModel(DnsRecord model) : ObservableObject
{
    private string _status = "Unchanged";

    public DnsRecord Model { get; } = model;
    public Guid LocalId => Model.LocalId;
    public string Name => Model.Name;
    public string Type => Model.Type;
    public string Value => Model.Value;
    public int TtlSeconds => Model.TtlSeconds;
    public string TtlDisplay => FormatTtl(Model.TtlSeconds);
    public int? Priority => Model.Priority;
    public string PriorityDisplay => Model.Priority?.ToString() ?? "—";

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public void RefreshBindings()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(TtlSeconds));
        OnPropertyChanged(nameof(TtlDisplay));
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(PriorityDisplay));
    }

    private static string FormatTtl(int seconds) => seconds switch
    {
        60 => "1 min",
        300 => "5 min",
        1200 => "20 min",
        1800 => "30 min",
        3600 => "60 min",
        _ => $"{seconds} sec"
    };
}
