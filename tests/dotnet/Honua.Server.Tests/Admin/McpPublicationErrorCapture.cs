// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Retains server exceptions for failed publication/readback assertions without
/// exposing internal exception details through the public MCP error envelope.
/// </summary>
internal sealed class McpPublicationErrorCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _exceptions = new();

    public string Diagnostics => string.Join(Environment.NewLine, _exceptions);

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(_exceptions);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(ConcurrentQueue<string> exceptions) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && exception is not null)
            {
                exceptions.Enqueue(exception.ToString());
            }
        }
    }
}
