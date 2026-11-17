using System.Diagnostics;

namespace TokenSaver.LogViewer;

/// <summary>Opens a viewer and manages its process. Faked in tests.</summary>
public interface IViewerLauncher
{
    /// <summary>Opens a viewer that connects to <paramref name="pipeName"/>. Returns once the launch was requested.</summary>
    void OpenTab(string title, string pipeName);

    bool IsRunning(int pid);

    void Kill(int pid);
}

/// <summary>
/// Opens the viewer as a tab of one Windows Terminal window, <see cref="WindowName"/>, so every watch shares a window.
/// Falls back to the viewer in its own console window when <c>wt.exe</c> is missing.
/// </summary>
public sealed class WindowsTerminalViewerLauncher : IViewerLauncher
{
    public const string WindowName = "tokensaver-logview";

    private readonly string _viewerPath;

    public WindowsTerminalViewerLauncher(string viewerPath)
    {
        _viewerPath = viewerPath;
    }

    public void OpenTab(string title, string pipeName)
    {
        if (!File.Exists(_viewerPath))
        {
            throw new FileNotFoundException($"The log viewer was not found: {_viewerPath}", _viewerPath);
        }

        var (file, lead) = _viewerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", new[] { _viewerPath })
            : (_viewerPath, Array.Empty<string>());
        string safeTitle = title.Replace(';', ',');

        if (OperatingSystem.IsWindows() && UserSessionProcess.IsServiceSession)
        {
            // A service cannot show windows: start Windows Terminal on the signed-in user's desktop.
            string wt = UserSessionProcess.ExpandForConsoleUser(@"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe");
            string tail = string.Join(' ', new[] { file }.Concat(lead).Concat(new[] { "--pipe", pipeName }).Select(Quote));
            _ = UserSessionProcess.Start($"{Quote(wt)} -w {WindowName} new-tab --title {Quote(safeTitle)} {tail}");
            return;
        }

        var start = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = false };
        foreach (string arg in new[] { "-w", WindowName, "new-tab", "--title", safeTitle, file }.Concat(lead).Concat(new[] { "--pipe", pipeName }))
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using Process? wt = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            var direct = new ProcessStartInfo { FileName = file, UseShellExecute = true };
            foreach (string arg in lead.Concat(new[] { "--pipe", pipeName }))
            {
                direct.ArgumentList.Add(arg);
            }

            using Process? process = Process.Start(direct);
        }
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    public bool IsRunning(int pid) => TryGetViewer(pid) is not null;

    public void Kill(int pid)
    {
        using Process? process = TryGetViewer(pid);
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    // Only a viewer (or dotnet running one) counts, so a recycled process id is never killed by mistake.
    private Process? TryGetViewer(int pid)
    {
        try
        {
            Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            string name = process.ProcessName;
            if (name.StartsWith("tokensaver-logview", StringComparison.OrdinalIgnoreCase)
                || (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && _viewerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                return process;
            }

            process.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
