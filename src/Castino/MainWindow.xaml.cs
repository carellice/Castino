using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Castino.Android;
using Castino.Mirroring;
using Castino.Video;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace Castino;

public sealed partial class MainWindow : Window
{
    private readonly Adb _adb = null!;
    private readonly AppSettings _settings = AppSettings.Load();
    private VideoRenderer? _renderer;

    private IMirrorSession? _session;
    private H264Decoder? _decoder;
    private volatile int _frameWidth, _frameHeight;
    private bool _starting;

    // Active pointers on the video (mouse = generic finger, touch = its own id for multi-touch).
    private readonly Dictionary<uint, long> _activePointers = new();
    private char _pendingHighSurrogate;

    private static readonly (string label, int value)[] Resolutions =
    {
        ("Originale", 0), ("1920 px", 1920), ("1600 px", 1600), ("1280 px", 1280), ("1024 px", 1024), ("800 px", 800),
    };
    private static readonly int[] FpsValues = { 30, 60, 90, 120 };
    private static readonly (string label, double fraction)[] ScrollSpeeds = { ("Lenta", 0.07), ("Normale", 0.12), ("Veloce", 0.2) };

    private readonly WheelScroller _scroller = new();

    public MainWindow()
    {
        InitializeComponent();
        FitWindowToScreen();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        try
        {
            _adb = new Adb();
        }
        catch (Exception ex)
        {
            _adb = null!;
            ShowStatus(InfoBarSeverity.Error, "adb mancante", ex.Message);
        }

        foreach (var (label, _) in Resolutions)
            ResolutionCombo.Items.Add(label);
        ResolutionCombo.SelectedIndex = Math.Max(0, Array.FindIndex(Resolutions, r => r.value == _settings.MaxSize));
        foreach (int fps in FpsValues)
            FpsCombo.Items.Add($"{fps} fps");
        FpsCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FpsValues, _settings.MaxFps));
        BitrateSlider.Value = _settings.BitRateMbps;
        ScreenOffToggle.IsOn = _settings.TurnScreenOff;
        // Toggle first: ScrollSettings_Changed ignores events while the combo has no selection.
        SmoothScrollToggle.IsOn = _settings.SmoothScroll;
        foreach (var (label, _) in ScrollSpeeds)
            ScrollSpeedCombo.Items.Add(label);
        ScrollSpeedCombo.SelectedIndex = Math.Clamp(_settings.ScrollSpeed, 0, ScrollSpeeds.Length - 1);
        _scroller.NotchFraction = ScrollSpeeds[ScrollSpeedCombo.SelectedIndex].fraction;
        AddressBox.ItemsSource = _settings.RecentAddresses;
        if (_settings.RecentAddresses.Count > 0)
            AddressBox.Text = _settings.RecentAddresses[0];

        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        };
        Closed += MainWindow_Closed;

        if (_adb != null)
            _ = RefreshDevicesAsync();
    }

    // ===================================================================== devices

    private async Task RefreshDevicesAsync(string? selectSerial = null)
    {
        try
        {
            var previous = (DevicesList.SelectedItem as AdbDevice)?.Serial;
            IReadOnlyList<AdbDevice> devices = await _adb.GetDevicesAsync();
            DevicesList.ItemsSource = devices;
            NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            string? wanted = selectSerial ?? previous;
            AdbDevice? toSelect = devices.FirstOrDefault(d => d.Serial == wanted)
                                  ?? devices.FirstOrDefault(d => d.IsOnline && d.IsWireless)
                                  ?? devices.FirstOrDefault(d => d.IsOnline);
            DevicesList.SelectedItem = toSelect;
            UpdateButtons();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Errore adb", ex.Message);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshDevicesAsync();

    private void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var device = DevicesList.SelectedItem as AdbDevice;
        EnableWifiButton.IsEnabled = device is { IsOnline: true, IsWireless: false } && _session == null;
        StartButton.IsEnabled = !_starting && (_session != null || device is { IsOnline: true });
        StartIcon.Glyph = _session != null ? "" : "";
        StartText.Text = _session != null ? "Interrompi mirroring" : "Avvia mirroring";
        bool canControl = _session?.Input != null;
        NavBar.IsHitTestVisible = canControl;
        NavBar.Opacity = canControl ? 1 : 0.4;
    }

    private async void EnableWifi_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItem is not AdbDevice device)
            return;
        EnableWifiButton.IsEnabled = false;
        ShowStatus(InfoBarSeverity.Informational, "Attivazione Wi-Fi…", "Lascia collegato il cavo per qualche secondo.");
        var (ok, message, ip) = await _adb.EnableWirelessAsync(device.Serial);
        if (ok)
        {
            string address = $"{ip}:5555";
            _settings.RememberAddress(address);
            _settings.Save();
            AddressBox.Text = address;
            ShowStatus(InfoBarSeverity.Success, "Wi-Fi attivo", $"Connesso a {address}. Ora puoi scollegare il cavo USB.");
            await RefreshDevicesAsync(address);
        }
        else
        {
            ShowStatus(InfoBarSeverity.Error, "Impossibile passare al Wi-Fi", message);
            UpdateButtons();
        }
    }

    private void AddressBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_settings.RecentAddresses.Count > 0)
            AddressBox.IsSuggestionListOpen = true;
    }

    private void AddressBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string s)
            AddressBox.Text = s;
        _ = ConnectAsync(AddressBox.Text);
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => _ = ConnectAsync(AddressBox.Text);

    private async Task ConnectAsync(string address)
    {
        address = address.Trim();
        if (address.Length == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Indirizzo mancante", "Inserisci l'indirizzo IP del telefono, ad esempio 192.168.1.20:5555.");
            return;
        }
        if (!address.Contains(':'))
            address += ":5555";

        ShowStatus(InfoBarSeverity.Informational, "Connessione…", address);
        var (ok, message) = await _adb.ConnectAsync(address);
        if (ok)
        {
            _settings.RememberAddress(address);
            _settings.Save();
            ShowStatus(InfoBarSeverity.Success, "Connesso", address);
            await RefreshDevicesAsync(address);
        }
        else
        {
            ShowStatus(InfoBarSeverity.Error, "Connessione non riuscita",
                message + "\nVerifica che telefono e PC siano sulla stessa rete e che il debug wireless/ADB via rete sia attivo.");
        }
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(InfoBarSeverity.Informational, "Ricerca in rete…", "Assicurati che «Debug wireless» sia attivo sul telefono.");
        IReadOnlyList<string> found = await _adb.DiscoverWirelessAsync();
        if (found.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Nessun telefono trovato",
                "Se non hai mai associato questo PC, usa «Associa un nuovo telefono». Puoi anche inserire l'indirizzo a mano.");
            return;
        }
        AddressBox.Text = found[0];
        await ConnectAsync(found[0]);
    }

    private async void PairQr_Click(object sender, RoutedEventArgs e)
    {
        var pairing = new QrPairing(_adb);
        var image = new Image { Width = 240, Height = 240, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        using (var stream = new InMemoryRandomAccessStream())
        {
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pairing.CreateQrPng());
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var ring = new ProgressRing { IsActive = true, Width = 20, Height = 20 };
        var content = new StackPanel { Spacing = 14, MaxWidth = 380 };
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Sul telefono (Android 11 o successivo, connesso alla stessa rete Wi-Fi del PC):\n" +
                   "Impostazioni › Opzioni sviluppatore › Debug wireless › «Associa dispositivo con codice QR», " +
                   "poi inquadra questo codice.",
        });
        content.Children.Add(new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = image,
        });
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        statusRow.Children.Add(ring);
        statusRow.Children.Add(status);
        content.Children.Add(statusRow);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Associa con QR code",
            Content = content,
            CloseButtonText = "Annulla",
        };
        dialog.Closing += (_, _) => cts.Cancel();

        Task<string?> work = pairing.WaitPairAndConnectAsync(new Progress<string>(s => status.Text = s), cts.Token);
        Task<ContentDialogResult> shown = dialog.ShowAsync().AsTask();
        _ = work.ContinueWith(_ => DispatcherQueue.TryEnqueue(() => dialog.Hide()), TaskScheduler.Default);
        await shown;

        try
        {
            string? serial = await work;
            if (serial != null)
            {
                _settings.RememberAddress(serial);
                _settings.Save();
                AddressBox.Text = serial;
                ShowStatus(InfoBarSeverity.Success, "Telefono associato e connesso", serial);
            }
            else
            {
                ShowStatus(InfoBarSeverity.Success, "Telefono associato",
                    "Premi «Cerca telefoni in rete» o inserisci l'indirizzo mostrato nella schermata Debug wireless.");
            }
            await RefreshDevicesAsync(serial);
        }
        catch (OperationCanceledException)
        {
            // closed by the user (or timed out)
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Associazione con QR non riuscita", ex.Message);
        }
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        string pairAddress = PairAddressBox.Text.Trim();
        string code = PairCodeBox.Text.Trim();
        if (pairAddress.Length == 0 || code.Length == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Dati mancanti", "Inserisci indirizzo di associazione e codice.");
            return;
        }

        ShowStatus(InfoBarSeverity.Informational, "Associazione…", pairAddress);
        var (ok, message) = await _adb.PairAsync(pairAddress, code);
        if (!ok)
        {
            ShowStatus(InfoBarSeverity.Error, "Associazione non riuscita", message);
            return;
        }

        string connectAddress = PairConnectBox.Text.Trim();
        if (connectAddress.Length == 0)
        {
            // The connection port differs from the pairing port: try to find it via mDNS.
            await Task.Delay(1000);
            string host = pairAddress.Split(':')[0];
            connectAddress = (await _adb.DiscoverWirelessAsync()).FirstOrDefault(a => a.StartsWith(host + ":")) ?? "";
        }
        if (connectAddress.Length == 0)
        {
            ShowStatus(InfoBarSeverity.Success, "Associato",
                "Ora inserisci in «Connetti via Wi-Fi» l'indirizzo e la porta mostrati nella schermata Debug wireless.");
            return;
        }
        AddressBox.Text = connectAddress;
        await ConnectAsync(connectAddress);
    }

    // ===================================================================== mirroring

    private void PlatformSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool apple = sender.SelectedItem == AppleTab;
        AndroidPanel.Visibility = apple ? Visibility.Collapsed : Visibility.Visible;
        ApplePanel.Visibility = apple ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = apple ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null)
        {
            await StopSessionAsync(null);
            return;
        }
        if (DevicesList.SelectedItem is not AdbDevice device || _renderer == null)
            return;

        SaveQualitySettings();
        var options = new ScrcpyOptions
        {
            MaxSize = _settings.MaxSize,
            MaxFps = _settings.MaxFps,
            BitRate = _settings.BitRateMbps * 1_000_000,
            TurnScreenOff = _settings.TurnScreenOff,
        };

        _starting = true;
        UpdateButtons();
        ConnectingRing.IsActive = true;
        PlaceholderText.Text = $"Connessione a {device.DisplayName}…";
        StatusBar.IsOpen = false;

        var session = new ScrcpySession(_adb, device.Serial, options);
        try
        {
            _decoder = new H264Decoder(_renderer.Device);
            _decoder.FormatChanged += () => _renderer?.InvalidateInputs();
            session.SessionStarted += OnSessionStarted;
            session.VideoPacket += OnVideoPacket;
            session.Stopped += error => DispatcherQueue.TryEnqueue(() => _ = StopSessionAsync(error));

            await session.StartAsync(new Progress<string>(s => PlaceholderText.Text = s));
            _session = session;
            Title = $"Castino — {session.DeviceName}";
            PlaceholderText.Text = "In attesa del primo fotogramma…";
            VideoHost.Focus(FocusState.Programmatic);
            if (!_decoder.IsHardwareAccelerated)
                ShowStatus(InfoBarSeverity.Warning, "Decodifica software",
                    "La GPU non offre decodifica H.264 hardware: la latenza e l'uso della CPU saranno più alti.");
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            _decoder?.Dispose();
            _decoder = null;
            ConnectingRing.IsActive = false;
            PlaceholderText.Text = "Seleziona un telefono e premi «Avvia mirroring».";
            ShowStatus(InfoBarSeverity.Error, "Impossibile avviare il mirroring", ex.Message);
        }
        finally
        {
            _starting = false;
            UpdateButtons();
        }
    }

#if DEBUG
    private void StartTestPattern()
    {
        var session = new TestPatternSession();
        _decoder = new H264Decoder(_renderer!.Device);
        _decoder.FormatChanged += () => _renderer?.InvalidateInputs();
        session.SessionStarted += OnSessionStarted;
        session.VideoPacket += OnVideoPacket;
        _session = session;
        session.Start();
        UpdateButtons();
    }
#endif

    private void OnSessionStarted(int width, int height)
    {
        _frameWidth = width;
        _frameHeight = height;
        _renderer?.SetContentSize(width, height);
    }

    // Network thread: decode and present immediately, no frame queue.
    private bool _firstFrameShown;
    private void OnVideoPacket(byte[] data, long pts)
    {
        H264Decoder? decoder = _decoder;
        VideoRenderer? renderer = _renderer;
        if (decoder == null || renderer == null)
            return;
        try
        {
            decoder.Decode(data, pts, frame =>
            {
                renderer.Present(frame);
                if (!_firstFrameShown)
                {
                    _firstFrameShown = true;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        Placeholder.Visibility = Visibility.Collapsed;
                        ConnectingRing.IsActive = false;
                    });
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Decode error: " + ex);
        }
    }

    private async Task StopSessionAsync(string? error)
    {
        IMirrorSession? session = _session;
        _session = null;
        if (session != null)
            await session.DisposeAsync();

        _decoder?.Dispose();
        _decoder = null;
        _renderer?.Clear();
        _firstFrameShown = false;
        _frameWidth = _frameHeight = 0;
        _activePointers.Clear();

        Title = "Castino";
        Placeholder.Visibility = Visibility.Visible;
        ConnectingRing.IsActive = false;
        PlaceholderText.Text = "Seleziona un telefono e premi «Avvia mirroring».";
        if (error != null)
            ShowStatus(InfoBarSeverity.Error, "Mirroring interrotto", error);
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            ToggleFullscreen();
        UpdateButtons();
        _ = RefreshDevicesAsync();
    }

    private void SaveQualitySettings()
    {
        _settings.MaxSize = Resolutions[Math.Max(0, ResolutionCombo.SelectedIndex)].value;
        _settings.MaxFps = FpsValues[Math.Max(0, FpsCombo.SelectedIndex)];
        _settings.BitRateMbps = (int)BitrateSlider.Value;
        _settings.TurnScreenOff = ScreenOffToggle.IsOn;
        _settings.Save();
    }

    private void ScrollSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (ScrollSpeedCombo.SelectedIndex < 0)
            return;
        _settings.SmoothScroll = SmoothScrollToggle.IsOn;
        _settings.ScrollSpeed = ScrollSpeedCombo.SelectedIndex;
        _scroller.NotchFraction = ScrollSpeeds[ScrollSpeedCombo.SelectedIndex].fraction;
        _settings.Save();
    }

    private void ScreenOffToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.TurnScreenOff = ScreenOffToggle.IsOn;
        _settings.Save();
        _session?.Input?.SetScreenPower(!ScreenOffToggle.IsOn);
    }

    // ===================================================================== video surface

    private void VideoPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_renderer != null)
            return;
        try
        {
            _renderer = new VideoRenderer();
            var (w, h) = PanelPixelSize();
            IntPtr swapChain = _renderer.CreateSwapChain(w, h, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY);
            SwapChainPanelInterop.SetSwapChain(VideoPanel, swapChain);
#if DEBUG
            if (Environment.GetEnvironmentVariable("CASTINO_TEST_PATTERN") == "1")
                StartTestPattern();
#endif
        }
        catch (Exception ex)
        {
            _renderer?.Dispose();
            _renderer = null;
            ShowStatus(InfoBarSeverity.Error, "Errore grafico", "Impossibile inizializzare Direct3D 11: " + ex.Message);
        }
    }

    private void VideoPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeRenderer();

    private void VideoPanel_CompositionScaleChanged(SwapChainPanel sender, object args) => ResizeRenderer();

    private void ResizeRenderer()
    {
        if (_renderer == null)
            return;
        var (w, h) = PanelPixelSize();
        _renderer.Resize(w, h, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY);
    }

    private (uint, uint) PanelPixelSize()
    {
        double w = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX;
        double h = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY;
        return ((uint)Math.Max(1, Math.Round(w)), (uint)Math.Max(1, Math.Round(h)));
    }

    /// <summary>Maps a point in the video panel (DIPs) to device frame pixels; null when outside the picture.</summary>
    private (int x, int y)? MapToFrame(Point p, bool clamp)
    {
        int fw = _frameWidth, fh = _frameHeight;
        if (fw <= 0 || fh <= 0)
            return null;
        var rect = VideoRenderer.FitRect(fw, fh, VideoPanel.ActualWidth, VideoPanel.ActualHeight);
        double x = (p.X - rect.X) / rect.Width * fw;
        double y = (p.Y - rect.Y) / rect.Height * fh;
        bool inside = x >= 0 && y >= 0 && x < fw && y < fh;
        if (!inside && !clamp)
            return null;
        return ((int)Math.Clamp(x, 0, fw - 1), (int)Math.Clamp(y, 0, fh - 1));
    }

    // ===================================================================== pointer input

    private void Video_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        VideoHost.Focus(FocusState.Pointer);
        IRemoteInput? input = _session?.Input;
        if (input == null)
            return;

        var point = e.GetCurrentPoint(VideoPanel);
        var props = point.Properties;
        bool isMouse = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse;

        if (isMouse && props.IsRightButtonPressed)
        {
            input.Back();
            e.Handled = true;
            return;
        }
        if (isMouse && props.IsMiddleButtonPressed)
        {
            input.Key(AndroidKeys.Home);
            e.Handled = true;
            return;
        }
        if (isMouse && (props.IsXButton1Pressed || props.IsXButton2Pressed))
        {
            // Side buttons of the mouse: back / app switcher.
            if (props.IsXButton1Pressed)
                input.Back();
            else
                input.Key(AndroidKeys.AppSwitch);
            e.Handled = true;
            return;
        }

        _scroller.Cancel();

        var pos = MapToFrame(point.Position, clamp: false);
        if (pos == null)
            return;

        long pointerId = isMouse ? ControlMessages.PointerIdGenericFinger : e.Pointer.PointerId;
        _activePointers[e.Pointer.PointerId] = pointerId;
        VideoHost.CapturePointer(e.Pointer);
        input.Touch(AndroidKeys.ActionDown, pointerId, pos.Value.x, pos.Value.y, _frameWidth, _frameHeight, 1f);
        e.Handled = true;
    }

    private void Video_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        IRemoteInput? input = _session?.Input;
        if (input == null || !_activePointers.TryGetValue(e.Pointer.PointerId, out long pointerId))
            return;

        // Send every intermediate point (coalesced by the OS) for smooth swipes.
        foreach (var pt in e.GetIntermediatePoints(VideoPanel).Reverse())
        {
            var pos = MapToFrame(pt.Position, clamp: true);
            if (pos != null)
                input.Touch(AndroidKeys.ActionMove, pointerId, pos.Value.x, pos.Value.y, _frameWidth, _frameHeight, 1f);
        }
        e.Handled = true;
    }

    private void Video_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_activePointers.Remove(e.Pointer.PointerId, out long pointerId))
            return;
        IRemoteInput? input = _session?.Input;
        var pos = MapToFrame(e.GetCurrentPoint(VideoPanel).Position, clamp: true);
        if (input != null && pos != null)
            input.Touch(AndroidKeys.ActionUp, pointerId, pos.Value.x, pos.Value.y, _frameWidth, _frameHeight, 0f);
        VideoHost.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Video_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        IRemoteInput? input = _session?.Input;
        if (input == null)
            return;
        var point = e.GetCurrentPoint(VideoPanel);
        var pos = MapToFrame(point.Position, clamp: false);
        if (pos == null)
            return;

        // Touchpads send many small deltas, wheels send ±120 per notch.
        float notches = point.Properties.MouseWheelDelta / 120f;
        if (IsDown(VirtualKey.Control))
        {
            _scroller.Pinch(input, pos.Value.x, pos.Value.y, _frameWidth, _frameHeight, notches);
            e.Handled = true;
            return;
        }

        // Normalise to "scroll right" / "scroll up" amounts (Windows: vertical delta > 0 = wheel up,
        // horizontal delta > 0 = scroll right; Shift + wheel down scrolls right as in Windows apps).
        float right = 0, up = 0;
        if (point.Properties.IsHorizontalMouseWheel)
            right = notches;
        else if (IsDown(VirtualKey.Shift))
            right = -notches;
        else
            up = notches;

        if (_settings.SmoothScroll)
            _scroller.Scroll(input, pos.Value.x, pos.Value.y, _frameWidth, _frameHeight, right, up);
        else
            input.Scroll(pos.Value.x, pos.Value.y, _frameWidth, _frameHeight, right, up); // AXIS_HSCROLL/VSCROLL
        e.Handled = true;
    }

    // ===================================================================== keyboard input

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void Video_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        IRemoteInput? input = _session?.Input;
        if (input == null)
            return;

        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);
        int meta = (ctrl ? AndroidKeys.MetaCtrlOn : 0) | (shift ? AndroidKeys.MetaShiftOn : 0) | (alt ? AndroidKeys.MetaAltOn : 0);

        if (e.Key == VirtualKey.F11)
            return; // handled by the window

        if (ctrl && e.Key == VirtualKey.V)
        {
            // Paste the PC clipboard into the phone.
            e.Handled = true;
            DataPackageView content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
                input.Paste(await content.GetTextAsync());
            return;
        }

        if (alt && !ctrl)
        {
            Action? shortcut = e.Key switch
            {
                VirtualKey.H => () => input.Key(AndroidKeys.Home),
                VirtualKey.S => () => input.Key(AndroidKeys.AppSwitch),
                VirtualKey.N => input.ExpandNotifications,
                VirtualKey.Up => () => input.Key(AndroidKeys.VolumeUp),
                VirtualKey.Down => () => input.Key(AndroidKeys.VolumeDown),
                VirtualKey.R => input.Rotate,
                VirtualKey.P => () => input.Key(AndroidKeys.Power),
                _ => null,
            };
            if (shortcut != null)
            {
                shortcut();
                e.Handled = true;
                return;
            }
        }

        int keycode = e.Key switch
        {
            VirtualKey.Enter => AndroidKeys.Enter,
            VirtualKey.Back => AndroidKeys.Del,
            VirtualKey.Delete => AndroidKeys.ForwardDel,
            VirtualKey.Tab => AndroidKeys.Tab,
            VirtualKey.Up => AndroidKeys.DpadUp,
            VirtualKey.Down => AndroidKeys.DpadDown,
            VirtualKey.Left => AndroidKeys.DpadLeft,
            VirtualKey.Right => AndroidKeys.DpadRight,
            VirtualKey.Home => AndroidKeys.MoveHome,
            VirtualKey.End => AndroidKeys.MoveEnd,
            VirtualKey.PageUp => AndroidKeys.PageUp,
            VirtualKey.PageDown => AndroidKeys.PageDown,
            >= VirtualKey.A and <= VirtualKey.Z when ctrl => AndroidKeys.A + (e.Key - VirtualKey.A),
            _ => 0,
        };

        if (e.Key == VirtualKey.Escape)
        {
            input.Back();
            e.Handled = true;
        }
        else if (keycode != 0)
        {
            input.Key(keycode, meta);
            e.Handled = true;
        }
        // Printable characters arrive through CharacterReceived (respects the PC keyboard layout).
    }

    private void Video_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        IRemoteInput? input = _session?.Input;
        if (input == null || IsDown(VirtualKey.Control))
            return;

        char c = e.Character;
        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return;
        }
        if (c < 0x20 || c == 0x7F)
            return;

        string text = char.IsLowSurrogate(c) && _pendingHighSurrogate != 0
            ? new string(new[] { _pendingHighSurrogate, c })
            : c.ToString();
        _pendingHighSurrogate = '\0';
        input.Text(text);
        e.Handled = true;
    }

    // ===================================================================== nav bar

    private void Nav_Back(object sender, RoutedEventArgs e) => _session?.Input?.Back();
    private void Nav_Home(object sender, RoutedEventArgs e) => _session?.Input?.Key(AndroidKeys.Home);
    private void Nav_Recents(object sender, RoutedEventArgs e) => _session?.Input?.Key(AndroidKeys.AppSwitch);
    private void Nav_Notifications(object sender, RoutedEventArgs e) => _session?.Input?.ExpandNotifications();
    private void Nav_VolumeDown(object sender, RoutedEventArgs e) => _session?.Input?.Key(AndroidKeys.VolumeDown);
    private void Nav_VolumeUp(object sender, RoutedEventArgs e) => _session?.Input?.Key(AndroidKeys.VolumeUp);
    private void Nav_Rotate(object sender, RoutedEventArgs e) => _session?.Input?.Rotate();
    private void Nav_Power(object sender, RoutedEventArgs e) => _session?.Input?.Key(AndroidKeys.Power);
    private void Nav_Fullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        bool full = AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(full ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        SidebarColumn.Width = full ? new GridLength(0) : new GridLength(340);
        Sidebar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        VideoHost.Focus(FocusState.Programmatic);
    }

    // ===================================================================== misc

    /// <summary>Opens the window as large as comfortable (portrait phones need height) but always on screen.</summary>
    private void FitWindowToScreen()
    {
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)(work.Width * 0.75), 1600);
        int height = (int)(work.Height * 0.92);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SaveQualitySettings();
        IMirrorSession? session = _session;
        _session = null;
        if (session != null)
            await session.DisposeAsync();
        _scroller.Dispose();
        _decoder?.Dispose();
        _renderer?.Dispose();
    }
}
