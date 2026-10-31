using System.Diagnostics;
using Microsoft.Win32;

namespace ShanksitoTweaker.Core;

public enum RegRoot { HKCU, HKLM }

public readonly record struct CommandResult(int Code, string Output);

/// <summary>Every write to the machine goes through here, so tests can run against a fake.</summary>
public interface ISystemApi
{
    object? RegGet(RegRoot root, string path, string name);
    RegistryValueKind RegKind(RegRoot root, string path, string name);
    string[] RegNames(RegRoot root, string path);
    void RegSet(RegRoot root, string path, string name, object value, RegistryValueKind kind);
    void RegDelete(RegRoot root, string path, string name);
    void RegDeleteKey(RegRoot root, string path);
    CommandResult Run(string exe, string args);
}

public sealed class RealSystem : ISystemApi
{
    private static RegistryKey Base(RegRoot root) => RegistryKey.OpenBaseKey(
        root == RegRoot.HKCU ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    public object? RegGet(RegRoot root, string path, string name)
    {
        using var key = Base(root).OpenSubKey(path);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public RegistryValueKind RegKind(RegRoot root, string path, string name)
    {
        using var key = Base(root).OpenSubKey(path);
        return key?.GetValue(name) is null ? RegistryValueKind.Unknown : key.GetValueKind(name);
    }

    public string[] RegNames(RegRoot root, string path)
    {
        using var key = Base(root).OpenSubKey(path);
        return key?.GetValueNames() ?? [];
    }

    public void RegSet(RegRoot root, string path, string name, object value, RegistryValueKind kind)
    {
        using var key = Base(root).CreateSubKey(path, true);
        key.SetValue(name, value, kind);
    }

    public void RegDelete(RegRoot root, string path, string name)
    {
        using var key = Base(root).OpenSubKey(path, true);
        key?.DeleteValue(name, false);
    }

    public void RegDeleteKey(RegRoot root, string path) => Base(root).DeleteSubKeyTree(path, false);

    public CommandResult Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30_000))
        {
            p.Kill();
            return new CommandResult(-1, "timeout");
        }
        return new CommandResult(p.ExitCode, output + err.Result);
    }
}
