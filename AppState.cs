using ShanksitoTweaker.Core;

namespace ShanksitoTweaker;

public sealed class AppState
{
    public ISystemApi Sys { get; }
    public bool Admin { get; }
    public TweakEngine Engine { get; }
    public ScanData Data { get; private set; } = new();
    public AiStore Ai { get; init; } = AiStore.Default;
    public AiServer Server { get; } = new();
    public List<ChatEntry> Chat { get; } = [];
    public List<string> ClosedApps { get; } = [];
    public List<Hop>? Route { get; set; }

    public AppState(ISystemApi sys, bool admin, BackupStore? store = null)
    {
        Sys = sys;
        Admin = admin;
        store ??= new BackupStore(Path.Combine(BackupStore.DefaultDir, "backup.json"));
        Engine = new TweakEngine(sys, store, Journal.Add);
    }

    public void Scan()
    {
        var (test, bloat) = (Data.NetTest, Data.Bloat);
        Data = Diagnostics.Scan(Sys);
        Data.NetTest = test;
        Data.Bloat = bloat;
    }

    public void TestNetwork()
    {
        if (Data.Net is { } net) Data.NetTest = Network.Run(net, Data.Valo.GamePod);
    }

    public string GameHost => Network.AwsRegion(Data.Valo.GamePod) is { } region ? $"dynamodb.{region}.amazonaws.com" : "1.1.1.1";

    public string AdvisorReport() => Diagnostics.Report(Data, Sys);

    public string AdvisorCatalog() => Diagnostics.TweakCatalog(Data, Sys);

    /// <summary>Each tweak state may run powercfg or PowerShell: read once per message, off the UI thread.</summary>
    public AiContext AiContext(string modelName)
    {
        var states = Data.AllTweaks.Where(t => t is not StartupTweak { Locked: true }).Select(t => (Tweak: t, On: t.IsApplied(Sys))).ToList();
        var catalog = string.Join("\n", states.Where(x => !x.On).Select(x => $"{x.Tweak.Id} | {x.Tweak.Title} | effet : {x.Tweak.Gain} | risque : {x.Tweak.Risk}"));
        var done = string.Join(", ", states.Where(x => x.On).Select(x => x.Tweak.Title));
        var allowed = states.Where(x => !x.On).Select(x => x.Tweak.Id).ToHashSet();
        var system = ShanksitoAi.SystemPrompt(modelName, Diagnostics.Report(Data, Sys, 4), catalog, done);
        return new AiContext(system, allowed, states.Select(x => new AiTweak(x.Tweak.Id, x.Tweak.Title, x.On)).ToList());
    }
}
