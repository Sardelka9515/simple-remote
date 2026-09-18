using System.Diagnostics;

namespace SimpleRemote.Platform;

/// <summary>
/// Adds the inbound firewall exception.
///
/// Windows normally prompts for this the first time Kestrel binds, but the prompt is easy to
/// dismiss by reflex - and once dismissed, Windows remembers the block and never asks again. The
/// symptom is a QR code that scans and then times out, with nothing to indicate why, so an
/// explicit repair button is worth the small amount of code.
/// </summary>
public static class FirewallRule
{
    private const string RuleName = "SimpleRemote";

    public static bool Exists()
    {
        var output = RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\"", elevated: false, out var exitCode);
        return exitCode == 0 && output.Contains(RuleName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the rule, elevating via UAC. Returns false if the user declines the prompt, which
    /// is a normal outcome and not an error.
    /// </summary>
    public static bool TryAdd(params int[] ports)
    {
        var list = string.Join(",", ports.Where(p => p > 0).Distinct());
        if (list.Length == 0) return false;

        // Remove first so changing the port does not leave a stale rule behind.
        RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"", elevated: true, out _);

        RunNetsh(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
            $"protocol=TCP localport={list} profile=private,domain " +
            $"program=\"{AutoStart.ExecutablePath}\"",
            elevated: true,
            out var exitCode);

        return exitCode == 0;
    }

    /// <summary>
    /// Scoped to private and domain profiles only. A public profile means an untrusted network
    /// such as cafe Wi-Fi, and opening an input-injection port there would be indefensible.
    /// </summary>
    private static string RunNetsh(string arguments, bool elevated, out int exitCode)
    {
        exitCode = -1;

        try
        {
            var startInfo = new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = elevated,
            };

            if (elevated)
            {
                startInfo.Verb = "runas";
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            }
            else
            {
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }

            using var process = Process.Start(startInfo);
            if (process is null) return "";

            var output = elevated ? "" : process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            exitCode = process.HasExited ? process.ExitCode : -1;
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Win32Exception here is almost always the user clicking No on the UAC prompt.
            return "";
        }
    }
}
