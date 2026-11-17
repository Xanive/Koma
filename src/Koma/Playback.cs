using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Koma;

public partial class MainWindow
{
    private static readonly string Engine = Path.Combine(AppContext.BaseDirectory, "engine", "mpv.exe");
    private static readonly string ShaderDirectory = Path.Combine(AppContext.BaseDirectory, "engine", "shaders");
    private readonly VideoHost primaryVideo = new();
    private readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Mpv? primary;
    private Episode? playing;
    private int generation;
    private bool loaded, paused, muted, seeking, updating;
    private double position, duration, speed = 1;
    private JsonElement tracks, chapters, selectedSubtitle;
    private double lastDropCount, sampleMediaTime, sampleWallTime, ignoreDropsUntil;
    private int dropWindows, autoTier;
    private bool changingQuality;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private DateTimeOffset historySaved;

    private void ConfigurePlayback()
    {
        VideoSlot.Child = primaryVideo;
        transport.Back.Click += async (_, _) => await Command("seek", -Settings.SeekStep, "relative+exact");
        transport.Forward.Click += async (_, _) => await Command("seek", Settings.SeekStep, "relative+exact");
        transport.Play.Click += async (_, _) => await PlayPause();
        transport.Previous.Click += async (_, _) => await Move(-1);
        transport.Next.Click += async (_, _) => await Move(1);
        transport.Mute.Click += async (_, _) => await Command("cycle", "mute");
        transport.Fullscreen.Click += (_, _) => ToggleFullscreen();
        transport.Compare.Click += async (_, _) => await ToggleCompare();
        transport.Speed.Click += (_, _) => OpenMenu(transport.Speed, new[] { .5, .75, 1, 1.25, 1.5, 1.75, 2 }
            .Select(value => ($"{value:0.##}×", speed == value, (Func<Task>)(() => Command("set_property", "speed", value)))));
        transport.Audio.Click += (_, _) => TrackMenu(false);
        transport.Subtitles.Click += (_, _) => TrackMenu(true);
        transport.Chapters.Click += (_, _) => ChapterMenu();
        transport.Quality.Click += (_, _) => OpenMenu(transport.Quality, new[] { "Auto", "A", "B", "C", "Off" }
            .Select(quality => (quality is "A" or "B" or "C" ? UiText.Translate("Mode {0}", quality) : UiText.Translate(quality), Settings.Quality == quality,
                (Func<Task>)(async () =>
                {
                    Settings.Quality = quality;
                    autoTier = 0;
                    SaveSettings();
                    await ApplyQuality();
                }))));
        transport.Volume.ValueChanged += async (_, _) =>
        {
            if (updating) return;
            Settings.Volume = (int)Math.Round(transport.Volume.Value);
            if (loaded) await Command("set_property", "volume", Settings.Volume);
        };
        transport.Position.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => seeking = true));
        transport.Position.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, _) =>
        {
            double target = transport.Position.Value;
            seeking = false;
            await Seek(target);
        }));
        transport.Position.ValueChanged += async (_, _) =>
        {
            if (!updating && !seeking && loaded) await Seek(transport.Position.Value);
        };
        syncTimer.Tick += async (_, _) => await SyncComparison();
        syncTimer.Start();
        UpdateTransport();
    }

    private bool CanOpen(Episode episode)
    {
        if (closing) return false;
        if (!File.Exists(episode.Path)) SetStatus("This episode could not be found. Reconnect the drive or locate its folder.");
        else if (!File.Exists(Engine)) SetStatus("The playback engine is missing. Extract the full portable ZIP or run the installer again.");
        else return true;
        return false;
    }

    private async Task Play(Episode episode)
    {
        if (!CanOpen(episode)) return;
        Remember();
        bool turnCompareBackOn = reference is not null || compareStarting || compareOnLoad;
        ClosePlayback();
        startingPlayback = true;
        int current = generation;
        playing = episode;
        compareOnLoad = turnCompareBackOn;
        DisplayPlayer();
        QueueList.SelectedItem = episode;
        QueueList.ScrollIntoView(episode);
        NowPlaying.Text = episode.Name;
        SetStatus(null);
        UpdateTransport();

        try
        {
            if (primaryVideo.Handle == 0)
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded);
            if (primaryVideo.Handle == 0) throw new IOException("Playback surface unavailable.");
            string input = Path.Combine(AppContext.BaseDirectory, "player-input.conf");
            var options = new List<string> {
                "--input-conf=" + input, "--input-ar-delay=300", "--input-ar-rate=10", "--keep-open=no",
                "--gpu-shader-cache-dir=" + Path.Combine(Path.GetTempPath(), "Koma", "mpv-cache"),
                "--volume=" + Settings.Volume, "--volume-max=100", "--speed=" + speed.ToString(CultureInfo.InvariantCulture),
                "--mute=" + (muted ? "yes" : "no")
            };
            if (Settings.AudioLanguage.Length != 0) options.Add("--alang=" + Settings.AudioLanguage);
            if (Settings.SubtitleLanguage.Length != 0) options.Add("--slang=" + Settings.SubtitleLanguage);
            if (Settings.Resume && FindProgress(episode) is { Watched: false, Seconds: > 0 } progress)
                options.Add("--start=" + progress.Seconds.ToString(CultureInfo.InvariantCulture));
            Mpv session = await Mpv.Start(Engine, primaryVideo.Handle, [.. options]);
            if (closing || generation != current) { session.Dispose(); return; }
            primary = session;
            session.Message += value => Dispatcher.BeginInvoke(() =>
            {
                if (primary == session) PlaybackMessage(value);
            });
            session.Disconnected += () => Dispatcher.BeginInvoke(() =>
            {
                if (primary != session) return;
                Remember();
                ClosePlayback();
                playing = episode;
                NowPlaying.Text = episode.Name;
                if (fullscreen) ToggleFullscreen();
                SetStatus("Playback stopped unexpectedly. Select the episode to retry, or play the next one.");
                UpdateTransport();
            });
            int observer = 0;
            foreach (string property in new[] { "time-pos", "duration", "pause", "mute", "volume", "speed", "track-list",
                         "chapter-list", "sid", "frame-drop-count", "decoder-frame-drop-count", "video-params", "dropped-files" })
                await session.Command("observe_property", ++observer, property);
            await ApplyQuality();
            await session.Command("loadfile", episode.Path, "replace");
        }
        catch (Exception error) when (error is IOException or TimeoutException or Win32Exception or ObjectDisposedException)
        {
            if (generation != current || closing) return;
            loaded = false;
            primary?.Dispose();
            primary = null;
            SetStatus("Could not play this episode. Select another episode or press Next.");
            UpdateTransport();
        }
        finally
        {
            if (generation == current) { startingPlayback = false; UpdateArtworkActivity(); }
        }
    }

    private async void PlaybackMessage(JsonElement message)
    {
        switch (message.String("event"))
        {
            case "file-loaded":
                loaded = true;
                IgnoreDrops();
                if (compareOnLoad)
                {
                    compareOnLoad = false;
                    await ToggleCompare();
                }
                break;
            case "seek":
                IgnoreDrops();
                break;
            case "property-change":
                JsonElement data = message.Value("data");
                switch (message.String("name"))
                {
                    case "time-pos": if (data.ValueKind == JsonValueKind.Number) position = data.Number(); break;
                    case "duration": if (data.ValueKind == JsonValueKind.Number) duration = data.Number(); break;
                    case "pause": paused = data.ValueKind == JsonValueKind.True; break;
                    case "mute": muted = data.ValueKind == JsonValueKind.True; break;
                    case "volume": if (data.ValueKind == JsonValueKind.Number) Settings.Volume = (int)Math.Clamp(data.Number(), 0, 100); break;
                    case "speed": if (data.Number() > 0) speed = data.Number(); break;
                    case "track-list": tracks = data; break;
                    case "chapter-list": chapters = data; break;
                    case "sid": selectedSubtitle = data; break;
                    case "frame-drop-count": videoDrops = data.Number(); break;
                    case "decoder-frame-drop-count": decoderDrops = data.Number(); break;
                    case "video-params": videoWidth = data.Value("dw").Number(); videoHeight = data.Value("dh").Number(); break;
                    case "dropped-files":
                        await OpenNativeDrop(data);
                        return;
                }
                if (loaded && DateTimeOffset.UtcNow - historySaved > TimeSpan.FromSeconds(15)) Remember();
                break;
            case "client-message":
                if (message.Value("args") is { ValueKind: JsonValueKind.Array } args && args.GetArrayLength() == 2 &&
                    args[0].GetString() == "player-action")
                    await RunAction(args[1].GetString() ?? "");
                break;
            case "end-file":
                if (message.String("reason") == "eof")
                {
                    Remember(true);
                    loaded = false;
                    int index = playing is null ? -1 : queue.IndexOf(playing);
                    if (Settings.Autoplay && index >= 0 && index + 1 < queue.Count && File.Exists(queue[index + 1].Path))
                    {
                        await Play(queue[index + 1]);
                        return;
                    }
                    StopCompare();
                    if (fullscreen) ToggleFullscreen();
                    SetStatus(index + 1 < queue.Count ? "Episode finished. Next episode is ready." : "Episode finished.");
                    RenderEpisodes();
                    RenderContinue();
                }
                else if (message.String("reason") == "error")
                {
                    loaded = false;
                    StopCompare();
                    if (fullscreen) ToggleFullscreen();
                    SetStatus("This video could not be opened. Select another episode or press Next.");
                }
                break;
        }
        UpdateTransport();
    }

    private double videoDrops, decoderDrops, videoWidth, videoHeight;

    private void Remember(bool completed = false)
    {
        historySaved = DateTimeOffset.UtcNow;
        if (playing is null || !loaded || !completed && position < 1) return;
        bool alreadyWatched = FindProgress(playing) is { Watched: true };
        if (alreadyWatched && !completed) return;
        Settings.History[playing.Key] = new(completed || !Settings.Resume ? 0 : position, duration, completed, historySaved);
        SaveSettings();
    }

    private void ClosePlayback()
    {
        generation++;
        startingPlayback = false;
        compareOnLoad = false;
        StopCompare();
        primary?.Dispose();
        primary = null;
        playing = null;
        loaded = paused = seeking = false;
        position = duration = videoDrops = decoderDrops = videoWidth = videoHeight = 0;
        tracks = chapters = selectedSubtitle = default;
        autoTier = 0;
        NowPlaying.Text = "";
    }

    private async Task PlayPause()
    {
        if (loaded) await Command("cycle", "pause");
        else if ((playing ?? queue.FirstOrDefault()) is { } episode) await Play(episode);
    }

    private async Task Move(int step)
    {
        int index = (playing is null ? -1 : queue.IndexOf(playing)) + step;
        if ((uint)index >= (uint)queue.Count) return;
        opening?.Cancel();
        await Play(queue[index]);
    }

    private Task Seek(double target)
    {
        IgnoreDrops();
        return Command("seek", target, "absolute+exact");
    }

    private async Task Command(params object[] args)
    {
        if (primary is not { } session || !loaded) return;
        try { await session.Command(args); }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException)
        {
            if (session == primary) SetStatus("The player could not complete that action.");
        }
    }

    private void UpdateTransport()
    {
        updating = true;
        transport.Position.Maximum = Math.Max(1, duration);
        if (!seeking) transport.Position.Value = Math.Clamp(position, 0, transport.Position.Maximum);
        transport.Position.IsEnabled = loaded && duration > 0;
        transport.Time.Text = $"{FormatTime(seeking ? transport.Position.Value : position)} / {FormatTime(duration)}";
        transport.PlayIcon.Symbol = loaded && !paused ? Wpf.Ui.Controls.SymbolRegular.Pause24 : Wpf.Ui.Controls.SymbolRegular.Play24;
        transport.Play.ToolTip = UiText.Translate(loaded && !paused ? "Pause" : "Play");
        System.Windows.Automation.AutomationProperties.SetName(transport.Play, (string)transport.Play.ToolTip);
        transport.Play.IsEnabled = playing is not null || queue.Count > 0;
        int index = playing is null ? -1 : queue.IndexOf(playing);
        transport.Previous.IsEnabled = index > 0;
        transport.Next.IsEnabled = index + 1 < queue.Count;
        transport.Back.IsEnabled = transport.Forward.IsEnabled = transport.Speed.IsEnabled =
            transport.Audio.IsEnabled = transport.Subtitles.IsEnabled = transport.Compare.IsEnabled = loaded;
        transport.Fullscreen.IsEnabled = loaded || fullscreen;
        transport.Chapters.IsEnabled = loaded && chapters.ValueKind == JsonValueKind.Array && chapters.GetArrayLength() > 0;
        transport.Volume.Value = Settings.Volume;
        transport.MuteIcon.Symbol = muted ? Wpf.Ui.Controls.SymbolRegular.SpeakerMute24 : Wpf.Ui.Controls.SymbolRegular.Speaker224;
        transport.Mute.ToolTip = UiText.Translate(muted ? "Unmute" : "Mute");
        System.Windows.Automation.AutomationProperties.SetName(transport.Mute, (string)transport.Mute.ToolTip);
        transport.Mute.IsEnabled = loaded;
        transport.Speed.Content = $"{speed:0.##}×";
        transport.Quality.Content = UiText.Translate("Upscaling · {0}", UiText.Translate(Settings.Quality == "Auto" && loaded ? TierName : Settings.Quality));
        transport.Quality.ToolTip = UiText.Translate(Settings.Quality == "Auto" ? "Anime4K adjusts quality automatically to keep playback smooth" : "Anime4K upscaling quality");
        transport.More.IsEnabled = loaded;
        transport.Compare.Content = UiText.Translate(reference is null ? "Compare" : "Close compare");
        CompareTitle.Text = "Anime4K · " + UiText.Translate(Settings.Quality == "Auto" ? TierName : Settings.Quality);
        transport.FullscreenIcon.Symbol = fullscreen ? Wpf.Ui.Controls.SymbolRegular.FullScreenMinimize24 : Wpf.Ui.Controls.SymbolRegular.FullScreenMaximize24;
        transport.Fullscreen.ToolTip = UiText.Translate(fullscreen ? "Exit fullscreen" : "Fullscreen");
        transport.Queue.IsEnabled = !fullscreen;
        UpdateArtworkActivity();
        updating = false;
    }

    private static string FormatTime(double value)
    {
        var time = TimeSpan.FromSeconds(Math.Clamp(value, 0, 864000));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void OpenMenu(Button button, IEnumerable<(string Title, bool Selected, Func<Task> Action)> options)
    {
        if (!button.IsVisible) button = transport.More;
        var menu = new ContextMenu {
            PlacementTarget = button, Placement = PlacementMode.Top, Style = (Style)FindResource("PlaybackMenu")
        };
        foreach (var item in options)
        {
            var entry = new MenuItem { Header = item.Title, IsChecked = item.Selected, Style = menu.ItemContainerStyle };
            entry.Click += async (_, _) => await item.Action();
            menu.Items.Add(entry);
        }
        transportMenu = menu;
        menu.Closed += (_, _) => { if (transportMenu == menu) transportMenu = null; RevealControls(); };
        menu.IsOpen = true;
    }

    private void TrackMenu(bool subtitles)
    {
        string type = subtitles ? "sub" : "audio", property = subtitles ? "sid" : "aid";
        JsonElement[] available = tracks.ValueKind == JsonValueKind.Array
            ? tracks.EnumerateArray().Where(x => x.String("type") == type).ToArray() : [];
        var entries = new List<(string, bool, Func<Task>)> {
            (UiText.Translate("Off"), !available.Any(track => track.Value("selected").ValueKind == JsonValueKind.True),
                () => Command("set_property", property, "no"))
        };
        foreach (JsonElement track in available)
        {
            int id = (int)track.Value("id").Number();
            string title = track.String("title") ?? track.String("lang") ?? UiText.Translate("Track {0}", id);
            entries.Add((title, track.Value("selected").ValueKind == JsonValueKind.True, () => Command("set_property", property, id)));
        }
        if (subtitles)
            entries.Add((UiText.Translate("Open subtitle file…"), false, async () =>
            {
                var file = new OpenFileDialog { Filter = UiText.Translate("Subtitles") + "|*.srt;*.ass;*.ssa;*.vtt;*.sub|" + UiText.Translate("All files") + "|*.*" };
                if (file.ShowDialog(this) == true) await Command("sub-add", file.FileName, "select");
            }));
        OpenMenu(subtitles ? transport.Subtitles : transport.Audio, entries);
    }

    private void ChapterMenu()
    {
        if (chapters.ValueKind != JsonValueKind.Array) return;
        OpenMenu(transport.Chapters, chapters.EnumerateArray().Select((chapter, index) =>
        {
            double target = chapter.Value("time").Number();
            string name = chapter.String("title") ?? UiText.Translate("Chapter {0}", index + 1);
            return ($"{FormatTime(target)}   {name}", false, (Func<Task>)(() => Seek(target)));
        }));
    }

    private string TierName => autoTier switch { 0 => "Balanced", 1 => "Performance", _ => "Off" };

    private string[] Preset()
    {
        string quality = Settings.Quality;
        if (quality == "Off" || quality == "Auto" && autoTier >= 2) return [];
        if (quality is "Auto" or "A" or "B" or "C")
        {
            string mode = quality == "Auto" ? "A" : quality;
            string restore = mode == "B" ? "Restore_CNN_Soft" : "Restore_CNN";
            string upscale = mode == "C" ? "Upscale_Denoise_CNN_x2" : "Upscale_CNN_x2";
            string size = quality != "Auto" ? "VL" : autoTier == 0 ? "M" : "S";
            var files = new List<string> { "Clamp_Highlights" };
            if (mode != "C") files.Add($"{restore}_{size}");
            files.AddRange([$"{upscale}_{size}", "AutoDownscalePre_x2", "AutoDownscalePre_x4", "Upscale_CNN_x2_S"]);
            return files.Select(name => Path.Combine(ShaderDirectory, $"Anime4K_{name}.glsl")).ToArray();
        }
        return [];
    }

    private async Task ApplyQuality()
    {
        if (primary is not { } session) return;
        try
        {
            string[] files = Preset();
            if (files.Any(file => !File.Exists(file))) throw new IOException("Shaders missing.");
            await session.Command("set_property", "glsl-shaders", files);
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException)
        {
            if (session != primary) return;
            try { await session.Command("set_property", "glsl-shaders", Array.Empty<string>()); }
            catch (Exception fallbackError) when (fallbackError is IOException or TimeoutException or ObjectDisposedException) { }
            autoTier = 2;
            SetStatus("Anime4K could not start. Playing the original video.");
        }
        if (session != primary) return;
        IgnoreDrops();
        UpdateTransport();
    }

    private void IgnoreDrops()
    {
        ignoreDropsUntil = clock.Elapsed.TotalSeconds + 4;
        sampleMediaTime = position;
        sampleWallTime = 0;
        dropWindows = 0;
    }

    private async Task AdaptQuality()
    {
        double now = clock.Elapsed.TotalSeconds;
        if (!loaded || paused || changingQuality || Settings.Quality != "Auto" || autoTier >= 2 || now < ignoreDropsUntil) return;
        if (sampleWallTime == 0 || position < sampleMediaTime)
        {
            sampleWallTime = now;
            sampleMediaTime = position;
            lastDropCount = videoDrops + decoderDrops;
            return;
        }
        if (now - sampleWallTime < 3) return;
        double drops = videoDrops + decoderDrops;
        bool fallingBehind = position - sampleMediaTime > 1 && drops - lastDropCount >= 5;
        sampleWallTime = now;
        sampleMediaTime = position;
        lastDropCount = drops;
        dropWindows = fallingBehind ? dropWindows + 1 : 0;
        if (dropWindows < 2) return;
        autoTier++;
        changingQuality = true;
        try { await ApplyQuality(); }
        finally { changingQuality = false; }
    }

    private async Task RunAction(string key)
    {
        RevealControls();
        switch (key)
        {
            case "back": await Command("seek", -Settings.SeekStep, "relative+exact"); break;
            case "forward": await Command("seek", Settings.SeekStep, "relative+exact"); break;
            case "louder": await Command("add", "volume", Settings.VolumeStep); break;
            case "quieter": await Command("add", "volume", -Settings.VolumeStep); break;
            case "pause": await PlayPause(); break;
            case "fullscreen": if (loaded || fullscreen) ToggleFullscreen(); break;
            case "escape": if (fullscreen) ToggleFullscreen(); break;
            case "compare": await ToggleCompare(); break;
            case "mute": await Command("cycle", "mute"); break;
            case "open": await PickFiles(false); break;
            case "next": await Move(1); break;
            case "previous": await Move(-1); break;
        }
    }

    private async void WindowKey(object sender, KeyEventArgs e)
    {
        RevealControls();
        if (HomePane.IsVisible && e.Key != Key.O) return;
        if (QueueList.IsKeyboardFocusWithin && e.Key is Key.Up or Key.Down) return;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        string? action = e.Key switch {
            Key.Left => control ? "previous" : "back", Key.Right => control ? "next" : "forward",
            Key.Up => "louder", Key.Down => "quieter", Key.F or Key.F11 => "fullscreen", Key.Escape => "escape",
            Key.Space when e.OriginalSource is not Button => "pause", Key.C => "compare", Key.M => "mute", Key.O when control => "open",
            _ => null
        };
        if (action is null) return;
        e.Handled = true;
        await RunAction(action);
    }
}
