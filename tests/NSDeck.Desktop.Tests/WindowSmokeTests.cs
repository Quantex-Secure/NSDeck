using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NSDeck.Core.Models;
using NSDeck.Core.Services;
using NSDeck.Desktop.Dialogs;
using NSDeck.Desktop.Services;
using NSDeck.Desktop.ViewModels;

namespace NSDeck.Desktop.Tests;

public sealed class WindowSmokeTests
{
    [Fact]
    public Task Scanner_previews_and_stages_and_editor_preserves_custom_ttl()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var app = new NSDeck.Desktop.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
                var records = new DnsRecord[] { new() { Name = "@", Type = "A", Value = "192.0.2.1", TtlSeconds = 600 } };
                var scanner = new BestPracticesWindow("example.com", "Demo / example.com", records, false, r => ZoneValidator.Validate(r));
                scanner.Loaded += (_, _) => scanner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Field<TextBox>(scanner, "_name").Text = "www"; Field<TextBox>(scanner, "_value").Text = "192.0.2.2";
                        var buttons = Descendants(scanner).OfType<Button>().ToArray();
                        buttons.Single(b => Equals(b.Content, "Preview records")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.True(Field<Button>(scanner, "_stage").IsEnabled);
                        Assert.Contains("192.0.2.2", Field<TextBox>(scanner, "_preview").Text);
                        Capture(scanner, "best-practices.png");
                        Field<Button>(scanner, "_stage").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { completion.TrySetException(ex); scanner.Close(); }
                }));
                scanner.ShowDialog(); Assert.Single(scanner.Result!);
                var editor = new RecordEditorWindow(records[0]);
                editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Assert.Equal("600", ((ComboBox)editor.FindName("TtlBox")).Text);
                        ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { completion.TrySetException(ex); editor.Close(); }
                }));
                editor.ShowDialog(); Assert.Equal(600, editor.Result!.TtlSeconds);
                var main = new NSDeck.Desktop.MainWindow(designPreview: true);
                main.Show(); main.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var profileSettings = new AppSettings { ActiveProfileId = "default", Profiles = [new() { Id = "default", Name = "Default" }, new() { Id = "test", Name = "Test profile" }] };
                var accounts = new AccountProfilesWindow(profileSettings);
                accounts.Loaded += (_, _) => accounts.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Field<ListBox>(accounts, "_list").SelectedIndex = 1;
                    Descendants(accounts).OfType<Button>().Single(b => Equals(b.Content, "Save and connect")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                accounts.ShowDialog(); Assert.Equal("test", accounts.Result!.ActiveProfileId);
                var reopened = new AccountProfilesWindow(accounts.Result);
                reopened.Show(); Assert.Equal("test", ((AccountProfile)Field<ListBox>(reopened, "_list").SelectedItem).Id); reopened.Close();
                var vm = (MainViewModel)main.DataContext;
                Pump(vm.ConfigureAsync(accounts.Result));
                var selector = (ComboBox)main.FindName("ProfileSelector");
                Assert.Equal("test", selector.SelectedValue);
                selector.SelectedValue = "default";
                Pump(WaitForIdle(vm));
                Assert.Equal("default", vm.ActiveProfileId); Assert.False(vm.IsDemoMode);
                selector.SelectedValue = "test";
                Pump(WaitForIdle(vm));
                Assert.Equal("test", vm.ActiveProfileId);
                Capture(main, "main-window.png"); main.Close();
                app.Shutdown(); completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static async Task WaitForIdle(MainViewModel vm)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (vm.IsBusy) await Task.Delay(10, deadline.Token);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject owner)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        { var child = VisualTreeHelper.GetChild(owner, index); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("NSDECK_REVIEW_CAPTURE_DIRECTORY"); if (string.IsNullOrWhiteSpace(directory)) return;
        window.UpdateLayout(); Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
