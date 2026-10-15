using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace BeaconSetupManager.Services
{
    public class LogService : ILogService
    {
        public event Action<LogMessage>? OnMessageLogged;
        private readonly string _logDirectory;

        public LogService()
        {
            _logDirectory = Path.Combine(SettingsStore.DirectoryPath, "Logs");
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            string logFilePath = Path.Combine(_logDirectory, "app-.log");

            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        public void LogInfo(string category, string message)
        {
            message = Redact(message);
            Serilog.Log.ForContext("Category", category).Information("{LogMessage}", message);
            RaiseEvent(category, message, isError: false, isWarning: false);
        }

        public void LogWarning(string category, string message)
        {
            message = Redact(message);
            Serilog.Log.ForContext("Category", category).Warning("{LogMessage}", message);
            RaiseEvent(category, message, isError: false, isWarning: true);
        }

        public void LogError(string category, string message, Exception? exception = null)
        {
            message = Redact(message);
            if (exception != null)
            {
                string detail = Redact($"{message} | Exception: {exception.Message}\nStack Trace:\n{exception.StackTrace}");
                Serilog.Log.ForContext("Category", category).Error("{LogMessage}", detail);
                RaiseEvent(category, detail, isError: true, isWarning: false);
            }
            else
            {
                Serilog.Log.ForContext("Category", category).Error("{LogMessage}", message);
                RaiseEvent(category, message, isError: true, isWarning: false);
            }
        }

        public void OpenLogFolder()
        {
            try
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{_logDirectory}\"",
                    UseShellExecute = true
                };
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                LogWarning("Application Events", $"Could not open log folder in explorer: {ex.Message}");
            }
        }

        public static string Redact(string message)
        {
            message = System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\bauthorization\s*[:=]\s*(?:Bearer|Basic)\s+\S+", "Authorization=[redacted]");
            message = System.Text.RegularExpressions.Regex.Replace(message, @"(?i)--(password|pwd|token|secret|authorization)(?:=|\s+)(""[^""]*""|'[^']*'|\S+)", "--$1=[redacted]");
            message = System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\b(password|pwd|token|secret|authorization)\s*[:=]\s*(""[^""]*""|'[^']*'|[^;\s]+)", "$1=[redacted]");
            return System.Text.RegularExpressions.Regex.Replace(message, @"(?i)(https?://)[^/@\s]+@", "$1[redacted]@");
        }

        private void RaiseEvent(string category, string message, bool isError, bool isWarning)
        {
            OnMessageLogged?.Invoke(new LogMessage(DateTime.Now, category, message, isError, isWarning));
        }
    }
}
