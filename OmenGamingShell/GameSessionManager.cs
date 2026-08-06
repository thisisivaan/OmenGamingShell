using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OmenGamingShell;

public enum GameSessionState { Launching, Running, Closed, Failed }

public sealed class GameSessionManager : IDisposable
{
    private CancellationTokenSource? _sessionCancellation;
    private Process? _gameProcess;
    private DateTime _runningSinceUtc;
    public GameEntry? CurrentGame { get; private set; }
    public IntPtr GameWindowHandle { get; private set; }
    public bool IsActive => CurrentGame is not null;
    public event Action<GameEntry, GameSessionState, TimeSpan>? StateChanged;

    public async Task<bool> LaunchAsync(GameEntry game, ProcessStartInfo startInfo, IntPtr shellWindow)
    {
        if (IsActive)
        {
            if (CurrentGame?.Target.Equals(game.Target, StringComparison.OrdinalIgnoreCase) == true)
            {
                FocusGame();
                return false;
            }
            return false;
        }

        CurrentGame = game;
        StateChanged?.Invoke(game, GameSessionState.Launching, TimeSpan.Zero);
        var existingIds = Process.GetProcesses().Select(process => process.Id).ToHashSet();
        Process? started;
        try { started = Process.Start(startInfo); }
        catch
        {
            CurrentGame = null;
            StateChanged?.Invoke(game, GameSessionState.Failed, TimeSpan.Zero);
            throw;
        }

        _sessionCancellation = new CancellationTokenSource();
        var token = _sessionCancellation.Token;
        _gameProcess = await DiscoverGameProcessAsync(game, started, existingIds, shellWindow, token);
        if (_gameProcess is null)
        {
            CurrentGame = null;
            StateChanged?.Invoke(game, GameSessionState.Failed, TimeSpan.Zero);
            return false;
        }

        _runningSinceUtc = DateTime.UtcNow;
        ApplyPerformanceProfile(game);
        RefreshWindowHandle();
        StateChanged?.Invoke(game, GameSessionState.Running, TimeSpan.Zero);
        _ = MonitorSessionAsync(game, existingIds, shellWindow, token);
        return true;
    }

    private void ApplyPerformanceProfile(GameEntry game)
    {
        try
        {
            if (_gameProcess is null || _gameProcess.HasExited) return;
            _gameProcess.PriorityClass = game.PerformanceProfile switch
            {
                "Performance" => ProcessPriorityClass.High,
                "Quiet" => ProcessPriorityClass.BelowNormal,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch { }
    }

    public void FocusGame()
    {
        RefreshWindowHandle();
        if (GameWindowHandle == IntPtr.Zero) return;
        ShowWindow(GameWindowHandle, 9);
        SetForegroundWindow(GameWindowHandle);
    }

    private async Task MonitorSessionAsync(GameEntry game, HashSet<int> baseline, IntPtr shellWindow,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                if (_gameProcess is { HasExited: false }) { RefreshWindowHandle(); continue; }

                // Launchers and anti-cheat bootstrappers can exit before the real game appears.
                var replacement = await DiscoverGameProcessAsync(game, null, baseline, shellWindow, token,
                    TimeSpan.FromSeconds(8));
                if (replacement is not null) { _gameProcess = replacement; RefreshWindowHandle(); continue; }
                break;
            }
        }
        catch (OperationCanceledException) { return; }

        var duration = DateTime.UtcNow - _runningSinceUtc;
        PlayHistoryStore.Record(game, duration);
        CurrentGame = null;
        _gameProcess?.Dispose();
        _gameProcess = null;
        GameWindowHandle = IntPtr.Zero;
        StateChanged?.Invoke(game, GameSessionState.Closed, duration);
    }

    private static async Task<Process?> DiscoverGameProcessAsync(GameEntry game, Process? started,
        HashSet<int> baseline, IntPtr shellWindow, CancellationToken token, TimeSpan? timeout = null)
    {
        var expires = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(75));
        var directExecutable = game.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        while (DateTime.UtcNow < expires && !token.IsCancellationRequested)
        {
            if (directExecutable && started is not null)
            {
                try { if (!started.HasExited) return started; } catch { }
            }

            var candidate = await Task.Run(() => FindBestCandidate(game, baseline, shellWindow), token);
            if (candidate is not null) return candidate;
            await Task.Delay(500, token);
        }
        return null;
    }

    private static Process? FindBestCandidate(GameEntry game, HashSet<int> baseline, IntPtr shellWindow)
    {
        var wanted = Normalize(game.Name);
        var working = game.WorkingDirectory?.TrimEnd(Path.DirectorySeparatorChar);
        return Process.GetProcesses()
            .Where(process => !baseline.Contains(process.Id))
            .Select(process => (Process: process, Score: Score(process, wanted, working, shellWindow)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Process)
            .FirstOrDefault();
    }

    private static int Score(Process process, string wanted, string? working, IntPtr shellWindow)
    {
        try
        {
            if (process.HasExited || process.MainWindowHandle == shellWindow) return 0;
            var name = process.ProcessName.ToLowerInvariant();
            if (new[] { "steam", "epicgameslauncher", "upc", "ubisoftconnect", "eadesktop",
                    "ealauncher", "explorer", "applicationframehost" }.Contains(name)) return 0;
            var score = process.MainWindowHandle != IntPtr.Zero ? 25 : 0;
            var normalized = Normalize(name);
            if (wanted.Contains(normalized, StringComparison.Ordinal) ||
                normalized.Contains(wanted, StringComparison.Ordinal)) score += 60;
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(working) && !string.IsNullOrWhiteSpace(path) &&
                    path.StartsWith(working, StringComparison.OrdinalIgnoreCase)) score += 100;
            }
            catch { }
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var foregroundId);
            if (foregroundId == process.Id) score += 40;
            return score;
        }
        catch { return 0; }
    }

    private void RefreshWindowHandle()
    {
        try
        {
            _gameProcess?.Refresh();
            if (_gameProcess?.MainWindowHandle is { } handle && handle != IntPtr.Zero)
                GameWindowHandle = handle;
        }
        catch { }
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", string.Empty);
    public void Dispose() { _sessionCancellation?.Cancel(); _gameProcess?.Dispose(); }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
