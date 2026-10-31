using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ShanksitoTweaker.Core;
using ShanksitoTweaker.Ui;
using static ShanksitoTweaker.Ui.W;

namespace ShanksitoTweaker;

public sealed partial class Shell
{
    private UIElement HomePage()
    {
        var d = _state.Data;
        var page = W.Page("Tableau de bord", "Ta configuration, tes dernières mesures et la suite à faire.");

        var top = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        top.ColumnDefinitions.Add(new ColumnDefinition());

        var ring = new StackPanel();
        ring.Children.Add(new ScoreRing { Score = d.Score, Height = 128 });
        ring.Children.Add(Text("Score de configuration", 12, "Muted").With(t => t.TextAlignment = TextAlignment.Center));
        var ringCard = Card(ring).With(c => c.Margin = new Thickness(0, 0, 8, 8));
        top.Children.Add(ringCard);

        var tiles = new UniformGrid { Columns = 2 };
        var last = d.Sessions.LastOrDefault();
        tiles.Children.Add(Tile("FPS moyens en partie", last is null ? "n/a" : $"{last.AvgFps:0}", "Text", last is null ? null : $"le {last.Start:dd/MM à HH:mm}"));
        var capped = d.Valo.LimitAlways == true;
        tiles.Children.Add(Tile("Limite Valorant", capped ? $"{d.Valo.MaxAlways:0} FPS" : "Aucune", capped && d.Valo.MaxAlways <= 144 ? "Accent" : "Good"));
        var screen = d.GameDisplay;
        tiles.Children.Add(Tile("Écran de jeu", screen is null ? "n/a" : $"{screen.Current} / {screen.Max} Hz", screen is not null && screen.Current < screen.Max ? "Accent" : "Good"));
        tiles.Children.Add(Tile("Température GPU", d.Gpu is null ? "n/a" : $"{d.Gpu.Temp:0} °C", d.Gpu is { Temp: >= 70, Util: < 15 } ? "Warn" : "Good", d.Gpu is null ? null : $"charge {d.Gpu.Util:0} %"));
        Grid.SetColumn(tiles, 1);
        top.Children.Add(tiles);
        page.Children.Add(top);

        page.Children.Add(Toolbar(
            Btn("Session de jeu", () => Show("session"), true),
            Btn("Appliquer tout le conseillé", () => ApplyAll(d.Windows.Concat(d.Network).Concat(d.Services).Concat(d.Tasks).Concat(d.Startup), "home")).With(b => b.IsEnabled = _state.Admin),
            Btn("Analyser", () => Rescan("home")),
            Btn("Copier le rapport", CopyReport)));

        if (d.Sessions.Count >= 2)
        {
            var sessions = d.Sessions.TakeLast(12).ToList();
            var chart = new StackPanel();
            chart.Children.Add(Text("FPS par session", 14, "Text", true));
            chart.Children.Add(new LineChart
            {
                Height = 130,
                Values = sessions.Select(s => s.AvgFps).ToList(),
                Labels = sessions.Select(s => s.Start.ToString("dd/MM")).ToList(),
                Marks = Journal.ChangedSessions(sessions, Journal.Read()),
            });
            page.Children.Add(Card(chart));
        }

        var todo = d.Findings.Where(f => f.Severity is Severity.Critique or Severity.Attention).ToList();
        page.Children.Add(Heading(todo.Count == 0 ? "Rien d'urgent" : "À faire maintenant"));
        foreach (var finding in d.Findings) page.Children.Add(FindingCard(finding));
        return page;
    }

    private Border FindingCard(Finding finding)
    {
        var edge = finding.Severity switch { Severity.Critique => "Accent", Severity.Attention => "Warn", Severity.Ok => "Good", _ => "Info" };
        var row = new DockPanel();
        if (finding.ActionTarget is { } target)
        {
            var action = Btn(finding.ActionLabel!, () => Open(target));
            action.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
        }
        if (finding.Page is { } pageKey)
        {
            var go = Btn("Y aller", () => Show(pageKey));
            go.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(go, Dock.Right);
            row.Children.Add(go);
        }
        row.Children.Add(Line(finding.Title, finding.Detail));
        return Card(row, edge);
    }

    private void CopyReport()
    {
        Clipboard.SetText(_state.AdvisorReport());
        SetStatus("Rapport copié dans le presse-papiers.");
    }
}
