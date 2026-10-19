using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Castino.Updates;

int passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
    passed++;
}
void Reject(Action action, string label)
{
    try { action(); } catch (InvalidDataException) { Check(true, label); return; }
    throw new Exception("Expected rejection: " + label);
}
string Release(string tag = "v0.1.2", bool prerelease = false, bool draft = false,
    bool withZip = true, string? url = null, long size = 100, string? digest = null)
{
    string name = $"Castino-{tag.TrimStart('v')}-win-x64.zip";
    var assets = new List<object>();
    if (withZip) assets.Add(new { name, state = "uploaded", size, digest,
        browser_download_url = url ?? $"https://github.com/carellice/Castino/releases/download/{tag}/{name}" });
    assets.Add(new { name = "SHA256SUMS.txt", state = "uploaded", size = 100,
        browser_download_url = $"https://github.com/carellice/Castino/releases/download/{tag}/SHA256SUMS.txt" });
    return JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, assets });
}

var release = UpdateService.ParseRelease(Release())!;
Check(release.Version == new Version(0, 1, 2), "stable release and version parsed");
Check(release.Version > new Version(0, 1, 1), "new version offered");
Check(!(release.Version > new Version(0, 1, 2)) && !(release.Version > new Version(0, 1, 3)), "same version and downgrades excluded");
Check(UpdateService.NormalizeVersion(new Version(0, 1, 2, 0)) == release.Version, "assembly revision normalized");
Check(UpdateService.ParseRelease(Release(prerelease: true)) == null, "prereleases ignored");
Check(UpdateService.ParseRelease(Release(draft: true)) == null, "drafts ignored");
Check(UpdateService.ParseRelease(Release(withZip: false)) == null, "incomplete releases ignored");
Check(UpdateService.ParseRelease(Release(tag: "v0.1.2-beta")) == null, "non-stable tags ignored");
Check(UpdateService.ParseRelease(Release(tag: "v0.10.0"))!.Version > release.Version, "numeric version ordering");
Reject(() => UpdateService.ParseRelease(Release(url: "https://example.com/a.zip")), "foreign download rejected");
Reject(() => UpdateService.ParseRelease(Release(url: release.DownloadUrl + "#unexpected")), "download URL fragment rejected");
Reject(() => UpdateService.ParseRelease(Release(size: UpdateService.MaxDownloadSize + 1)), "oversized release rejected");
string checksum = new('a', 64);
Check(UpdateService.ParseChecksum(checksum + " *test.zip\r\n", "test.zip") == checksum, "checksum parser");
Reject(() => UpdateService.ParseChecksum(checksum + "  other.zip", "test.zip"), "missing checksum rejected");
Reject(() => UpdateService.ParseChecksum($"{checksum} test.zip\n{checksum} test.zip", "test.zip"), "ambiguous checksum rejected");

byte[] MakeZip(bool traversal = false)
{
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
    {
        foreach (string name in new[] { "Castino.exe", "Castino.dll", "coreclr.dll", "Microsoft.UI.Xaml.dll", "Assets/AppIcon.ico", "Updates/Install-Update.ps1" })
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("test payload");
        if (traversal) using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) writer.Write("escape");
    }
    return stream.ToArray();
}
string temporary = Path.Combine(Path.GetTempPath(), "Castino-UpdateTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var bytes = MakeZip();
    string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var actualRelease = UpdateService.ParseRelease(Release(size: bytes.Length, digest: "sha256:" + sha))!;
    using var client = new HttpClient(new FakeHandler(bytes, sha, actualRelease.AssetName));
    var service = new UpdateService(client);
    string staged = await service.DownloadAndStageAsync(actualRelease, Path.Combine(temporary, "good"), null, default);
    Check(File.Exists(Path.Combine(staged, "Castino.exe")), "verified package downloaded and staged");
    Check(!File.Exists(Path.Combine(temporary, "good", actualRelease.AssetName)), "archive removed after staging");
    async Task RejectDownload(UpdateService instance, UpdateRelease item, string name, CancellationToken ct = default)
    {
        string directory = Path.Combine(temporary, name);
        try { await instance.DownloadAndStageAsync(item, directory, null, ct); }
        catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException)
        {
            Check(!Directory.Exists(directory), name + " rejected and partial files cleaned");
            return;
        }
        throw new Exception("Unsafe download accepted: " + name);
    }
    using var badClient = new HttpClient(new FakeHandler(bytes, checksum, actualRelease.AssetName));
    await RejectDownload(new UpdateService(badClient), actualRelease with { Digest = null }, "checksum-mismatch");
    await RejectDownload(service, actualRelease with { Digest = "sha256:" + checksum }, "digest-mismatch");
    await RejectDownload(service, actualRelease with { Size = bytes.Length - 1 }, "size-mismatch");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await RejectDownload(service, actualRelease, "cancelled", cancelled.Token);
    var evilZip = MakeZip(true);
    string evilHash = Convert.ToHexString(SHA256.HashData(evilZip));
    using var evilClient = new HttpClient(new FakeHandler(evilZip, evilHash, actualRelease.AssetName));
    await RejectDownload(new UpdateService(evilClient), actualRelease with { Size = evilZip.Length, Digest = null }, "zip-traversal");
    Check(!File.Exists(Path.Combine(temporary, "zip-traversal", "escaped.txt")), "no files escaped extraction root");
    using var limitedClient = new HttpClient(new FakeHandler([], "", "", HttpStatusCode.Forbidden));
    try { await new UpdateService(limitedClient).CheckAsync(default); throw new Exception("Rate limit accepted"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("limitato"), "GitHub rate limit handled"); }
    using var offlineClient = new HttpClient(new FakeHandler([], "", "", HttpStatusCode.NotFound));
    Check(await new UpdateService(offlineClient).CheckAsync(default) == null, "no published release handled");
}
finally { Directory.Delete(temporary, true); }
Console.WriteLine($"{passed} updater checks passed.");
if (args.Contains("--live"))
{
    var live = await new UpdateService().CheckAsync(default);
    if (live == null) throw new Exception("No complete release returned by GitHub");
    Console.WriteLine($"LIVE: GitHub release {live.Tag}, asset {live.AssetName}, size {live.Size}, digest {live.Digest}");
}

sealed class FakeHandler(byte[] archive, string hash, string name, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")
            ? Encoding.UTF8.GetBytes($"{hash}  {name}\n") : archive;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(content) });
    }
}
