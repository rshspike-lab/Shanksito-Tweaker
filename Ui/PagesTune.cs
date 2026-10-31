using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ShanksitoTweaker.Core;
using ShanksitoTweaker.Ui;
using static ShanksitoTweaker.Ui.W;

namespace ShanksitoTweaker;

public sealed partial class Shell
{
    private UIElement WindowsPage()
    {
        var page = W.Page("Windows", "Chaque interrupteur sauvegarde la valeur d'origine. Les effets annoncés sont des ordres de grandeur : mesure dans Performances.");
        var restore = Btn("Point de restauration", CreateRestorePoint);
        restore.IsEnabled = _state.Admin;
        var all = Btn("Appliquer le conseillé", () => ApplyAll(_state.Data.Windows, "windows"), true);
        all.IsEnabled = _state.Admin;
        page.Children.Add(Toolbar(all, restore));
        foreach (var group in _state.Data.Windows.GroupBy(t => t.Group))
        {
            page.Children.Add(Heading(group.Key));
            foreach (var tweak in group) page.Children.Add(TweakRow(tweak));
        }
        return page;
    }

    private async void CreateRestorePoint()
    {
        SetStatus("Création du point de restauration…");
        var result = await Task.Run(() => _state.Sys.Run("powershell",
            CommandTweak.Ps("Checkpoint-Computer -Description 'Shanksito Tweaker' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop")));
        SetStatus(result.Code == 0 ? "Point de restauration créé." : "Point non créé : protection système coupée, ou déjà un point aujourd'hui.");
    }

    private UIElement TweakListPage(string title, string intro, List<Tweak> tweaks, string bulk, string pageKey, bool userScope)
    {
        var page = W.Page(title, intro);
        var all = Btn(bulk, () => ApplyAll(tweaks, pageKey), true);
        all.IsEnabled = _state.Admin || userScope;
        page.Children.Add(Toolbar(all));
        if (tweaks.Count == 0) page.Children.Add(Text("Rien à afficher ici.", 13, "Muted"));
        foreach (var tweak in tweaks.OrderByDescending(t => t.Recommended && !t.IsApplied(_state.Sys)).ThenBy(t => t.Title))
            page.Children.Add(TweakRow(tweak));
        return page;
    }

    private UIElement DisplayPage()
    {
        var page = W.Page("Affichage", "Valorant tourne sur l'écran marqué « jeu ». Après un changement, retour automatique en 15 s sans confirmation.");
        foreach (var display in _state.Data.Displays)
        {
            var isGame = display == _state.Data.GameDisplay;
            var row = new DockPanel();
            var combo = new ComboBox { Width = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            foreach (var rate in display.Rates.OrderDescending())
                combo.Items.Add(new ComboBoxItem { Content = $"{rate} Hz", Tag = rate });
            combo.SelectedIndex = display.Rates.Count == 0 ? -1 : 0;
            var apply = Btn("Appliquer", () =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: int hz }) ChangeRefresh(display, hz);
            }, display.Current < display.Max);
            apply.IsEnabled = _state.Admin;
            apply.Margin = new Thickness(0);
            DockPanel.SetDock(apply, Dock.Right);
            DockPanel.SetDock(combo, Dock.Right);
            row.Children.Add(apply);
            row.Children.Add(combo);
            var stack = new StackPanel();
            stack.Children.Add(Text($"{display.Model}{(isGame ? "  (jeu)" : "")}", 14, "Text", true));
            stack.Children.Add(Text($"{display.Width}x{display.Height}, {display.Current} Hz actuellement, {display.Max} Hz maximum", 12.5, display.Current < display.Max ? "Warn" : "Muted"));
            row.Children.Add(stack);
            page.Children.Add(Card(row));
        }
        return page;
    }

    private void ChangeRefresh(DisplayInfo display, int hz)
    {
        var previous = display.Current;
        if (!Displays.SetRefresh(display.Device, hz))
        {
            SetStatus($"Windows a refusé {hz} Hz sur {display.Model}.");
            return;
        }
        var confirm = new RefreshConfirm(hz);
        confirm.ShowDialog();
        if (!confirm.Kept)
        {
            Displays.SetRefresh(display.Device, previous);
            SetStatus($"{display.Model} revenu à {previous} Hz.");
        }
        else
        {
            SetStatus($"{display.Model} réglé à {hz} Hz.");
        }
        Rescan("display");
    }

    private UIElement NetworkPage()
    {
        var d = _state.Data;
        var page = W.Page("Réseau et ping", "Les tests montrent où se perd le temps. Ils mesurent et ne changent rien.");

        var run = Btn("Tester le ping", () => { }, true);
        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            SetStatus("Test du ping, environ 10 secondes…");
            await Task.Run(_state.TestNetwork);
            _state.Route = await Task.Run(() => NetLab.Trace(_state.GameHost));
            Show("network");
            SetStatus("Test terminé.");
        };
        var bloat = Btn("Tester sous charge", () => { });
        bloat.Click += async (_, _) =>
        {
            if (GameWatcher.GameRunning())
            {
                SetStatus("Pas pendant une partie : ce test charge toute la ligne.");
                return;
            }
            if (MessageBox.Show("Ce test sature ta connexion pendant environ 15 secondes et télécharge plusieurs centaines de Mo. Continuer ?", "Shanksito Tweaker", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            bloat.IsEnabled = false;
            try
            {
                _state.Data.Bloat = await NetLab.Bufferbloat(text => Dispatcher.Invoke(() => SetStatus(text)));
                Show("network");
                SetStatus("Test sous charge terminé.");
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException)
            {
                bloat.IsEnabled = true;
                SetStatus("Test impossible : serveur de mesure injoignable.");
            }
        };
        page.Children.Add(Toolbar(run, bloat));

        if (d.Net is { } net)
        {
            var kind = net.WiFi ? "Wi-Fi" : "Ethernet";
            page.Children.Add(Text($"{kind} · {net.SpeedMbps} Mbps · {net.Description}{(net.Vpn is null ? "" : " · VPN : " + net.Vpn)}", 13, net.WiFi || net.Vpn is not null ? "Warn" : "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 12)));
        }
        else
        {
            page.Children.Add(Text("Aucune connexion active détectée.", 13, "Warn").With(t => t.Margin = new Thickness(0, 0, 0, 12)));
        }

        if (d.NetTest is { } test)
        {
            var tiles = new UniformGrid { Columns = 3 };
            foreach (var probe in new[] { test.Gateway, test.Internet, test.GameRegion }.OfType<Probe>())
                tiles.Children.Add(Tile(probe.Label, probe.Reached ? $"{probe.Avg:0.#} ms" : "Sans réponse", probe.Reached ? "Text" : "Warn",
                    probe.Reached ? $"écart {probe.Jitter:0.#} ms · perte {probe.LossPct:0} %" : ""));
            page.Children.Add(tiles);
            page.Children.Add(Card(Text(test.Verdict, 14, "Text", true), test.Healthy ? "Good" : "Warn"));
        }

        if (d.Bloat is { } result)
        {
            page.Children.Add(Heading("Ping sous charge (bufferbloat)"));
            var tiles = new UniformGrid { Columns = 4 };
            tiles.Children.Add(Tile("Note", result.Grade, result.Grade is "A+" or "A" ? "Good" : result.Grade == "B" ? "Warn" : "Accent"));
            tiles.Children.Add(Tile("Au repos", $"{result.IdleMs:0} ms"));
            tiles.Children.Add(Tile("En réception", $"{result.DownMs:0} ms", "Text", $"{result.DownMbps:0} Mbps"));
            tiles.Children.Add(Tile("En envoi", $"{result.UpMs:0} ms", "Text", $"{result.UpMbps:0} Mbps"));
            page.Children.Add(tiles);
            page.Children.Add(Card(Text(result.Verdict, 13.5), result.Grade is "A+" or "A" ? "Good" : "Warn"));
        }

        if (_state.Route is { Count: > 0 } route)
        {
            page.Children.Add(Heading("Trajet jusqu'aux serveurs"));
            var jump = NetLab.BiggestJump(route);
            // Below 10 ms a jump is ordinary line delay, not something worth pointing at.
            if (jump is not null && jump.Ms - route.TakeWhile(h => h != jump).Where(h => h.Ms >= 0).Select(h => h.Ms).LastOrDefault() < 10) jump = null;
            var table = new StackPanel();
            foreach (var hop in route)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var ms = Text(hop.Ms < 0 ? "pas de réponse" : $"{hop.Ms:0.#} ms", 12.5, hop == jump ? "Warn" : "Text");
                DockPanel.SetDock(ms, Dock.Right);
                row.Children.Add(ms);
                row.Children.Add(Text($"{hop.Number}.  {(hop.Address.Length == 0 ? "routeur silencieux" : hop.Address)}", 12.5, "Muted"));
                table.Children.Add(row);
            }
            table.Children.Add(Text(jump is null ? "Aucun gros saut sur le trajet. Les relais silencieux sont normaux." : $"Plus gros saut au relais {jump.Number} : le temps se perd là, pas sur ton PC.", 12, "Muted").With(t => t.Margin = new Thickness(0, 8, 0, 0)));
            page.Children.Add(Card(table));
        }

        page.Children.Add(Heading("Tweaks réseau"));
        foreach (var tweak in d.Network) page.Children.Add(TweakRow(tweak));

        page.Children.Add(Heading("Pour le ping, ce qui compte vraiment"));
        foreach (var (title, detail) in new[]
        {
            ("Câble Ethernet plutôt que Wi-Fi", "Le plus gros gain possible, surtout sur les variations de ping."),
            ("VPN coupé pendant la partie", "Un VPN rallonge le trajet. Les filtres réseau se coupent dans Services."),
            ("Rien qui envoie en fond", "Sauvegarde cloud, torrent, mise à jour : la Session de jeu ferme MEGAsync, OneDrive et Steam."),
            ("Serveur le plus proche dans Valorant", "La distance fixe le ping minimum. Aucun tweak ne la raccourcit."),
            ("DNS et TCP : aucun effet en partie", "Le DNS ne sert qu'à se connecter, et Valorant joue en UDP. Les « boosters de ping » ne changent rien ici."),
        })
            page.Children.Add(Card(Line(title, detail)));
        return page;
    }
}
