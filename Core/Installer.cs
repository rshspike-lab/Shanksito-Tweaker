using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace ShanksitoTweaker.Core;

public sealed record InstallOptions(string Folder, string StartMenuFolder, string? DesktopFolder);

/// <summary>The same exe is both setup and app: installing copies it, so there is no separate payload to keep in sync.</summary>
public static class Installer
{
    public const string Product = "Shanksito Tweaker";
    public const string Publisher = "Shanksito";
    public const string ExeName = "ShanksitoTweaker.exe";
    public const string SetupName = "ShanksitoTweaker-Setup.exe";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ShanksitoTweaker";

    public static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    public static bool IsSetup(string? processPath) =>
        Path.GetFileName(processPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static InstallOptions DefaultOptions(bool desktop) => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Product),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Product),
        desktop ? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) : null);

    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    private static void Shortcut(ISystemApi s, string link, string target)
    {
        var script = $"$w=New-Object -ComObject WScript.Shell;$l=$w.CreateShortcut({Quote(link)});$l.TargetPath={Quote(target)};" +
                     $"$l.WorkingDirectory={Quote(Path.GetDirectoryName(target)!)};$l.Save()";
        var result = s.Run("powershell", CommandTweak.Ps(script));
        if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
    }

    public static string Install(ISystemApi s, string sourceExe, InstallOptions options)
    {
        Directory.CreateDirectory(options.Folder);
        var target = Path.Combine(options.Folder, ExeName);
        File.Copy(sourceExe, target, true);
        ClearDownloadMark(target);

        Directory.CreateDirectory(options.StartMenuFolder);
        var links = new List<string> { Path.Combine(options.StartMenuFolder, Product + ".lnk") };
        if (options.DesktopFolder is not null) links.Add(Path.Combine(options.DesktopFolder, Product + ".lnk"));
        foreach (var link in links) Shortcut(s, link, target);

        void Set(string name, object value, RegistryValueKind kind = RegistryValueKind.String) =>
            s.RegSet(RegRoot.HKLM, UninstallKey, name, value, kind);
        Set("DisplayName", Product);
        Set("DisplayVersion", Version);
        Set("Publisher", Publisher);
        Set("DisplayIcon", target);
        Set("InstallLocation", options.Folder);
        Set("UninstallString", $"\"{target}\" --uninstall");
        Set("Shortcuts", string.Join("|", links));
        Set("EstimatedSize", (int)(new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
        Set("NoModify", 1, RegistryValueKind.DWord);
        Set("NoRepair", 1, RegistryValueKind.DWord);
        return target;
    }

    /// <summary>Returns the install folder. The running exe cannot delete itself: see <see cref="ScheduleRemoval"/>.</summary>
    public static string? Unregister(ISystemApi s)
    {
        var folder = s.RegGet(RegRoot.HKLM, UninstallKey, "InstallLocation") as string;
        var links = (s.RegGet(RegRoot.HKLM, UninstallKey, "Shortcuts") as string ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        foreach (var link in links)
        {
            if (File.Exists(link)) File.Delete(link);
            var parent = Path.GetDirectoryName(link);
            if (parent is not null && Path.GetFileName(parent) == Product && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        s.RegDeleteKey(RegRoot.HKLM, UninstallKey);
        return folder;
    }

    /// <summary>Like any installer: the warning belongs to the setup the person chose to run, not to every later launch.</summary>
    public static void ClearDownloadMark(string file)
    {
        try
        {
            File.Delete(file + ":Zone.Identifier");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Only means one more Windows prompt.
        }
    }

    /// <summary>Removes only our exe, and the folder if nothing else is in it.</summary>
    public static void ScheduleRemoval(string folder, bool removeUnpackedRuntime = true)
    {
        var exe = Path.Combine(folder, ExeName);
        var command = $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{exe}\" & rmdir \"{folder}\"";
        if (removeUnpackedRuntime)
        {
            foreach (var name in new[] { "ShanksitoTweaker", "ShanksitoTweaker-Setup" })
                command += $" & rmdir /s /q \"{Path.Combine(Path.GetTempPath(), ".net", name)}\"";
        }
        Process.Start(new ProcessStartInfo("cmd.exe", command) { UseShellExecute = false, CreateNoWindow = true });
    }

    private static string AcceptedFlag => Path.Combine(BackupStore.DefaultDir, "accepted");

    public static bool NoticeAccepted => File.Exists(AcceptedFlag);

    public static void AcceptNotice()
    {
        Directory.CreateDirectory(BackupStore.DefaultDir);
        File.WriteAllText(AcceptedFlag, Version);
    }

    public const string Notice =
        "Shanksito Tweaker modifie des réglages de Windows.\n\n" +
        "Chaque changement est sauvegardé et annulable, mais tu l'utilises à tes risques.\n" +
        "Shanksito AI tourne sur ton PC : rien n'est envoyé sur internet. Les modèles et les tests réseau ne se lancent que sur ton clic.\n" +
        "Pendant une partie, seul un ping vers 1.1.1.1 mesure ta connexion.\n" +
        "Le jeu et son anti-triche ne sont jamais touchés.\n\n" +
        "Projet indépendant, non affilié à Riot Games, NVIDIA ou ASUS.";
}
