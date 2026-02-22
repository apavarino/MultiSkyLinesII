using System;
using System.IO;
using UnityEngine;

namespace MultiSkyLineII
{
    internal static class ModDiagnostics
    {
        private static readonly object Sync = new object();
        private static string _logFilePath;
        private static bool _initialized;

        public static string LogFilePath
        {
            get
            {
                EnsureInitialized();
                return _logFilePath;
            }
        }

        public static void ResetForNewSession()
        {
            EnsureInitialized();
            lock (Sync)
            {
                try
                {
                    File.WriteAllText(_logFilePath, string.Empty);
                }
                catch
                {
                }
            }
        }

        public static void Write(string message)
        {
            EnsureInitialized();
            lock (Sync)
            {
                try
                {
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                    File.AppendAllText(_logFilePath, line);
                }
                catch
                {
                }
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;

            lock (Sync)
            {
                if (_initialized)
                    return;

                try
                {
                    var logsDir = Path.Combine(Application.persistentDataPath, "Logs");
                    Directory.CreateDirectory(logsDir);
                    _logFilePath = Path.Combine(logsDir, "MultiSkyLineII.debug.log");
                }
                catch
                {
                    _logFilePath = "MultiSkyLineII.debug.log";
                }

                _initialized = true;
            }
        }
    }
}
