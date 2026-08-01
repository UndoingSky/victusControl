using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VictusControl.Core.Bios;
using VictusControl.Core.Games;
using VictusControl.Core.Monitoring;

namespace VictusControl.App;

public partial class MainWindow : Window {

    private readonly HpBiosControl _bios = new();
    private readonly SystemMonitor _monitor = new();
    private readonly GameTracker _games = new();
    private readonly TrayPresence _tray = new();

    private bool _biosAvailable;
    private bool _maxFanOn;
    private bool _reallyExiting;

    // Telemetry runs off the UI thread: reading fan RPM alone costs ~365 ms
    // because the embedded controller is slow to answer.
    private readonly CancellationTokenSource _pollCancel = new();
    private volatile bool _windowVisible = true;

    private string _fanSummary = "fans --";
    private GpuStats? _lastGpu;

    public MainWindow() {
        InitializeComponent();

        ThemeManager.Apply(ThemeManager.LoadPreference());

        // Reflect the saved theme only once the toggle's template exists: its
        // slide animation targets a named element inside that template, and
        // setting IsChecked in the constructor fires the trigger before there is
        // a namescope to resolve against.
        Loaded += (_, _) => {
            // Detach rather than guard with a flag: this is a state restore, not a
            // user action, and it must not report "theme applied" on every launch.
            ThemeSwitch.Checked -= OnThemeChanged;
            ThemeSwitch.Unchecked -= OnThemeChanged;
            ThemeSwitch.IsChecked = ThemeManager.Current == AppTheme.Light;
            ThemeSwitch.Checked += OnThemeChanged;
            ThemeSwitch.Unchecked += OnThemeChanged;
        };

        try {
            _bios.Initialize();
            _biosAvailable = true;
        } catch (Exception ex) {
            Say($"BIOS interface unavailable — {ex.Message}");
        }

        ElevationNote.Text = _biosAvailable
            ? ""
            : "Fan and power control need Administrator. Restart the app elevated.";

        InitGpuModeSupport();
        BuildSupportList();
        UpdateSliderLabels();

        AboutDetail.Text = "Fan limits measured on this machine: CPU 5400 rpm, GPU 5200 rpm. " +
                           "Game history is stored locally in your AppData folder and never uploaded.";

        // "--page games" opens straight to a section, so a screenshot or a bug
        // report can name a starting point instead of describing a click path.
        var args = Environment.GetCommandLineArgs();
        int pageArg = Array.IndexOf(args, "--page");
        if (pageArg >= 0 && pageArg + 1 < args.Length) {
            switch (args[pageArg + 1].ToLowerInvariant()) {
                case "games": NavGames.IsChecked = true; break;
                case "performance": NavPerformance.IsChecked = true; break;
                case "settings": NavSettings.IsChecked = true; break;
            }
        }

        _tray.ShowRequested += RestoreFromTray;
        _tray.ExitRequested += () => { _reallyExiting = true; Close(); };

        _ = PollLoop(_pollCancel.Token);
    }

    // ---------- Polling ----------

    private async Task PollLoop(CancellationToken ct) {
        var fanDue = DateTime.MinValue;
        var gpuDue = DateTime.MinValue;
        var stateDue = DateTime.MinValue;
        var heavyDue = DateTime.MinValue;   // process list + game detection

        while (!ct.IsCancellationRequested) {
            bool visible = _windowVisible;
            var now = DateTime.UtcNow;

            bool doFans = now >= fanDue;
            bool doGpu = now >= gpuDue;
            bool doState = now >= stateDue;
            bool doHeavy = now >= heavyDue;

            if (doFans) fanDue = now + TimeSpan.FromMilliseconds(visible ? 2000 : 10000);
            if (doGpu) gpuDue = now + TimeSpan.FromMilliseconds(visible ? 2000 : 10000);
            if (doState) stateDue = now + TimeSpan.FromMilliseconds(visible ? 3000 : 15000);
            // Game detection must keep running while hidden — that is when you play
            if (doHeavy) heavyDue = now + TimeSpan.FromMilliseconds(visible ? 3000 : 6000);

            try {
                var result = await Task.Run(() => Gather(doFans, doGpu, doState, doHeavy), ct)
                                       .ConfigureAwait(true);
                Apply(result);
            } catch (OperationCanceledException) {
                return;
            } catch {
                // one failed sample must not kill the loop
            }

            try {
                await Task.Delay(visible ? 1000 : 5000, ct).ConfigureAwait(true);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    private sealed class PollResult {
        public SystemSnapshot Snapshot { get; init; } = new();
        public byte[]? FanLevels { get; init; }
        public bool FanReadFailed { get; init; }
        public HpBiosData.Throttling? Throttling { get; init; }
        public bool? MaxFanOn { get; init; }
        public bool GamesChanged { get; init; }
    }

    /// <summary>Worker thread. Touches hardware, never the UI.</summary>
    private PollResult Gather(bool doFans, bool doGpu, bool doState, bool doHeavy) {
        byte[]? fans = null;
        bool fanFailed = false;
        HpBiosData.Throttling? throttling = null;
        bool? maxFan = null;

        if (doFans && _biosAvailable) {
            try { fans = _bios.GetFanLevel(); } catch { fanFailed = true; }
        }

        if (doState && _biosAvailable) {
            try { throttling = _bios.GetThrottling(); } catch { }
            try { maxFan = _bios.GetMaxFan(); } catch { }
        }

        Dictionary<int, double>? gpuByPid = null;
        IReadOnlyList<ProcessStats>? processes = null;
        bool gamesChanged = false;

        if (doHeavy) {
            gpuByPid = _monitor.GetGpuUsageByPid();
            gamesChanged = _games.Observe(gpuByPid);
            if (_windowVisible) processes = _monitor.GetTopProcesses(6, gpuByPid);
        }

        return new PollResult {
            Snapshot = new SystemSnapshot {
                CpuUsagePercent = _monitor.GetCpuUsagePercent(),
                CpuTempC = _monitor.GetCpuTemperatureC(),
                Gpu = doGpu ? _monitor.GetGpuStats() : null,
                Memory = _monitor.GetMemoryStats(),
                Drives = doHeavy ? _monitor.GetDrives() : null,
                Network = _monitor.GetNetworkStats(),
                TopProcesses = processes
            },
            FanLevels = fans,
            FanReadFailed = fanFailed,
            Throttling = throttling,
            MaxFanOn = maxFan,
            GamesChanged = gamesChanged
        };
    }

    /// <summary>Dispatcher thread. Display only.</summary>
    private void Apply(PollResult r) {
        var s = r.Snapshot;

        CpuRing.Value = s.CpuUsagePercent;
        CpuRing.DisplayText = $"{s.CpuUsagePercent:0}%";

        if (s.CpuTempC.HasValue) {
            CpuTempText.Text = $"{s.CpuTempC.Value:0}°C";
            SetSeverityChip(CpuTempBox, CpuTempText, Severity(s.CpuTempC.Value, 85, 95));
        } else {
            CpuTempText.Text = "--";
            SetSeverityChip(CpuTempBox, CpuTempText, 0);
        }

        var gpu = s.Gpu ?? _lastGpu;
        if (s.Gpu != null) _lastGpu = s.Gpu;

        if (gpu is { Available: true }) {
            GpuRing.Value = gpu.UtilizationPercent;
            GpuRing.DisplayText = $"{gpu.UtilizationPercent:0}%";
            GpuTempText.Text = $"{gpu.TemperatureC:0}°C";
            SetSeverityChip(GpuTempBox, GpuTempText, Severity(gpu.TemperatureC, 80, 87));
        } else {
            GpuRing.DisplayText = "--";
            GpuTempText.Text = "--";
            SetSeverityChip(GpuTempBox, GpuTempText, 0);
        }

        if (s.Memory != null) {
            RamRing.Value = s.Memory.UsedPercent;
            RamRing.DisplayText = $"{s.Memory.UsedPercent:0}%";
            RamText.Text = $"{s.Memory.UsedGb:0.0} GB / {s.Memory.TotalGb:0.0} GB";
        }

        if (s.Drives != null) BuildDrives(s.Drives);

        if (s.Network != null) {
            DownText.Text = $"{s.Network.DownloadMbps:0.0}";
            UpText.Text = $"{s.Network.UploadMbps:0.0}";
        }

        if (s.TopProcesses != null) BuildProcesses(s.TopProcesses);

        // Fans
        if (!_biosAvailable) {
            CpuFanText.Text = GpuFanText.Text = "--";
            _fanSummary = "needs administrator";
        } else if (r.FanReadFailed) {
            CpuFanText.Text = GpuFanText.Text = "--";
            _fanSummary = "fans unreadable";
        } else if (r.FanLevels != null) {
            int cpuRpm = HpBiosControl.LevelToRpm(r.FanLevels[0]);
            int gpuRpm = HpBiosControl.LevelToRpm(r.FanLevels[1]);
            CpuFanText.Text = $"{cpuRpm} rpm";
            GpuFanText.Text = $"{gpuRpm} rpm";
            _fanSummary = $"fans {cpuRpm}/{gpuRpm} rpm";
        }

        if (r.Throttling.HasValue) {
            bool on = r.Throttling.Value == HpBiosData.Throttling.On;
            ThrottleText.Text = r.Throttling.Value == HpBiosData.Throttling.Unknown
                ? "UNKNOWN" : on ? "YES" : "NO";
            SetSeverityChip(ThrottleBox, ThrottleText, on ? 2 : 0);
        }

        if (r.MaxFanOn.HasValue) {
            _maxFanOn = r.MaxFanOn.Value;
            MaxFanButton.IsChecked = _maxFanOn;
        }

        if (r.GamesChanged || GamesList.ItemsSource == null) BuildGames();

        _tray.UpdateTooltip(s.CpuTempC, gpu is { Available: true } ? gpu.TemperatureC : null, _fanSummary);
    }

    private static int Severity(double c, double warn, double danger) =>
        c >= danger ? 2 : c >= warn ? 1 : 0;

    /// <summary>
    /// Severity in a two-colour world: 0 leaves the reading plain, 1 lays a light
    /// stipple behind it, 2 inverts it to solid ink. Colour is not available, and
    /// dithering the glyphs themselves would destroy legibility.
    /// </summary>
    private void SetSeverityChip(Border box, TextBlock text, int severity) {
        switch (severity) {
            case >= 2:
                box.Background = (Brush)FindResource("Ink");
                text.Foreground = (Brush)FindResource("Paper");
                break;
            case 1:
                box.Background = (Brush)FindResource("Dither1");
                text.Foreground = (Brush)FindResource("Ink");
                break;
            default:
                box.Background = Brushes.Transparent;
                text.Foreground = (Brush)FindResource("Ink");
                break;
        }
    }

    // ---------- Composed lists ----------

    private void BuildDrives(IReadOnlyList<DriveStats> drives) {
        DrivesPanel.Children.Clear();
        foreach (var d in drives) {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            stack.Children.Add(new TextBlock {
                Text = d.Name,
                Style = (Style)FindResource("Label"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            var track = new Border {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("Track")
            };
            var fillGrid = new Grid();
            fillGrid.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(Math.Max(0.01, d.UsedPercent), GridUnitType.Star)
            });
            fillGrid.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(Math.Max(0.01, 100 - d.UsedPercent), GridUnitType.Star)
            });
            var fill = new Border {
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("AccentRamp")
            };
            Grid.SetColumn(fill, 0);
            fillGrid.Children.Add(fill);
            track.Child = fillGrid;
            stack.Children.Add(track);

            stack.Children.Add(new TextBlock {
                Text = $"{d.FreeGb:0.0} GB free of {d.TotalGb:0.0} GB",
                Style = (Style)FindResource("LabelMuted"),
                Margin = new Thickness(0, 6, 0, 0)
            });
            DrivesPanel.Children.Add(stack);
        }
    }

    private void BuildProcesses(IReadOnlyList<ProcessStats> processes) {
        var rows = new List<UIElement>();
        foreach (var p in processes) {
            var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            void Cell(string text, int col, bool primary) {
                var tb = new TextBlock {
                    Text = text,
                    Style = (Style)FindResource(primary ? "Label" : "LabelMuted"),
                    TextAlignment = col == 0 ? TextAlignment.Left : TextAlignment.Right,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                if (primary) tb.Foreground = (Brush)FindResource("TextPrimary");
                Grid.SetColumn(tb, col);
                grid.Children.Add(tb);
            }

            Cell(p.Name, 0, true);
            Cell($"{p.CpuPercent:0}%", 1, false);
            Cell(p.GpuPercent > 0 ? $"{p.GpuPercent:0}%" : "—", 2, false);
            Cell($"{p.MemoryMb:0} MB", 3, false);
            rows.Add(grid);
        }
        ProcessList.ItemsSource = rows;
    }

    private void BuildGames() {
        var games = _games.RecentlyPlayed();
        GamesEmpty.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var cards = new List<UIElement>();
        foreach (var g in games) {
            var card = new Border {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(18, 14, 18, 14)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock {
                Text = g.Name,
                Style = (Style)FindResource("CardTitle"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (g.IsRunning) {
                titleRow.Children.Add(new Border {
                    Background = (Brush)FindResource("Ink"),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 2, 8, 3),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock {
                        Text = "PLAYING",
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("Paper")
                    }
                });
            }
            titleStack.Children.Add(titleRow);
            titleStack.Children.Add(new TextBlock {
                Text = LastPlayedText(g),
                Style = (Style)FindResource("LabelMuted"),
                Margin = new Thickness(0, 3, 0, 0)
            });
            grid.Children.Add(titleStack);

            void Stat(string value, string label, int col) {
                var sp = new StackPanel { Margin = new Thickness(24, 0, 0, 0), MinWidth = 84 };
                sp.Children.Add(new TextBlock {
                    Text = value,
                    FontFamily = (FontFamily)FindResource("DisplayFace"),
                    FontSize = 19,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    TextAlignment = TextAlignment.Right
                });
                sp.Children.Add(new TextBlock {
                    Text = label,
                    Style = (Style)FindResource("LabelMuted"),
                    TextAlignment = TextAlignment.Right
                });
                Grid.SetColumn(sp, col);
                grid.Children.Add(sp);
            }

            Stat(PlaytimeText(g.TotalMinutes), "playtime", 1);
            Stat(g.SessionCount.ToString(), g.SessionCount == 1 ? "session" : "sessions", 2);

            var forget = new Button {
                Content = "Not a game",
                Style = (Style)FindResource("GhostButton"),
                Margin = new Thickness(24, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = g.Key
            };
            forget.Click += OnForgetGame;
            Grid.SetColumn(forget, 3);
            grid.Children.Add(forget);

            card.Child = grid;
            cards.Add(card);
        }
        GamesList.ItemsSource = cards;
    }

    private static string PlaytimeText(double minutes) {
        if (minutes < 1) return "<1m";
        if (minutes < 60) return $"{minutes:0}m";
        return $"{minutes / 60:0.0}h";
    }

    private static string LastPlayedText(GameRecord g) {
        if (g.IsRunning) return "Playing now";
        var span = DateTime.UtcNow - g.LastPlayedUtc;
        if (span.TotalMinutes < 2) return "Just now";
        if (span.TotalHours < 1) return $"{span.TotalMinutes:0} minutes ago";
        if (span.TotalHours < 24) return $"{span.TotalHours:0} hours ago";
        if (span.TotalDays < 30) return $"{span.TotalDays:0} days ago";
        return g.LastPlayedUtc.ToLocalTime().ToString("d MMM yyyy");
    }

    private void OnForgetGame(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string key }) {
            _games.Forget(key);
            BuildGames();
            Say("Removed from the list. It will not be detected as a game again.");
        }
    }

    private void OnClearGames(object sender, RoutedEventArgs e) {
        var confirm = MessageBox.Show(
            "Delete all recorded game history? This cannot be undone.",
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        _games.ClearAll();
        BuildGames();
        Say("Game history cleared.");
    }

    private void BuildSupportList() {
        void Row(string feature, bool supported, string note) {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Filled means available, hollow means not — the two-colour way of
            // saying yes and no without a colour to say it with.
            var dot = new StackPanel { Orientation = Orientation.Horizontal };
            dot.Children.Add(new Ellipse {
                Width = 9, Height = 9,
                Fill = (Brush)FindResource(supported ? "Ink" : "Paper"),
                Stroke = (Brush)FindResource("Ink"),
                StrokeThickness = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            });
            dot.Children.Add(new TextBlock {
                Text = feature,
                Style = (Style)FindResource("Label"),
                Foreground = (Brush)FindResource("TextPrimary")
            });
            grid.Children.Add(dot);

            var noteText = new TextBlock {
                Text = note,
                Style = (Style)FindResource("LabelMuted"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(noteText, 1);
            grid.Children.Add(noteText);
            SupportList.Children.Add(grid);
        }

        bool mux = false;
        if (_biosAvailable) { try { mux = _bios.IsGpuModeSupported(); } catch { } }

        Row("Fan telemetry", _biosAvailable, "Read from the embedded controller, in 100 rpm steps.");
        Row("Fan modes", _biosAvailable, "Default, Performance, Cool and Quiet BIOS curves.");
        Row("Manual fan speed", _biosAvailable, "Applies, then the BIOS curve reclaims the fans.");
        Row("Graphics power", _biosAvailable, "Minimum, Medium and Maximum TGP.");
        Row("CPU temperature", true, "ACPI thermal zone; HP's BIOS sensor returns zero on this model.");
        Row("GPU telemetry", true, "Read via nvidia-smi, which ships with the driver.");
        Row("Graphics mode switch", mux, mux
            ? "Supported. Requires a restart."
            : "Not available — this machine has no MUX switch and the BIOS rejects the command.");
    }

    // ---------- Navigation ----------

    private void OnNavChanged(object sender, RoutedEventArgs e) {
        if (PageVitals == null) return;   // fires during initial parse

        PageVitals.Visibility = NavVitals.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageGames.Visibility = NavGames.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PagePerformance.Visibility = NavPerformance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        PageHeading.Text =
            NavGames.IsChecked == true ? "Games" :
            NavPerformance.IsChecked == true ? "Performance Control" :
            NavSettings.IsChecked == true ? "Settings" : "System Vitals";

        if (NavGames.IsChecked == true) BuildGames();
    }

    // ---------- Theme ----------

    private void OnThemeChanged(object sender, RoutedEventArgs e) {
        ThemeManager.Apply(ThemeSwitch.IsChecked == true ? AppTheme.Light : AppTheme.Dark);
        Say(ThemeSwitch.IsChecked == true ? "Light theme applied." : "Dark theme applied.");
    }

    // ---------- Window chrome ----------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e) {
        if (e.ClickCount == 2) {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void RestoreFromTray() {
        Show();
        _windowVisible = true;
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // beat a full-screen game to the front,
        Topmost = false;  // then stop insisting
    }

    // ---------- Fans ----------

    private void OnFanModeDefault(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetFanMode(HpBiosData.FanMode.Default), "Fan mode set to Default.");

    private void OnFanModePerformance(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetFanMode(HpBiosData.FanMode.Performance), "Fan mode set to Performance.");

    private void OnFanModeCool(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetFanMode(HpBiosData.FanMode.Cool), "Fan mode set to Cool.");

    private void OnFanModeQuiet(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetFanMode(HpBiosData.FanMode.Quiet), "Fan mode set to Quiet.");

    private void OnFanSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateSliderLabels();

    private void UpdateSliderLabels() {
        if (CpuSliderText == null) return;
        CpuSliderText.Text = $"{HpBiosControl.LevelToRpm((byte)CpuFanSlider.Value)} rpm";
        GpuSliderText.Text = $"{HpBiosControl.LevelToRpm((byte)GpuFanSlider.Value)} rpm";
    }

    private void OnApplyManualFan(object sender, RoutedEventArgs e) {
        byte cpu = (byte)CpuFanSlider.Value;
        byte gpu = (byte)GpuFanSlider.Value;
        Send(() => _bios.SetFanLevel(cpu, gpu),
            $"Holding CPU {HpBiosControl.LevelToRpm(cpu)} rpm, GPU {HpBiosControl.LevelToRpm(gpu)} rpm — " +
            "the BIOS curve reclaims the fans in a few seconds.");
    }

    private void OnMaxFanToggle(object sender, RoutedEventArgs e) {
        bool target = MaxFanButton.IsChecked == true;
        Send(() => _bios.SetMaxFan(target),
            target ? "Max fan engaged and held until switched off." : "Max fan released.");
    }

    // ---------- Power ----------

    private void InitGpuModeSupport() {
        bool supported = false;
        if (_biosAvailable) {
            try { supported = _bios.IsGpuModeSupported(); } catch { supported = false; }
        }

        GpuHybridButton.IsEnabled = supported;
        GpuDiscreteButton.IsEnabled = supported;
        GpuModeNote.Text = supported
            ? "Switching graphics mode takes effect after a restart."
            : "This machine has no graphics switch — the BIOS rejects the command, so these controls are disabled rather than left to fail.";
    }

    private void OnGpuModeHybrid(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetGpuMode(HpBiosData.GpuMode.Hybrid), "Graphics mode set to Hybrid. Restart to apply.");

    private void OnGpuModeDiscrete(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetGpuMode(HpBiosData.GpuMode.Discrete), "Graphics mode set to Discrete. Restart to apply.");

    private void OnGpuPowerMin(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetGpuPower(HpBiosData.GpuPowerLevel.Minimum), "Graphics power set to Minimum.");

    private void OnGpuPowerMed(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetGpuPower(HpBiosData.GpuPowerLevel.Medium), "Graphics power set to Medium.");

    private void OnGpuPowerMax(object sender, RoutedEventArgs e) =>
        Send(() => _bios.SetGpuPower(HpBiosData.GpuPowerLevel.Maximum), "Graphics power set to Maximum.");

    // ---------- Command plumbing ----------

    private async void Send(Action hardware, string successMessage) {
        if (!_biosAvailable) {
            Say("The BIOS interface is not available, so nothing was sent. Restart as Administrator.");
            return;
        }

        Say("Sending…");
        try {
            await Task.Run(hardware).ConfigureAwait(true);
            Say(successMessage);
        } catch (Exception ex) {
            Say($"The machine refused that: {ex.Message}");
        }
    }

    private void Say(string message) {
        if (StatusText != null) StatusText.Text = message;
    }

    /// <summary>
    /// Closing parks the app in the tray so fan and power settings stay applied.
    /// Quit from the tray menu or the sidebar.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) {
        if (!_reallyExiting) {
            e.Cancel = true;
            Hide();
            _windowVisible = false;
            _tray.NoteStillRunning();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e) {
        _pollCancel.Cancel();
        _monitor.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }
}
