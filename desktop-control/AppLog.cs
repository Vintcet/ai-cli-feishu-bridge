using System.Text;

namespace CodexFeishuControl;

internal static class AppLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? logDirectory;
    private static string? logPath;
    private static DateTime lastThrottledWrite = DateTime.MinValue;

    public static void Initialize(string dataDirectory)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                logDirectory = dataDirectory;
                logPath = Path.Combine(dataDirectory, "control-panel.log");
                Info("控制面板日志已初始化。");
            }
            catch (Exception error)
            {
                logDirectory = null;
                logPath = null;
                System.Diagnostics.Trace.WriteLine(
                    $"AppLog init failed: {error.Message}");
            }
        }
    }

    public static string? LogFilePath => logPath;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception error)
    {
        Write(
            "ERROR",
            $"{message} {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
    }

    /// <summary>Writes a warning, but at most once per interval per message.</summary>
    public static void WarnThrottled(string message, TimeSpan interval)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - lastThrottledWrite < interval)
            {
                return;
            }
            lastThrottledWrite = DateTime.UtcNow;
        }
        Write("WARN", message);
    }

    private static void Write(string level, string message)
    {
        string? path;
        lock (Gate)
        {
            path = logPath;
            if (path is null)
            {
                return;
            }
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    var backup = Path.ChangeExtension(path, ".old.log");
                    try
                    {
                        File.Copy(path, backup, overwrite: true);
                    }
                    catch
                    {
                        // Best-effort rotation only.
                    }
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore rotation failures.
            }
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        try
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Logging must never break the panel.
        }
    }
}
