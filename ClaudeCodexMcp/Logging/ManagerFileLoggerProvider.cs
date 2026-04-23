using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClaudeCodexMcp.Logging;

public sealed class ManagerFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly LogLevel _minimumLevel;
    private readonly object _gate = new();

    public ManagerFileLoggerProvider(string path, LogLevel minimumLevel)
    {
        _path = path;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ManagerFileLogger(categoryName, _path, _minimumLevel, _gate);
    }

    public void Dispose()
    {
    }

    private sealed class ManagerFileLogger(
        string categoryName,
        string path,
        LogLevel minimumLevel,
        object gate) : ILogger
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None && logLevel >= minimumLevel;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var record = new ManagerLogRecord(
                DateTimeOffset.UtcNow,
                logLevel.ToString(),
                categoryName,
                eventId.Id,
                eventId.Name,
                message,
                exception?.ToString());

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(record, SerializerOptions);
            lock (gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
    }

    private sealed record ManagerLogRecord(
        DateTimeOffset Timestamp,
        string Level,
        string Category,
        int EventId,
        string? EventName,
        string Message,
        string? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
