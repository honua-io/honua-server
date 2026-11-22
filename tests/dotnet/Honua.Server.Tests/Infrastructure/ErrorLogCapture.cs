// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure;

// Keeps warnings and errors a test would otherwise lose, so a failed assertion can print them.
internal sealed class ErrorLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public string Diagnostics => string.Join(Environment.NewLine, _entries);

    public ILogger CreateLogger(string categoryName) => new CaptureLogger<object>(_entries, categoryName);

    public ILogger<TCategory> CreateLogger<TCategory>() => new CaptureLogger<TCategory>(_entries, typeof(TCategory).Name);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger<TCategory>(ConcurrentQueue<string> entries, string category) : ILogger<TCategory>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Enqueue(exception is null
                    ? $"{logLevel} {category}: {formatter(state, exception)}"
                    : $"{logLevel} {category}: {formatter(state, exception)}{Environment.NewLine}{exception}");
            }
        }
    }
}
