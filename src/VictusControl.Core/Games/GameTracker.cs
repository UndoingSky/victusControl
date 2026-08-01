using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using VictusControl.Core.Monitoring;

namespace VictusControl.Core.Games;

public sealed class GameRecord {
    public string Key { get; set; } = "";            // lowercased executable path
    public string Name { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public DateTime LastPlayedUtc { get; set; }
    public double TotalMinutes { get; set; }
    public int SessionCount { get; set; }
    public bool Hidden { get; set; }                  // user marked "not a game"

    // Set while a session is in progress; not persisted meaningfully
    public bool IsRunning { get; set; }
}

/// <summary>
/// Detects games by watching which processes actually drive the 3D engine, then
/// records how long each was played.
///
/// There is no reliable list of "what is a game" on Windows, so this uses the
/// same signal a human would: sustained 3D GPU work from a windowed application
/// that is not a known non-game. It learns from the moment it is installed —
/// sessions played before that cannot be recovered.
/// </summary>
public sealed class GameTracker {

    // Applications that legitimately use the 3D engine but are not games.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase) {
        "dwm", "explorer", "shellexperiencehost", "searchhost", "startmenuexperiencehost",
        "applicationframehost", "textinputhost", "systemsettings", "lockapp",
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "browser",
        "discord", "slack", "teams", "zoom", "whatsapp", "telegram", "spotify",
        "code", "devenv", "rider64", "idea64", "pycharm64", "sublime_text", "notepad++",
        "obs64", "obs32", "streamlabs obs", "nvidia share", "nvcontainer", "nvidia overlay",
        "photoshop", "illustrator", "afterfx", "premiere", "blender", "unity", "unrealeditor",
        "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient", "battle.net",
        "eadesktop", "ubisoftconnect", "riotclientux", "origin",
        "victuscontrol.app", "claude", "cursor", "windowsterminal", "powershell", "cmd",
        "taskmgr", "perfmon", "hwinfo64", "msiafterburner", "rtss", "omen gaming hub",
        "widgets", "phoneexperiencehost", "gamebar", "gamebarftserver", "xboxgamebar"
    };

    // A game has to hold the 3D engine for a while — a brief spike is a menu
    // animation or a video thumbnail, not a play session.
    private const double GpuThresholdPercent = 8.0;
    private const int ConfirmationsRequired = 2;

    private readonly string _storePath;
    private readonly Dictionary<string, GameRecord> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _candidateHits = new();
    private readonly Dictionary<int, (string key, DateTime startedUtc, DateTime lastSeenUtc)> _sessions = new();

    public GameTracker(string? storePath = null) {
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VictusControl", "games.json");
        Load();
    }

    /// <summary>
    /// Feeds one sample of per-process GPU usage. Call on the polling thread.
    /// Returns true when the library changed and the UI should refresh.
    /// </summary>
    public bool Observe(Dictionary<int, double> gpuByPid) {
        bool changed = false;
        var now = DateTime.UtcNow;
        var activeNow = new HashSet<int>();

        foreach (var (pid, usage) in gpuByPid) {
            if (usage < GpuThresholdPercent) continue;

            string? path = null;
            string name;
            try {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
                if (NotGames.Contains(name)) continue;
                try { path = p.MainModule?.FileName; } catch { /* 32/64-bit or access issues */ }
                // A game puts something on screen; background compute does not
                if (p.MainWindowHandle == IntPtr.Zero && !_sessions.ContainsKey(pid)) continue;
            } catch {
                continue;
            }

            // Require a couple of consecutive samples before believing it
            if (!_sessions.ContainsKey(pid)) {
                _candidateHits[pid] = _candidateHits.GetValueOrDefault(pid) + 1;
                if (_candidateHits[pid] < ConfirmationsRequired) continue;
            }

            string key = (path ?? name).ToLowerInvariant();
            activeNow.Add(pid);

            if (!_sessions.ContainsKey(pid)) {
                _sessions[pid] = (key, now, now);
                if (!_games.TryGetValue(key, out var rec)) {
                    rec = new GameRecord {
                        Key = key,
                        Name = FriendlyName(path, name),
                        ExecutablePath = path
                    };
                    _games[key] = rec;
                }
                rec.SessionCount++;
                rec.IsRunning = true;
                rec.LastPlayedUtc = now;
                changed = true;
            } else {
                var s = _sessions[pid];
                double minutes = (now - s.lastSeenUtc).TotalMinutes;
                if (_games.TryGetValue(s.key, out var rec)) {
                    rec.TotalMinutes += minutes;
                    rec.LastPlayedUtc = now;
                    rec.IsRunning = true;
                }
                _sessions[pid] = (s.key, s.startedUtc, now);
            }
        }

        // Close out sessions whose process stopped rendering or exited
        foreach (var pid in _sessions.Keys.ToList()) {
            if (activeNow.Contains(pid)) continue;
            bool stillAlive;
            try { using var p = Process.GetProcessById(pid); stillAlive = !p.HasExited; }
            catch { stillAlive = false; }

            // Give a running-but-idle game one grace period before ending the session
            if (stillAlive && (DateTime.UtcNow - _sessions[pid].lastSeenUtc).TotalSeconds < 60) continue;

            if (_games.TryGetValue(_sessions[pid].key, out var rec)) rec.IsRunning = false;
            _sessions.Remove(pid);
            _candidateHits.Remove(pid);
            changed = true;
        }

        foreach (var pid in _candidateHits.Keys.ToList())
            if (!gpuByPid.ContainsKey(pid)) _candidateHits.Remove(pid);

        if (changed) Save();
        return changed;
    }

    private static string FriendlyName(string? path, string processName) {
        if (path != null) {
            try {
                var info = FileVersionInfo.GetVersionInfo(path);
                foreach (var candidate in new[] { info.ProductName, info.FileDescription }) {
                    if (!string.IsNullOrWhiteSpace(candidate) && candidate.Trim().Length > 1)
                        return candidate.Trim();
                }
            } catch {
                // fall through to the process name
            }
        }
        // "witcher3" -> "Witcher3"; better than nothing when metadata is missing
        return processName.Length > 1
            ? char.ToUpperInvariant(processName[0]) + processName.Substring(1)
            : processName;
    }

    public IReadOnlyList<GameRecord> RecentlyPlayed(int max = 20) =>
        _games.Values
            .Where(g => !g.Hidden)
            .OrderByDescending(g => g.IsRunning)
            .ThenByDescending(g => g.LastPlayedUtc)
            .Take(max)
            .ToList();

    /// <summary>Marks a false positive as not a game, so it stops appearing.</summary>
    public void Forget(string key) {
        if (_games.TryGetValue(key, out var rec)) {
            rec.Hidden = true;
            Save();
        }
    }

    public void ClearAll() {
        _games.Clear();
        Save();
    }

    private void Load() {
        try {
            if (!File.Exists(_storePath)) return;
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<GameRecord>>(json);
            if (list == null) return;
            foreach (var g in list) {
                g.IsRunning = false;
                _games[g.Key] = g;
            }
        } catch {
            // a corrupt store must not stop the app from starting
        }
    }

    private void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath,
                JsonSerializer.Serialize(_games.Values.ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
        } catch {
            // losing history is preferable to crashing
        }
    }
}
