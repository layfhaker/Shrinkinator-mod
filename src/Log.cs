using System;
using BepInEx.Logging;

namespace Shrinkinator
{
    /// <summary>
    /// Тонкая обёртка над логгером BepInEx, чтобы не таскать ManualLogSource по всем классам.
    /// </summary>
    internal static class Log
    {
        private static ManualLogSource _logger;

        internal static void Init(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal static void Info(string message)
        {
            _logger?.LogInfo(message);
        }

        internal static void Warning(string message)
        {
            _logger?.LogWarning(message);
        }

        internal static void Error(string message)
        {
            _logger?.LogError(message);
        }

        internal static void Error(string message, Exception exception)
        {
            _logger?.LogError(message + "\n" + exception);
        }
    }
}
