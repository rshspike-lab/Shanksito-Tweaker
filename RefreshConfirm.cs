using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShanksitoTweaker;

/// <summary>Same safety as Windows: without a click within 15 s the old refresh rate comes back.</summary>
public sealed class RefreshConfirm : Window
{
    public bool Kept { get; private set; }

    public RefreshConfirm(int hz)
    {
        Title = "Confirmer l'affichage";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = (Brush)Application.Current.Resources["Panel"];

        var left = 15;
        var text = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["Text"],
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 16),
            Text = $"Écran passé à {hz} Hz. Garder ? Retour automatique dans {left} s.",
        };
        var keep = new Button { Content = "Garder", Style = (Style)Application.Current.Resources["Primary"], Margin = new Thickness(0, 0, 8, 0) };
        var back = new Button { Content = "Revenir" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(keep);
        buttons.Children.Add(back);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(text);
        root.Children.Add(buttons);
        Content = root;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            left--;
            text.Text = $"Écran passé à {hz} Hz. Garder ? Retour automatique dans {left} s.";
            if (left <= 0) Close();
        };
        keep.Click += (_, _) => { Kept = true; Close(); };
        back.Click += (_, _) => Close();
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }
}
