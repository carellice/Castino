using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;

namespace Castino.Android;

/// <summary>
/// Android 11+ "Pair device with QR code" (same protocol as Android Studio):
/// the PC shows <c>WIFI:T:ADB;S:&lt;name&gt;;P:&lt;password&gt;;;</c>, the phone scans it and advertises
/// an mDNS service "_adb-tls-pairing._tcp" named &lt;name&gt;; we then run <c>adb pair</c> with the password
/// and connect to the phone's "_adb-tls-connect._tcp" service.
/// </summary>
public sealed class QrPairing
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private readonly Adb _adb;

    public string ServiceName { get; }
    public string Password { get; }
    public string Payload => $"WIFI:T:ADB;S:{ServiceName};P:{Password};;";

    public QrPairing(Adb adb)
    {
        _adb = adb;
        ServiceName = "castino-" + RandomString(8);
        Password = RandomString(12);
    }

    /// <summary>QR code as PNG bytes (black on white, with quiet zone).</summary>
    public byte[] CreateQrPng(int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(Payload, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Waits for the phone to scan the code, pairs and connects. Returns the serial (ip:port) of the
    /// connected device, or null if paired but the connection service was not found.
    /// </summary>
    public async Task<string?> WaitPairAndConnectAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Inquadra il codice con il telefono…");
        MdnsService? pairing = null;
        while (pairing == null)
        {
            ct.ThrowIfCancellationRequested();
            pairing = (await _adb.GetMdnsServicesAsync()).FirstOrDefault(s => s.IsPairing && s.Name == ServiceName);
            if (pairing == null)
                await Task.Delay(800, ct);
        }

        progress.Report($"Telefono trovato ({pairing.Host}), associazione in corso…");
        var (ok, message) = await _adb.PairAsync(pairing.Address, Password);
        if (!ok)
            throw new InvalidOperationException("Associazione non riuscita: " + message);

        progress.Report("Associato! Connessione…");
        // After pairing the phone keeps advertising its connection service on a different port.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            MdnsService? connect = (await _adb.GetMdnsServicesAsync())
                .FirstOrDefault(s => s.IsConnect && s.Host == pairing.Host);
            if (connect != null)
            {
                var (connected, _) = await _adb.ConnectAsync(connect.Address);
                if (connected)
                    return connect.Address;
            }
            await Task.Delay(800, ct);
        }
        return null;
    }

    private static string RandomString(int length) =>
        new(Enumerable.Range(0, length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
}
