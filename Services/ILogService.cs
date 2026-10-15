using System;

namespace BeaconSetupManager.Services
{
    public record LogMessage(DateTime Timestamp, string Category, string Message, bool IsError, bool IsWarning);

    public interface ILogService
    {
        void LogInfo(string category, string message);
        void LogWarning(string category, string message);
        void LogError(string category, string message, Exception? exception = null);
        void OpenLogFolder();

        event Action<LogMessage>? OnMessageLogged;
    }
}
