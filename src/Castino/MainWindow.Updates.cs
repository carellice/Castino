using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Castino.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Castino;

public sealed partial class MainWindow
{
    private readonly UpdateService _updates = new();
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private CancellationTokenSource? _downloadCancellation;
    private UpdateRelease? _availableUpdate;
    private string? _stagedUpdate;
    private bool _checkingUpdates, _updatesInitialized, _updatesClosed, _installingUpdate;
    private static string UpdateResultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Castino", "update-result.json");

    private void InitializeUpdates()
    {
        VersionText.Text = $"Versione installata: {UpdateService.CurrentVersion}";
        AutoUpdatesCheck.IsChecked = _settings.CheckForUpdatesAutomatically;
        _updatesInitialized = true;
        RootGrid.Loaded += Updates_Loaded;
        _updateTimer.Tick += async (_, _) =>
        {
            if (_settings.CheckForUpdatesAutomatically) await CheckUpdatesAsync(false);
        };
        _updateTimer.Start();
    }

    private async void Updates_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= Updates_Loaded;
        bool hasResult = false;
        try
        {
            if (File.Exists(UpdateResultPath))
            {
                using var result = JsonDocument.Parse(File.ReadAllText(UpdateResultPath));
                bool success = result.RootElement.GetProperty("Success").GetBoolean();
                SetUpdateStatus(success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                    success ? "Castino aggiornato" : "Aggiornamento non riuscito",
                    result.RootElement.GetProperty("Message").GetString() ?? "");
                File.Delete(UpdateResultPath);
                hasResult = true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        if (_settings.CheckForUpdatesAutomatically)
        {
            await Task.Delay(1500);
            if (!_updatesClosed) await CheckUpdatesAsync(false, hasResult);
        }
    }

    private void AutoUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (!_updatesInitialized) return;
        _settings.CheckForUpdatesAutomatically = AutoUpdatesCheck.IsChecked == true;
        _settings.Save();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);

    private async Task CheckUpdatesAsync(bool manual, bool preserveResult = false)
    {
        if (_checkingUpdates || _downloadCancellation != null || _stagedUpdate != null || _updatesClosed) return;
        _checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        if (manual) SetUpdateStatus(InfoBarSeverity.Informational, "Ricerca aggiornamenti…", "Controllo delle release di Castino su GitHub.");
        try
        {
            var release = await _updates.CheckAsync(_updateLifetime.Token);
            if (_updatesClosed) return;
            _availableUpdate = release != null && release.Version > UpdateService.CurrentVersion ? release : null;
            InstallUpdateButton.Visibility = _availableUpdate != null ? Visibility.Visible : Visibility.Collapsed;
            if (_availableUpdate != null)
                SetUpdateStatus(InfoBarSeverity.Informational, $"Castino {_availableUpdate.Version} disponibile",
                    "Scarica e installa l'aggiornamento direttamente da qui. Il mirroring verrà chiuso solo al riavvio.");
            else if (manual || (!preserveResult && (UpdateBar.Title ?? "").StartsWith("Castino ", StringComparison.Ordinal)))
                SetUpdateStatus(InfoBarSeverity.Success, "Castino è aggiornato", $"Versione {UpdateService.CurrentVersion}. Nessuna nuova release stabile pronta da installare.");
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_updatesClosed && manual)
                SetUpdateStatus(InfoBarSeverity.Warning, "Controllo non riuscito", ex is OperationCanceledException ? "Tempo di attesa scaduto. Riprova più tardi." : ex.Message);
        }
        finally
        {
            _checkingUpdates = false;
            if (!_updatesClosed) CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate == null || _downloadCancellation != null || _installingUpdate) return;
        if (_stagedUpdate != null)
        {
            _installingUpdate = true;
            InstallUpdateButton.IsEnabled = false;
            try { await LaunchInstallerAsync(); }
            catch (Exception ex) { SetUpdateStatus(InfoBarSeverity.Error, "Installazione non avviata", ex.Message); }
            finally { _installingUpdate = false; if (!_updatesClosed) InstallUpdateButton.IsEnabled = true; }
            return;
        }
        string work = Path.Combine(Path.GetTempPath(), "Castino-Update-" + Guid.NewGuid().ToString("N"));
        _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        InstallUpdateButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = CancelUpdateButton.Visibility = Visibility.Visible;
        SetUpdateStatus(InfoBarSeverity.Informational, "Download dell'aggiornamento…", "L'app resta utilizzabile durante il download.");
        try
        {
            var progress = new Progress<int>(value => { if (!_updatesClosed) UpdateProgress.Value = value; });
            _stagedUpdate = await _updates.DownloadAndStageAsync(_availableUpdate, work, progress, _downloadCancellation.Token);
            if (!_updatesClosed)
            {
                InstallUpdateButton.Content = "Installa e riavvia";
                SetUpdateStatus(InfoBarSeverity.Success, "Aggiornamento pronto", "Download verificato. Premi «Installa e riavvia» per chiudere il mirroring e aggiornare Castino.");
            }
        }
        catch (OperationCanceledException)
        {
            if (!_updatesClosed) SetUpdateStatus(InfoBarSeverity.Informational, "Download interrotto", "Puoi riprovare quando vuoi.");
        }
        catch (Exception ex)
        {
            if (!_updatesClosed) SetUpdateStatus(InfoBarSeverity.Error, "Aggiornamento non scaricato", ex.Message);
        }
        finally
        {
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            if (!_updatesClosed)
            {
                InstallUpdateButton.IsEnabled = CheckUpdatesButton.IsEnabled = true;
                UpdateProgress.Visibility = CancelUpdateButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();

    private async Task LaunchInstallerAsync()
    {
        // MSIX deployments are immutable and must continue through their original channel.
        bool packaged;
        try { _ = Windows.ApplicationModel.Package.Current; packaged = true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { packaged = false; }
        if (packaged) throw new InvalidOperationException("Per un'installazione MSIX usa lo stesso canale di installazione. Lo ZIP dalle release supporta gli aggiornamenti dall'app.");
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string probe = Path.Combine(target, ".castino-write-test-" + Guid.NewGuid().ToString("N"));
        try { using (File.Create(probe)) { } File.Delete(probe); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("La cartella di Castino non è scrivibile. Sposta l'app in una cartella personale e riprova."); }
        string work = Path.GetDirectoryName(_stagedUpdate!)!;
        Directory.CreateDirectory(Path.GetDirectoryName(UpdateResultPath)!);
        string manifest = Path.Combine(work, "install.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            Target = target, ProcessId = Environment.ProcessId, ResultFile = UpdateResultPath,
            Version = _availableUpdate!.Version.ToString()
        }));
        string script = Path.Combine(work, "Install-Update.ps1");
        // Run the currently installed helper, not code from the downloaded package.
        File.Copy(Path.Combine(target, "Updates", "Install-Update.ps1"), script, true);
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = work };
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(powershell)!, "Modules");
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Manifest", manifest })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Impossibile avviare l'aggiornatore.");
        for (int i = 0; i < 100; i++)
        {
            if (process.HasExited) throw new InvalidOperationException("L'aggiornatore non è partito. Controlla la cartella temporanea: " + work);
            if (File.Exists(Path.Combine(work, "ready")))
            {
                SaveQualitySettings();
                Close();
                return;
            }
            await Task.Delay(100, _updateLifetime.Token);
        }
        // The helper has not acknowledged the request: leave this app open.
        try { process.Kill(); } catch (InvalidOperationException) { }
        throw new InvalidOperationException("L'aggiornatore non risponde. Riprova.");
    }

    private void SetUpdateStatus(InfoBarSeverity severity, string title, string message)
    {
        UpdateBar.Severity = severity;
        UpdateBar.Title = title;
        UpdateBar.Message = message;
        UpdateBar.IsOpen = true;
    }

    private void StopUpdates()
    {
        _updatesClosed = true;
        _updateTimer.Stop();
        _updateLifetime.Cancel();
        _downloadCancellation?.Cancel();
    }
}
