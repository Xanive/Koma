using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Koma;

public partial class MainWindow
{
    private readonly VideoHost originalVideo = new();
    private Mpv? reference;
    private bool compareOnLoad, compareStarting, referenceReady, syncing, detailZoom;
    private int compareRequest;
    private bool? referencePaused;
    private double referenceSpeed, zoom = double.NaN;
    private string? referenceSubtitle;
    private bool fullscreen;
    private WindowState restoreState;
    private Rect restoreBounds;
    private Popup? controlOverlay;
    private ContextMenu? transportMenu;
    private DispatcherTimer? controlsTimer;
    private Native.CursorPoint lastPointer;
    private DateTimeOffset controlsActivity;
    private bool overlayFading;

    private async void ToggleDetail(object sender, RoutedEventArgs e)
    {
        if (!referenceReady) return;
        detailZoom = !detailZoom;
        Detail.Content = detailZoom ? "Fit" : "Detail";
        await SyncComparison();
    }

    private async Task ToggleCompare()
    {
        if (reference is not null || compareStarting) { StopCompare(); return; }
        if (!loaded || playing is not { } episode) return;
        int attempt = ++compareRequest;
        compareStarting = true;
        Detail.IsEnabled = false;
        ReferenceSlot.Child = originalVideo;
        ReferenceWidth.Width = new(1, GridUnitType.Star);
        CompareHeight.Height = new(34);
        VideoSlot.Margin = new(2, 0, 0, 0);
        try
        {
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded);
            if (attempt != compareRequest) return;
            if (originalVideo.Handle == 0) throw new IOException("The original video surface is unavailable.");
            Mpv session = await Mpv.Start(Engine, originalVideo.Handle, "--no-audio", "--pause", "--sid=no",
                "--keep-open=always", "--focus-on=never",
                "--input-conf=" + Path.Combine(AppContext.BaseDirectory, "player-input.conf"),
                "--start=" + position.ToString(CultureInfo.InvariantCulture));
            if (attempt != compareRequest || closing) { session.Dispose(); return; }
            reference = session;
            session.Message += message => Dispatcher.BeginInvoke(async () =>
            {
                if (session != reference) return;
                switch (message.String("event"))
                {
                    case "file-loaded":
                        referenceReady = true;
                        Detail.IsEnabled = true;
                        await SyncComparison();
                        break;
                    case "end-file" when message.String("reason") == "error":
                        StopCompare();
                        SetStatus("The original view could not open this episode.");
                        break;
                    case "client-message":
                        if (message.Value("args") is { ValueKind: JsonValueKind.Array } args && args.GetArrayLength() == 2 &&
                            args[0].GetString() == "player-action") await RunAction(args[1].GetString() ?? "");
                        break;
                    case "property-change" when message.String("name") == "dropped-files":
                        await OpenNativeDrop(message.Value("data"));
                        break;
                }
            });
            session.Disconnected += () => Dispatcher.BeginInvoke(() =>
            {
                if (reference != session) return;
                StopCompare();
                SetStatus("The comparison view closed. The episode is still playing.");
            });
            await session.Command("observe_property", 1, "dropped-files");
            if (session == reference) await session.Command("loadfile", episode.Path, "replace");
        }
        catch (Exception error) when (error is IOException or Win32Exception or TimeoutException or ObjectDisposedException)
        {
            if (attempt != compareRequest) return;
            StopCompare();
            SetStatus("The comparison view could not start.");
        }
        finally { if (attempt == compareRequest) compareStarting = false; }
        UpdateTransport();
    }

    private void StopCompare()
    {
        compareRequest++;
        compareStarting = false;
        referenceReady = false;
        Mpv? previous = reference;
        reference = null;
        previous?.Dispose();
        referencePaused = null;
        referenceSpeed = 0;
        referenceSubtitle = null;
        if (detailZoom || !double.IsNaN(zoom) && zoom != 0)
            _ = Command("set_property", "video-zoom", 0);
        detailZoom = false;
        zoom = double.NaN;
        Detail.Content = "Detail";
        Detail.IsEnabled = false;
        ReferenceWidth.Width = new(0);
        CompareHeight.Height = new(0);
        VideoSlot.Margin = new(0);
        // Keep the native host attached. Reattaching a disposed HwndHost is unsafe.
        UpdateTransport();
    }

    private async Task SyncComparison()
    {
        await AdaptQuality();
        if (syncing || !loaded || !referenceReady || reference is not { } session || primary is not { } main) return;
        syncing = true;
        try
        {
            if (paused && referencePaused != true)
            {
                await session.Command("set_property", "pause", true);
                if (session != reference) return;
                referencePaused = true;
            }
            if (referenceSpeed != speed)
            {
                double requestedSpeed = speed;
                await session.Command("set_property", "speed", requestedSpeed);
                if (session != reference) return;
                referenceSpeed = requestedSpeed;
            }
            double a = (await main.Command("get_property", "time-pos")).Number();
            double b = (await session.Command("get_property", "time-pos")).Number();
            if (Math.Abs(a - b) > (paused ? .02 : .24))
                await session.Command("seek", a, "absolute+exact");
            if (!paused && referencePaused != false)
            {
                await session.Command("set_property", "pause", false);
                if (session != reference) return;
                referencePaused = false;
            }
            if (session != reference) return;
            double target = 0;
            if (detailZoom && videoWidth > 0 && videoHeight > 0)
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(VideoSlot);
                double fit = Math.Min(VideoSlot.ActualWidth * dpi.DpiScaleX / videoWidth,
                    VideoSlot.ActualHeight * dpi.DpiScaleY / videoHeight);
                if (fit > 0) target = Math.Min(3, Math.Max(.6, Math.Log2(1.4 / fit)));
            }
            if (double.IsNaN(zoom) || Math.Abs(target - zoom) > .015)
            {
                await session.Command("set_property", "video-zoom", target);
                if (session != reference) return;
                zoom = target;
                await main.Command("set_property", "video-zoom", target);
            }
            if (session == reference) await SyncReferenceSubtitle(session);
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException) { }
        finally { syncing = false; }
    }

    private async Task SyncReferenceSubtitle(Mpv session)
    {
        int subtitle = (int)selectedSubtitle.Number();
        string? external = tracks.ValueKind == JsonValueKind.Array && subtitle > 0
            ? tracks.EnumerateArray().FirstOrDefault(track => track.String("type") == "sub" &&
                track.Value("id").Number() == subtitle).String("external-filename")
            : null;
        string key = external ?? subtitle.ToString(CultureInfo.InvariantCulture);
        if (key == referenceSubtitle) return;
        try
        {
            if (external is not null) await session.Command("sub-add", external, "cached");
            else await session.Command("set_property", "sid", subtitle > 0 ? subtitle : "no");
        }
        catch (IOException)
        {
            // A sidecar can disappear after the primary session has loaded it.
            if (session == reference) await session.Command("set_property", "sid", "no");
        }
        if (session == reference) referenceSubtitle = key;
    }

    private void ToggleFullscreen()
    {
        transportMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        if (fullscreen)
        {
            fullscreen = false;
            controlsTimer?.Stop();
            VideoArea.SizeChanged -= UpdateOverlaySize;
            overlayFading = false;
            transport.BeginAnimation(OpacityProperty, null);
            if (controlOverlay is { } overlay)
            {
                overlay.IsOpen = false;
                overlay.Child = null;
            }
            controlOverlay = null;
            transport.PreviewKeyDown -= WindowKey;
            transport.Opacity = 1;
            transport.ClearValue(WidthProperty);
            ControlsDock.Children.Add(transport);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Left = restoreBounds.Left;
            Top = restoreBounds.Top;
            Width = restoreBounds.Width;
            Height = restoreBounds.Height;
            WindowState = restoreState;
            Header.Visibility = QueuePane.Visibility = Visibility.Visible;
            SetStatus(Status.Text);
        }
        else
        {
            fullscreen = true;
            DisplayPlayer();
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor);
            restoreState = WindowState;
            restoreBounds = WindowState == WindowState.Normal ? new(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (monitor.Bounds.Right > monitor.Bounds.Left)
                SetWindowPos(handle, 0, monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Right - monitor.Bounds.Left,
                    monitor.Bounds.Bottom - monitor.Bounds.Top, 0x14);
            else WindowState = WindowState.Maximized;
            Header.Visibility = QueuePane.Visibility = Status.Visibility = Visibility.Collapsed;
            ControlsDock.Children.Remove(transport);
            controlOverlay = new Popup {
                Placement = PlacementMode.Relative, PlacementTarget = VideoArea,
                AllowsTransparency = true, StaysOpen = true, Child = transport
            };
            VideoArea.SizeChanged += UpdateOverlaySize;
            transport.PreviewKeyDown += WindowKey;
            controlsTimer ??= MakeControlTimer();
            controlsTimer.Start();
            RevealControls();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RevealControls));
        }
        UpdateTransport();
    }

    private DispatcherTimer MakeControlTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) =>
        {
            if (!fullscreen || controlOverlay is not { } overlay) return;
            nint foreground = Native.GetForegroundWindow();
            if (foreground != 0 && !OwnsFullscreenForeground(foreground))
            {
                transportMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                overlay.IsOpen = false;
                overlayFading = false;
                return;
            }
            if (Native.GetCursorPos(out var cursor) && cursor != lastPointer)
            {
                lastPointer = cursor;
                Point local = PointFromScreen(new(cursor.X, cursor.Y));
                if (local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight) RevealControls();
            }
            if (seeking || transport.IsMouseCaptureWithin || transportMenu?.IsOpen == true)
                controlsActivity = DateTimeOffset.UtcNow;
            if (overlay.IsOpen && !overlayFading && DateTimeOffset.UtcNow - controlsActivity > TimeSpan.FromSeconds(Settings.FullscreenDelay))
            {
                overlayFading = true;
                AnimateControls(0, 160, () =>
                {
                    if (overlayFading && controlOverlay == overlay) overlay.IsOpen = false;
                });
            }
        };
        return timer;
    }

    private void RevealControls()
    {
        if (!fullscreen || controlOverlay is not { } overlay || closing) return;
        controlsActivity = DateTimeOffset.UtcNow;
        PositionControls(overlay);
        if (overlay.IsOpen && !overlayFading) return;
        overlayFading = false;
        if (!overlay.IsOpen)
        {
            transport.BeginAnimation(OpacityProperty, null);
            transport.Opacity = 0;
            overlay.IsOpen = true;
        }
        AnimateControls(1, 125);
    }

    private bool OwnsFullscreenForeground(nint foreground)
    {
        nint window = new WindowInteropHelper(this).Handle;
        return foreground == window || GetAncestor(foreground, 3) == window ||
            HwndSource.FromVisual(transport) is HwndSource controls && controls.Handle == foreground ||
            transportMenu is { IsOpen: true } menu &&
                HwndSource.FromVisual(menu) is HwndSource menuSource && menuSource.Handle == foreground;
    }

    private void UpdateOverlaySize(object sender, SizeChangedEventArgs e)
    {
        if (controlOverlay is { } overlay) PositionControls(overlay);
    }

    private void PositionControls(Popup overlay)
    {
        double width = VideoArea.ActualWidth;
        if (width <= 0 || VideoArea.ActualHeight <= 0) return;
        transport.Width = width;
        transport.Measure(new Size(width, double.PositiveInfinity));
        overlay.VerticalOffset = Math.Max(0, VideoArea.ActualHeight - transport.DesiredSize.Height);
    }

    private void AnimateControls(double target, int milliseconds, Action? completed = null)
    {
        double from = transport.Opacity;
        transport.BeginAnimation(OpacityProperty, null);
        transport.Opacity = target;
        if (!SystemParameters.ClientAreaAnimation)
        {
            completed?.Invoke();
            return;
        }
        var fade = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop };
        if (completed is not null) fade.Completed += (_, _) => completed();
        transport.BeginAnimation(OpacityProperty, fade);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Bounds, Work; public int Flags; }
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint handle, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
