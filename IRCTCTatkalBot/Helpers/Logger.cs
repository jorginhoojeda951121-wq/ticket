using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;

namespace IRCTCTatkalBot.Helpers
{
    public enum LogLevel { Debug, Info, Warning, Error }

    /// <summary>
    /// Thread-safe logger that writes to both a file and an in-memory queue
    /// (consumed by the UI's log view).
    /// </summary>
    public static class Logger
    {
        private static readonly string LogDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Logs");

        private static readonly string LogFile = Path.Combine(
            LogDir, $"tatkal_{DateTime.Now:yyyyMMdd}.log");

        private static readonly SemaphoreSlim _fileLock = new(1, 1);

        // UI consumers can subscribe to this event
        public static event Action<string, LogLevel>? OnLog;

        static Logger()
        {
            Directory.CreateDirectory(LogDir);
        }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-7}] {message}";

            // Notify UI (fire-and-forget, don't block caller)
            try { OnLog?.Invoke(entry, level); } catch { }

            // Write to file (async-safe)
            _ = WriteToFileAsync(entry);
        }

        public static void Debug(string msg) => Log(msg, LogLevel.Debug);
        public static void Info(string msg) => Log(msg, LogLevel.Info);
        public static void Warning(string msg) => Log(msg, LogLevel.Warning);
        public static void Error(string msg) => Log(msg, LogLevel.Error);
        public static void Error(Exception ex, string context = "") =>
            Log($"{context} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);

        private static async System.Threading.Tasks.Task WriteToFileAsync(string entry)
        {
            await _fileLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(LogFile, entry + Environment.NewLine);
            }
            catch { /* never crash on logging failure */ }
            finally
            {
                _fileLock.Release();
            }
        }
    }
}
