using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ShanksitoTweaker.Core;
using static ShanksitoTweaker.Ui.W;

namespace ShanksitoTweaker.Ui;

public sealed class InstallerWindow : Window
{
    public InstallerWindow(ISystemApi sys)
    {
        Title = $"Installer {Installer.Product}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B("Bg");

        var defaults = Installer.DefaultOptions(true);
        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        brand.Children.Add(Icon("\uEB42", 24, "Accent").With(i => i.Margin = new Thickness(0, 2, 10, 0)));
        brand.Children.Add(Text(Installer.Product, 24, "Text", true));
        root.Children.Add(brand);
        root.Children.Add(Text($"Version {Installer.Version} · Optimiseur Valorant", 12.5, "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 16)));

        root.Children.Add(Text("Dossier d'installation", 12.5, "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 4)));
        var folder = new TextBox { Text = defaults.Folder, Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(folder);

        CheckBox Option(string label)
        {
            var box = new CheckBox { Style = (Style)Application.Current.Resources["Switch"], IsChecked = true };
            DockPanel.SetDock(box, Dock.Right);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(box);
            row.Children.Add(Text(label, 13).With(t => t.VerticalAlignment = VerticalAlignment.Center));
            root.Children.Add(row);
            return box;
        }

        var desktop = Option("Raccourci sur le bureau");
        var launch = Option("Ouvrir après l'installation");
        root.Children.Add(Card(Text(Installer.Notice, 12, "Muted")).With(c => c.Margin = new Thickness(0, 8, 0, 12)));

        var status = Text("", 12.5, "Warn").With(t => t.Margin = new Thickness(0, 0, 0, 10));
        root.Children.Add(status);
        var install = new Button { Content = "Installer", Style = (Style)Application.Current.Resources["Primary"], Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Annuler" };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(install);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        install.Click += async (_, _) =>
        {
            var path = folder.Text.Trim();
            if (!Path.IsPathFullyQualified(path))
            {
                status.Text = "Choisis un dossier complet, par exemple C:\\Program Files\\Shanksito Tweaker.";
                return;
            }
            install.IsEnabled = false;
            cancel.IsEnabled = false;
            status.Text = "Installation…";
            var options = defaults with { Folder = path, DesktopFolder = desktop.IsChecked == true ? Installer.DefaultOptions(true).DesktopFolder : null };
            try
            {
                var source = Environment.ProcessPath!;
                var exe = await Task.Run(() => Installer.Install(sys, source, options));
                if (launch.IsChecked == true) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                status.Text = ex is IOException
                    ? "Fichier occupé : ferme Shanksito Tweaker puis réessaie."
                    : $"Installation impossible : {ex.Message}";
                install.IsEnabled = true;
                cancel.IsEnabled = true;
            }
        };
    }
}
