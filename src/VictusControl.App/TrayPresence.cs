using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace VictusControl.App;

/// <summary>
/// Keeps the app alive in the notification area after the window is closed.
/// The window is only ever hidden; <see cref="ExitRequested"/> is the one path
/// that actually ends the process, so the tray icon is always cleaned up and
/// never left behind as a ghost.
/// </summary>
public sealed class TrayPresence : IDisposable {

    private readonly NotifyIcon _icon;
    private bool _toldUserItKeepsRunning;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public TrayPresence() {
        _icon = new NotifyIcon {
            Icon = LoadIcon(),
            Text = "Victus Control",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Victus Control");
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => ShowRequested?.Invoke();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Quit");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    private static Icon LoadIcon() {
        try {
            var stream = Application.GetResourceStream(
                new Uri("pack://application:,,,/victus.ico"))?.Stream;
            if (stream != null) return new Icon(stream);
        } catch {
            // fall through to the stock icon rather than failing to start
        }
        return SystemIcons.Application;
    }

    /// <summary>
    /// Hover text: the two numbers worth knowing without opening the window.
    /// Windows truncates this at 63 characters.
    /// </summary>
    public void UpdateTooltip(double? cpuTempC, double? gpuTempC, string fanSummary) {
        string cpu = cpuTempC.HasValue ? $"{cpuTempC.Value:0}°C" : "--";
        string gpu = gpuTempC.HasValue ? $"{gpuTempC.Value:0}°C" : "--";
        string text = $"Victus Control\nCPU {cpu}  GPU {gpu}\n{fanSummary}";
        _icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    /// <summary>
    /// Called the first time the window is closed, so the app never simply
    /// vanishes without saying where it went.
    /// </summary>
    public void NoteStillRunning() {
        if (_toldUserItKeepsRunning) return;
        _toldUserItKeepsRunning = true;
        _icon.BalloonTipTitle = "Victus Control is still running";
        _icon.BalloonTipText = "Fan and power settings stay applied. " +
                               "Double-click the tray icon to reopen, or right-click to quit.";
        _icon.BalloonTipIcon = ToolTipIcon.None;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose() {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
