using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace EventTicketBooking.Tests.Mocks
{
    public class FakeLogger<T> : ILogger<T>
    {
        public List<LogEntry> Logs { get; } = new List<LogEntry>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Logs.Add(new LogEntry
            {
                LogLevel = logLevel,
                Message = message,
                State = state
            });
        }
    }

    public class LogEntry
    {
        public LogLevel LogLevel { get; set; }
        public string Message { get; set; } = string.Empty;
        public object? State { get; set; }
    }
}
