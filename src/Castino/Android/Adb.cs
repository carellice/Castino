using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Castino.Android;

public sealed record AdbDevice(string Serial, string State, string Model)
{
    public bool IsOnline => State == "device";
    public bool IsWireless => Serial.Contains(':') || Serial.Contains("._adb-tls-connect.");
    public string DisplayName => $"{(string.IsNullOrEmpty(Model) ? Serial : Model.Replace('_', ' '))}";
    public string Detail => $"{(IsWireless ? "Wi-Fi" : "USB")} · {Serial}{(IsOnline ? "" : $" ({State})")}";
}

public sealed record MdnsService(string Name, string Type, string Address)
{
    public bool IsPairing => Type == "_adb-tls-pairing._tcp";
    public bool IsConnect => Type == "_adb-tls-connect._tcp";
    public string Host => Address[..Address.LastIndexOf(':')];
}

public sealed record AdbResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
    public string Text => (Output + "\n" + Error).Trim();
}

/// <summary>Thin wrapper around the bundled adb.exe.</summary>
public sealed class Adb
{
    public string ExecutablePath { get; }

    public Adb()
    {
        ExecutablePath = Locate();
    }

    private static string Locate()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "tools", "platform-tools", "adb.exe");
        if (File.Exists(bundled))
            return bundled;

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { }
        }
        throw new FileNotFoundException("adb.exe non trovato. Deve trovarsi in tools\\platform-tools accanto all'eseguibile.");
    }

    public ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }

    public async Task<AdbResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(args) };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            if (ct.IsCancellationRequested)
                throw;
            return new AdbResult(-1, "", "Timeout del comando adb");
        }
        return new AdbResult(process.ExitCode, await stdout, await stderr);
    }

    public Task<AdbResult> RunAsync(params string[] args) => RunAsync((IEnumerable<string>)args);

    public Task<AdbResult> RunForDeviceAsync(string serial, params string[] args)
    {
        var all = new List<string> { "-s", serial };
        all.AddRange(args);
        return RunAsync(all);
    }

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync()
    {
        AdbResult r = await RunAsync("devices", "-l");
        var list = new List<AdbDevice>();
        foreach (string raw in r.Output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith('*'))
                continue;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            string model = "";
            foreach (string p in parts)
                if (p.StartsWith("model:"))
                    model = p["model:".Length..];
            list.Add(new AdbDevice(parts[0], parts[1], model));
        }
        return list;
    }

    /// <summary>adb connect host[:port]. Port defaults to 5555.</summary>
    public async Task<(bool ok, string message)> ConnectAsync(string hostPort)
    {
        AdbResult r = await RunAsync(new[] { "connect", hostPort }, TimeSpan.FromSeconds(15));
        string text = r.Text;
        bool ok = text.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                  && !text.Contains("cannot", StringComparison.OrdinalIgnoreCase)
                  && !text.Contains("failed", StringComparison.OrdinalIgnoreCase);
        return (ok, text);
    }

    /// <summary>Android 11+ wireless debugging pairing: adb pair host:port code.</summary>
    public async Task<(bool ok, string message)> PairAsync(string hostPort, string code)
    {
        AdbResult r = await RunAsync(new[] { "pair", hostPort, code }, TimeSpan.FromSeconds(20));
        return (r.Text.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase), r.Text);
    }

    public Task<AdbResult> DisconnectAsync(string serial) => RunAsync("disconnect", serial);

    /// <summary>
    /// Switches a USB-connected device to TCP/IP mode on port 5555 and returns its Wi-Fi IP address,
    /// so it can then be reached wirelessly without the cable (works on every Android version).
    /// </summary>
    public async Task<(bool ok, string message, string? ip)> EnableWirelessAsync(string serial)
    {
        string? ip = await GetWifiIpAsync(serial);
        if (ip is null)
            return (false, "Impossibile trovare l'indirizzo IP Wi-Fi del telefono. È connesso alla stessa rete del PC?", null);

        AdbResult r = await RunForDeviceAsync(serial, "tcpip", "5555");
        if (!r.Success)
            return (false, r.Text, null);

        // adbd restarts on the device: give it a moment.
        await Task.Delay(1500);
        var (ok, message) = await ConnectAsync($"{ip}:5555");
        return (ok, message, ip);
    }

    private async Task<string?> GetWifiIpAsync(string serial)
    {
        AdbResult r = await RunForDeviceAsync(serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0");
        Match m = Regex.Match(r.Output, @"inet (\d+\.\d+\.\d+\.\d+)/");
        if (m.Success)
            return m.Groups[1].Value;

        r = await RunForDeviceAsync(serial, "shell", "ip", "route");
        m = Regex.Match(r.Output, @"wlan\d+.*?src (\d+\.\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Devices advertising wireless debugging on the LAN (mDNS), as host:port.</summary>
    public async Task<IReadOnlyList<string>> DiscoverWirelessAsync()
    {
        var found = new List<string>();
        foreach (MdnsService s in await GetMdnsServicesAsync())
            if (s.IsConnect)
                found.Add(s.Address);
        return found;
    }

    /// <summary>
    /// Parses "adb mdns services": one service per line, "instance name &lt;TAB&gt; type &lt;TAB&gt; ip:port".
    /// </summary>
    public async Task<IReadOnlyList<MdnsService>> GetMdnsServicesAsync()
    {
        AdbResult r = await RunAsync("mdns", "services");
        var list = new List<MdnsService>();
        foreach (string raw in r.Output.Split('\n'))
        {
            Match m = Regex.Match(raw.Trim(), @"^(\S+)\s+(_adb[\w-]*\._tcp)\.?\s+(\d+\.\d+\.\d+\.\d+:\d+)$");
            if (m.Success)
                list.Add(new MdnsService(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
        }
        return list;
    }
}
