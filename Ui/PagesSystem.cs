using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ShanksitoTweaker.Core;
using ShanksitoTweaker.Ui;
using static ShanksitoTweaker.Ui.W;

namespace ShanksitoTweaker;

public sealed partial class Shell
{
    private UIElement CleanPage()
    {
        var page = W.Page("Nettoyage", "Seulement des dossiers jetables connus. Rien ne part sans ton clic et la suppression est définitive.");
        var list = new StackPanel();
        list.Children.Add(Text("Calcul des tailles…", 13, "Muted"));
        var boxes = new List<(CheckBox Box, CleanMeasure Measure)>();
        var total = Text("", 13, "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 8));

        void Fill(List<CleanMeasure> measures)
        {
            list.Children.Clear();
            boxes.Clear();
            foreach (var measure in measures)
            {
                var box = new CheckBox { Style = (Style)Application.Current.Resources["Switch"], IsChecked = measure.Target.Default && measure.Bytes > 0 };
                box.IsEnabled = measure.Bytes > 0;
                boxes.Add((box, measure));
                DockPanel.SetDock(box, Dock.Right);
                var size = Text(Size(measure.Bytes), 14, "Text", true).With(t =>
                {
                    t.Margin = new Thickness(16, 0, 16, 0);
                    t.VerticalAlignment = VerticalAlignment.Center;
                });
                DockPanel.SetDock(size, Dock.Right);
                var row = new DockPanel();
                row.Children.Add(box);
                row.Children.Add(size);
                row.Children.Add(Line(measure.Target.Title, measure.Target.Why));
                list.Children.Add(Card(row));
            }
            total.Text = $"Récupérable au total : {Size(measures.Sum(m => m.Bytes))}";
        }

        var clean = Btn("Nettoyer la sélection", () => { }, true);
        clean.Click += async (_, _) =>
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Measure).ToList();
            if (chosen.Count == 0) return;
            var summary = string.Join("\n", chosen.Select(m => $"• {m.Target.Title} : {Size(m.Bytes)}"));
            if (MessageBox.Show($"Supprimer définitivement ?\n\n{summary}", "Shanksito Tweaker", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            clean.IsEnabled = false;
            SetStatus("Nettoyage…");
            var results = await Task.Run(() => chosen.Select(m => Cleaner.Clean(m.Target)).ToList());
            Show("clean");
            var skipped = results.Sum(r => r.Skipped);
            SetStatus($"{Size(results.Sum(r => r.Freed))} libérés{(skipped > 0 ? $", {skipped} fichiers utilisés ignorés" : "")}.");
        };
        page.Children.Add(Toolbar(clean, Btn("Stockage Windows", () => Open("ms-settings:storagesense"))));
        page.Children.Add(total);
        page.Children.Add(list);
        if (Animate) Task.Run(() => Cleaner.Targets().Select(Cleaner.Measure).ToList()).ContinueWith(t => Dispatcher.Invoke(() => Fill(t.Result)));
        else Fill(Cleaner.Targets().Select(Cleaner.Measure).ToList());
        return page;
    }

    private UIElement HealthPage()
    {
        var d = _state.Data;
        var h = d.Health;
        var page = W.Page("Santé du PC", "Ce qui peut faire planter ou ralentir la machine avec le temps. Lecture seule.");
        page.Children.Add(Toolbar(Btn("Actualiser", () => Rescan("health"), true), Btn("Rapport Windows d'erreurs", () => Open("ms-settings:troubleshoot"))));

        var tiles = new UniformGrid { Columns = 4 };
        var errors = h.GpuErrorsByDay.Values.Sum();
        var beforeDriver = d.DriverErrors == 0 && d.OldDriverErrors > 0;
        tiles.Children.Add(Tile("Erreurs GPU sur 14 j", errors.ToString(), errors == 0 ? "Good" : beforeDriver ? "Warn" : "Accent", beforeDriver ? "avant le nouveau driver" : null));
        tiles.Children.Add(Tile("Crashs Valorant sur 14 j", h.GameCrashes.ToString(), h.GameCrashes > 0 ? "Warn" : "Good"));
        tiles.Children.Add(Tile("Arrêts brutaux sur 14 j", h.HardResets.ToString(), h.HardResets > 0 ? "Warn" : "Good"));
        tiles.Children.Add(Tile("Erreurs matérielles", h.WheaErrors.ToString(), h.WheaErrors > 0 ? "Accent" : "Good"));
        page.Children.Add(tiles);

        if (h.GpuErrorsByDay.Count > 0)
        {
            var bars = new StackPanel();
            bars.Children.Add(Text("Erreurs du driver GPU par jour", 14, "Text", true));
            var max = h.GpuErrorsByDay.Values.Max();
            foreach (var (day, count) in h.GpuErrorsByDay)
            {
                var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
                row.Children.Add(Text(day.ToString("dd/MM"), 12, "Muted"));
                var bar = StackBar((count, "Accent"), (max - count, "Panel"));
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var value = Text(count.ToString(), 12, "Text").With(t => t.TextAlignment = TextAlignment.Right);
                Grid.SetColumn(value, 2);
                row.Children.Add(value);
                bars.Children.Add(row);
            }
            page.Children.Add(Card(bars));
        }

        page.Children.Add(Heading("Matériel"));
        void Item(string title, string detail, bool ok = true) => page.Children.Add(Card(Line(title, detail), ok ? null : "Warn"));

        Item("Système démarré", h.UptimeHours > 72 ? $"Depuis {h.UptimeHours / 24:0} jours : un redémarrage remet tout à plat." : $"Depuis {h.UptimeHours:0} h.", h.UptimeHours <= 72);
        Item("Mémoire", h.RamSticks == 0 ? "Non détectée." : $"{h.RamSticks} barrettes, {h.RamChannels} canaux, {h.RamSpeed} MHz{(h.RamChannels < 2 ? " : un seul canal bride les FPS" : " : double canal actif")}.", h.RamChannels != 1);
        Item("SSD", h.DiskTemp < 0 ? $"État : {h.DiskHealth}. Détails visibles avec les droits admin." : $"État : {h.DiskHealth}, {h.DiskTemp} °C, usure {Math.Max(0, h.DiskWear)} %, {h.DiskHours} heures de marche.", h.DiskHealth is "" or "Healthy");
        Item("Batterie", h.BatteryWearPct < 0 ? "Usure visible avec les droits admin." : $"Usure : {h.BatteryWearPct:0} %.", h.BatteryWearPct < 30);
        Item("Espace disque", $"{d.DiskFreeGb:0} Go libres, {d.DiskFreePct:0} %.", d.DiskFreePct >= 10);
        Item("BIOS", h.BiosVersion.Length == 0 ? "Version inconnue." : $"Version {h.BiosVersion}. Mises à jour sur le site du fabricant.");
        if (d.DriverVersion.Length > 0) Item("Driver NVIDIA", $"{d.DriverVersion}{(d.DriverDate is { } date ? $", daté du {date:dd/MM/yyyy}" : "")}.");
        return page;
    }

    private UIElement UndoPage()
    {
        var page = W.Page("Historique", "Tout ce que Shanksito Tweaker a changé, avec retour à la valeur d'origine exacte.");
        var items = _state.Engine.Store.Items.Values.OrderByDescending(s => s.When).ToList();
        var ids = items.Select(s => s.Id).ToList();
        var undoAll = Btn("Tout annuler", async () =>
        {
            if (MessageBox.Show($"Restaurer {ids.Count} réglages ?", "Shanksito Tweaker", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var results = await Task.Run(() => ids.Select(_state.Engine.Revert).ToList());
            Rescan("undo");
            var failed = results.Count(r => !r.Ok);
            SetStatus(failed == 0 ? "Tout est restauré." : $"{failed} réglages non restaurés : {results.First(r => !r.Ok).Message}");
        }, true);
        undoAll.IsEnabled = items.Count > 0 && _state.Admin;
        page.Children.Add(Toolbar(undoAll));

        if (items.Count == 0) page.Children.Add(Text("Aucun changement pour l'instant.", 13, "Muted"));
        foreach (var item in items)
        {
            var row = new DockPanel();
            var undo = Btn("Annuler", () =>
            {
                SetStatus(_state.Engine.Revert(item.Id).Message);
                Rescan("undo");
            });
            undo.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
            row.Children.Add(Line(item.Title, item.When.ToString("dd/MM/yyyy HH:mm")));
            page.Children.Add(Card(row));
        }

        var journal = Journal.Read();
        if (journal.Count > 0)
        {
            page.Children.Add(Heading("Journal"));
            var lines = new StackPanel();
            foreach (var entry in journal.AsEnumerable().Reverse().Take(30))
                lines.Children.Add(Text($"{entry.When:dd/MM HH:mm}  {entry.Action}  {entry.Detail}", 12, entry.Action == "ECHEC" ? "Warn" : "Muted"));
            page.Children.Add(Card(lines));
        }
        return page;
    }
}
