using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ShanksitoTweaker.Core;

namespace ShanksitoTweaker;

public sealed class MainWindow : Window
{
    private const int DarkTitleBar = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow(AppState state)
    {
        Title = "Shanksito Tweaker";
        Width = 1240;
        Height = 860;
        MinWidth = 1020;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.Resources["Bg"];
        SourceInitialized += (_, _) =>
        {
            var on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DarkTitleBar, ref on, sizeof(int));
        };

        var shell = new Shell(state);
        Content = shell;
        shell.ShowLoading();
        GameWatcher? watcher = null;
        Loaded += async (_, _) =>
        {
            await Task.Run(state.Scan);
            shell.Show("home");
            watcher = new GameWatcher(state.Sys, report => Dispatcher.Invoke(() => shell.OnMatchFinished(report)))
            {
                // The game gets the whole GPU back.
                GameStarted = state.Server.Stop,
            };
            watcher.Start();
        };
        Closed += (_, _) =>
        {
            shell.StopMonitor();
            watcher?.Dispose();
            state.Server.Dispose();
        };
    }
}
