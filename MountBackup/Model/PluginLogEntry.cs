using System;

namespace MountBackup.Model {

    public enum PluginLogLevel {
        Info,
        Warning,
        Error
    }

    public class PluginLogEntry {
        public PluginLogEntry(DateTime timestamp, PluginLogLevel level, string message) {
            Timestamp = timestamp;
            Level = level;
            Message = message;
        }

        public DateTime Timestamp { get; }
        public PluginLogLevel Level { get; }
        public string Message { get; }
        public bool IsWarning => Level == PluginLogLevel.Warning;
        public bool IsError => Level == PluginLogLevel.Error;
    }
}
