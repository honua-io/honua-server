// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;
using Honua.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventSource;
using Serilog;
using Xunit;

namespace Honua.Server.Tests.Infrastructure;

[Trait("Tier", "Fast")]
public sealed class EventSourceLoggerRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Forwarding_RepeatedEvents_RetainsOneLoggerPerCategory(bool concurrent)
    {
        var otherProvider = new CountingProvider();
        using var host = CreateHost(otherProvider);
        var provider = Assert.Single(host.Services.GetServices<ILoggerProvider>().OfType<CachedEventSourceLoggerProvider>());
        var logger = host.Services.GetRequiredService<Serilog.ILogger>()
            .ForContext("SourceContext", "Honua.Tests.RepeatedFeatures");
        const int eventCount = 4000;
        if (concurrent)
        {
            Parallel.For(0, eventCount, index => logger.Information("Feature request {Index}", index));
        }
        else
        {
            for (var index = 0; index < eventCount; index++)
            {
                logger.Information("Feature request {Index}", index);
            }
        }

        Assert.Equal(eventCount, otherProvider.EventCount);
        Assert.Single(RetainedLoggers(provider));
        Assert.Same(provider.CreateLogger("Honua.Tests.RepeatedFeatures"), provider.CreateLogger("Honua.Tests.RepeatedFeatures"));
        Assert.NotSame(provider.CreateLogger("Honua.Tests.RepeatedFeatures"), provider.CreateLogger("Honua.Tests.OtherCategory"));
        Assert.Equal(2, RetainedLoggers(provider).Count);
    }

    [Fact]
    public void Forwarding_EventSourceMessagesAndScopes_RemainObservableAndStopAfterDisposal()
    {
        var categoryPrefix = "Honua.Tests.EventDelivery." + Guid.NewGuid().ToString("N");
        using var listener = new CaptureListener(categoryPrefix);
        using var host = CreateHost(new CountingProvider());
        var provider = Assert.Single(host.Services.GetServices<ILoggerProvider>().OfType<CachedEventSourceLoggerProvider>());
        var logger = host.Services.GetRequiredService<Serilog.ILogger>()
            .ForContext("SourceContext", categoryPrefix + ".Messages");
        logger.Information("Forwarded marker {Value}", 42);
        Assert.Contains(listener.Events, entry => entry.Name == "FormattedMessage" && entry.Payload.Contains("Forwarded marker 42"));

        var eventLogger = provider.CreateLogger(categoryPrefix + ".Scope");
        using (eventLogger.BeginScope(new Dictionary<string, object> { ["request"] = "scope-marker" }))
        {
            eventLogger.Log(LogLevel.Information, new EventId(1), "Scoped marker", null, static (state, _) => state);
        }
        Assert.Contains(listener.Events, entry => entry.Name == "ActivityStart" && entry.Payload.Contains(categoryPrefix + ".Scope"));
        Assert.Contains(listener.Events, entry => entry.Name == "ActivityStop" && entry.Payload.Contains(categoryPrefix + ".Scope"));

        host.Dispose();
        provider.Dispose();
        Assert.False(eventLogger.IsEnabled(LogLevel.Critical));
        Assert.Throws<ObjectDisposedException>(() => provider.CreateLogger("after-dispose"));
        var eventTotal = listener.Events.Count;
        eventLogger.Log(LogLevel.Critical, new EventId(2), "Must not be emitted after shutdown", null, static (state, _) => state);
        Assert.Equal(eventTotal, listener.Events.Count);
    }

    [Fact]
    public void Registration_RepeatedCalls_PreservesAliasLifetimeAndCustomProviders()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging(logging => logging.AddEventSourceLogger());
        using var initialServices = services.BuildServiceProvider();
        var eventSource = initialServices.GetRequiredService<LoggingEventSource>();
        using var customInstance = new EventSourceLoggerProvider(eventSource);
        var instanceDescriptor = ServiceDescriptor.Singleton<ILoggerProvider>(customInstance);
        var factoryDescriptor = ServiceDescriptor.Singleton<ILoggerProvider>(_ => new EventSourceLoggerProvider(eventSource));
        var otherDescriptor = ServiceDescriptor.Singleton<ILoggerProvider, CountingProvider>();
        var keyedDescriptor = ServiceDescriptor.KeyedSingleton<ILoggerProvider, EventSourceLoggerProvider>("custom");
        services.Add(instanceDescriptor);
        services.Add(factoryDescriptor);
        services.Add(otherDescriptor);
        services.Add(keyedDescriptor);
        services.CacheEventSourceLoggersForSerilogForwarding();
        services.CacheEventSourceLoggersForSerilogForwarding();

        var cached = Assert.Single(services, descriptor => !descriptor.IsKeyedService && descriptor.ImplementationType == typeof(CachedEventSourceLoggerProvider));
        Assert.Equal(ServiceLifetime.Singleton, cached.Lifetime);
        Assert.Equal("EventSource", typeof(CachedEventSourceLoggerProvider).GetCustomAttribute<ProviderAliasAttribute>()?.Alias);
        Assert.Contains(instanceDescriptor, services);
        Assert.Contains(factoryDescriptor, services);
        Assert.Contains(otherDescriptor, services);
        Assert.Contains(keyedDescriptor, services);
        Assert.DoesNotContain(services, descriptor => !descriptor.IsKeyedService && descriptor.ImplementationType == typeof(EventSourceLoggerProvider));
    }

    private static IHost CreateHost(CountingProvider otherProvider) => new HostBuilder()
        .ConfigureLogging(logging => logging.ClearProviders().AddEventSourceLogger().AddProvider(otherProvider))
        .UseSerilog((_, configuration) => configuration.MinimumLevel.Information(), writeToProviders: true)
        .ConfigureServices(services => services.CacheEventSourceLoggersForSerilogForwarding())
        .Build();

    private static List<object> RetainedLoggers(CachedEventSourceLoggerProvider provider)
    {
        // Inspect the actual framework retaining owner, not a test-only production counter.
        var inner = typeof(CachedEventSourceLoggerProvider).GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
        var current = typeof(EventSourceLoggerProvider).GetField("_loggers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inner);
        var loggers = new List<object>();
        while (current is not null)
        {
            loggers.Add(current);
            current = current.GetType().GetProperty("Next")!.GetValue(current);
        }
        return loggers;
    }

    private sealed class CaptureListener(string categoryPrefix) : EventListener
    {
        public ConcurrentQueue<(string? Name, string[] Payload)> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Extensions-Logging")
            {
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)6);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name == "Microsoft-Extensions-Logging")
            {
                var payload = eventData.Payload?.Select(value => value?.ToString() ?? string.Empty).ToArray() ?? [];
                if (payload.Any(value => value.StartsWith(categoryPrefix, StringComparison.Ordinal)))
                {
                    Events.Enqueue((eventData.EventName, payload));
                }
            }
        }
    }

    private sealed class CountingProvider : ILoggerProvider
    {
        private int _eventCount;
        public int EventCount => Volatile.Read(ref _eventCount);
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CountingLogger(this);
        public void Dispose() { }

        private sealed class CountingLogger(CountingProvider owner) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Interlocked.Increment(ref owner._eventCount);
        }
    }
}
