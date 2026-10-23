// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging.EventSource;

namespace Honua.Infrastructure.Logging;

internal static class SerilogEventSourceLoggerRegistration
{
    internal static void CacheEventSourceLoggersForSerilogForwarding(this IServiceCollection services)
    {
        // This adapter is specific to Serilog's direct provider forwarding, which
        // already bypasses Microsoft LoggerFactory filter-rule selection. EventSource
        // FilterSpecs target the original provider's full type name, so this is not
        // a general-purpose replacement for that provider under LoggerFactory.
        // Custom factories and instances retain their own ownership and behavior.
        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (!descriptor.IsKeyedService &&
                descriptor.ServiceType == typeof(ILoggerProvider) &&
                descriptor.ImplementationType == typeof(EventSourceLoggerProvider))
            {
                services[index] = ServiceDescriptor.Describe(
                    typeof(ILoggerProvider), typeof(CachedEventSourceLoggerProvider), descriptor.Lifetime);
            }
        }
    }
}

// Serilog's provider forwarding calls CreateLogger for every emitted event.
// The framework EventSource provider retains each logger in a linked list, so
// reuse the category logger just as the normal Microsoft LoggerFactory does.
[ProviderAlias("EventSource")]
internal sealed class CachedEventSourceLoggerProvider(LoggingEventSource eventSource) : ILoggerProvider
{
    private readonly EventSourceLoggerProvider _inner = new(eventSource);
    private readonly object _sync = new();
    private readonly Dictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private bool _disposed;

    public ILogger CreateLogger(string categoryName)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_loggers.TryGetValue(categoryName, out var logger))
            {
                logger = _inner.CreateLogger(categoryName);
                _loggers.Add(categoryName, logger);
            }

            return logger;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _loggers.Clear();
            _inner.Dispose();
        }
    }
}
