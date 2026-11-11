// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Protocols.Ogc.Classic;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.OgcClassic.UnitTests;

public sealed class CapabilitiesLayerBuilderTests
{
    [UnitTest]
    public async Task TryBuildAsync_MetadataFails_RecordsOnlyLayerIdentityAndErrorClass()
    {
        var publicationId = Guid.NewGuid().ToString("N");
        var logger = new CapturingLogger();
        var samples = new List<(long Count, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == HonuaTelemetry.ServiceName &&
                instrument.Name == "honua.ogc.capabilities.omitted_layers")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(tag => Equals(tag.Value, publicationId)))
            {
                samples.Add((value, tags.ToArray()));
            }
        });
        listener.Start();

        var result = await CapabilitiesLayerBuilder.TryBuildAsync(
            () => Task.FromException<string>(new IOException("private exception details")), publicationId, logger);

        Assert.Null(result);
        var sample = Assert.Single(samples);
        Assert.Equal(1, sample.Count);
        Assert.Collection(sample.Tags,
            tag => Assert.Equal(new KeyValuePair<string, object?>(HonuaTelemetry.Tags.LayerId, publicationId), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("error.type", nameof(IOException)), tag));
        Assert.Equal($"OGC capabilities omitted layer {publicationId}: IOException", logger.Warning);
        Assert.Null(logger.Exception);
    }

    [UnitTest]
    public async Task TryBuildAsync_MetadataAvailable_ReturnsBuiltEntry()
    {
        var result = await CapabilitiesLayerBuilder.TryBuildAsync(
            () => Task.FromResult("healthy layer"), "healthy-publication", new CapturingLogger());

        Assert.Equal("healthy layer", result);
    }

    [UnitTest]
    public async Task TryBuildAsync_RequestCancelled_PropagatesCancellation()
    {
        var cancellation = new OperationCanceledException();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CapabilitiesLayerBuilder.TryBuildAsync(
                () => Task.FromException<string>(cancellation), "cancelled-publication", new CapturingLogger()));

        Assert.Same(cancellation, thrown);
    }

    [UnitTest]
    public async Task TryBuildAsync_OutOfMemory_PropagatesFailure()
    {
#pragma warning disable CA2201 // Inject a runtime failure to verify it escapes per-layer recovery.
        var failure = new OutOfMemoryException();
#pragma warning restore CA2201

        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            CapabilitiesLayerBuilder.TryBuildAsync(
                () => Task.FromException<string>(failure), "failed-publication", new CapturingLogger()));

        Assert.Same(failure, thrown);
    }

    private sealed class CapturingLogger : ILogger
    {
        public string? Warning { get; private set; }
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warning = formatter(state, exception);
                Exception = exception;
            }
        }
    }
}
