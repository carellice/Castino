using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Castino.Updates;

internal sealed record UpdateRelease(Version Version, string Tag, string AssetName, Uri DownloadUrl,
    Uri ChecksumUrl, long Size, string? Digest);

internal sealed class UpdateService
{
    internal const string LatestUrl = "https://api.github.com/repos/carellice/Castino/releases/latest";
    internal const long MaxDownloadSize = 512L * 1024 * 1024;
    private readonly HttpClient _http;
    private static readonly HttpClient Client = CreateClient();

    internal UpdateService(HttpClient? http = null) => _http = http ?? Client;
    internal static Version CurrentVersion => NormalizeVersion(typeof(UpdateService).Assembly.GetName().Version!);
    internal static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Castino/{CurrentVersion}");
        return client;
    }

    internal async Task<UpdateRelease?> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            throw new InvalidOperationException("GitHub ha limitato le richieste. Riprova più tardi.");
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    internal static UpdateRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var match = Regex.Match(tag, @"^v?(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version)) return null;
        string name = $"Castino-{version}-win-x64.zip";
        JsonElement? archive = null, checksums = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("state").GetString() != "uploaded") continue;
            if (asset.GetProperty("name").GetString() == name) archive = asset;
            if (asset.GetProperty("name").GetString() == "SHA256SUMS.txt") checksums = asset;
        }
        if (archive == null || checksums == null) return null;
        long size = archive.Value.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxDownloadSize) throw new InvalidDataException("Dimensione dell'aggiornamento non valida.");
        var download = ValidateAssetUrl(archive.Value.GetProperty("browser_download_url").GetString(), tag, name);
        var checksum = ValidateAssetUrl(checksums.Value.GetProperty("browser_download_url").GetString(), tag, "SHA256SUMS.txt");
        string? digest = archive.Value.TryGetProperty("digest", out var value) ? value.GetString() : null;
        if (digest != null && !Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Checksum GitHub non valido.");
        return new UpdateRelease(version, tag, name, download, checksum, size, digest);
    }

    private static Uri ValidateAssetUrl(string? url, string tag, string name)
    {
        var expected = new Uri($"https://github.com/carellice/Castino/releases/download/{tag}/{name}");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri != expected || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("L'aggiornamento non proviene dalla repository ufficiale di Castino.");
        return uri;
    }

    internal async Task<string> DownloadAndStageAsync(UpdateRelease release, string work,
        IProgress<int>? progress, CancellationToken ct)
    {
        // Each attempt owns a new directory; incomplete attempts are never installed.
        Directory.CreateDirectory(work);
        string zipPath = Path.Combine(work, release.AssetName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        ct = timeout.Token;
        try
        {
            string sums = await _http.GetStringAsync(release.ChecksumUrl, ct);
            string expectedHash = ParseChecksum(sums, release.AssetName);
            if (release.Digest != null && !string.Equals(release.Digest, "sha256:" + expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("I checksum della release non corrispondono. Riprova più tardi.");
            using (var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != release.Size)
                    throw new InvalidDataException("Dimensione del download diversa da quella pubblicata.");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long received = 0;
                int lastPercent = -1, read;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    received += read;
                    if (received > release.Size) throw new InvalidDataException("Download troppo grande.");
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    int percent = (int)(received * 100 / release.Size);
                    if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                }
                if (received != release.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Verifica SHA-256 fallita. Il download non verrà installato.");
            }
            string staged = Path.Combine(work, "package");
            await Task.Run(() => ExtractPackage(zipPath, staged, ct), ct);
            File.Delete(zipPath);
            return staged;
        }
        catch
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static string ParseChecksum(string text, string name)
    {
        var matches = text.Split('\n').Select(line => Regex.Match(line.Trim(), @"^([a-fA-F0-9]{64})\s+\*?(.+)$"))
            .Where(m => m.Success && m.Groups[2].Value == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Checksum SHA-256 della release assente o duplicato.");
        return matches[0].Groups[1].Value;
    }

    internal static void ExtractPackage(string zipPath, string staged, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string root = Path.GetFullPath(staged) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            total = checked(total + entry.Length);
            if (total > 2L * 1024 * 1024 * 1024 || zip.Entries.Count > 10000)
                throw new InvalidDataException("Pacchetto troppo grande.");
            string name = entry.FullName.Replace('\\', '/');
            if (name.Contains(':') || name.StartsWith('/') ||
                !Path.GetFullPath(Path.Combine(staged, name)).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Percorso non valido nel pacchetto.");
            // Reject Unix symbolic links, which must never redirect update writes.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Collegamento simbolico nel pacchetto.");
        }
        Directory.CreateDirectory(staged);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(staged, entry.FullName.Replace('\\', '/'));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        foreach (string required in new[] { "Castino.exe", "Castino.dll", "coreclr.dll", "Microsoft.UI.Xaml.dll", "Assets/AppIcon.ico", "Updates/Install-Update.ps1" })
            if (!File.Exists(Path.Combine(staged, required))) throw new InvalidDataException("Pacchetto Castino incompleto.");
    }
}
