// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage for the <see cref="RemoteSourceExecutor"/> wiring: it resolves the
/// matching <see cref="IDagFeatureSource"/> from a scope, builds the upstream
/// <see cref="DagSourceRequest"/> from the durable step inputs (including the
/// since/watermark pass-through), streams the source features, and publishes the
/// canonical FeatureCollection artifact. The source reader is faked so no transport runs.
/// </summary>
public sealed class RemoteSourceExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task RemoteSource_StreamsFeatures_AndPublishesFeatureCollection()
    {
        var fake = new FakeDagFeatureSource("source.ogc-features",
        [
            new DagSourceFeature
            {
                GeometryGeoJson = """{"type":"Point","coordinates":[1,2]}""",
                Attributes = new Dictionary<string, object?> { ["name"] = "a" }
            },
            new DagSourceFeature
            {
                GeometryGeoJson = null,
                Attributes = new Dictionary<string, object?> { ["name"] = "b" }
            }
        ]);

        var (status, uri, captured) = await RunAsync(
            fake,
            ("serviceUrl", "https://example.com/ogc"),
            ("collectionId", "buildings"),
            ("bbox", "0,0,10,10"),
            ("since", "2026-01-01T00:00:00Z"));

        status.Should().Be(ExecutionJobStatus.Succeeded);

        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
        features[0].Attributes.GetOptionalValue("name").Should().Be("a");
        features[1].Geometry.Should().BeNull();

        // The executor built the upstream request from the step inputs and passed the
        // watermark through unchanged.
        captured.Should().NotBeNull();
        captured!.ServiceUrl.Should().Be("https://example.com/ogc");
        captured.CollectionId.Should().Be("buildings");
        captured.Bbox.Should().Be("0,0,10,10");
        captured.Since.Should().Be("2026-01-01T00:00:00Z");
    }

    [UnitTest]
    public async Task RemoteSource_NoRegisteredSource_FailsCleanly()
    {
        // Register a source with a different id so resolution finds no match.
        var fake = new FakeDagFeatureSource("source.wfs", []);
        var executor = BuildExecutor("source.ogc-features", fake);

        var (status, _, _) = await RunExecutorAsync(executor, "source.ogc-features");
        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task RemoteSource_SecureConnectionResolverThrows_LogsInnerExceptionAndFailsCleanly()
    {
        // PA-211: the secure-connection resolution failure is wrapped in a
        // TransformInputException with a sanitized PublicMessage, but the original
        // exception must be preserved as InnerException and logged — not discarded —
        // so an operator can diagnose why the connection could not be resolved.
        var fake = new FakeDagFeatureSource("source.postgis", []);
        var resolver = Substitute.For<Honua.Core.Features.Security.Abstractions.ISecureConnectionResolver>();
        resolver
            .ResolveConnectionStringAsync("broken-connection", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("secret store unreachable")));

        var services = new ServiceCollection();
        services.AddSingleton(fake as IDagFeatureSource);
        services.AddSingleton(resolver);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);

        var logger = new RecordingLogger<RemoteSourceExecutor>();
        var executor = RemoteSourceExecutor.ForProcess("source.postgis", scopeFactory, monitor, logger);

        var (status, _, _) = await RunExecutorAsync(executor, "source.postgis", ("connectionName", "broken-connection"));

        status.Should().Be(ExecutionJobStatus.Failed);
        logger.Entries
            .Any(e => e.Exception is InvalidOperationException { Message: "secret store unreachable" })
            .Should().BeTrue("the original resolver exception must be logged, not discarded");
    }

    [UnitTest]
    public async Task RemoteSource_StreamingBytesExceedMaxArtifactBytes_FailsBeforeFullMaterialization()
    {
        // BH6-024 regression: the executor previously accumulated ALL features into a List<IFeature>
        // before checking payload.Length against MaxArtifactBytes. A single feature whose attributes
        // exceed a small ceiling must cause a Failed result during streaming, not after the full
        // list has already been materialized and serialized.
        //
        // "overflow-trigger" as an attribute value is ~16 bytes; together with the geometry GeoJSON
        // (~36 bytes), the estimate easily exceeds the 10-byte MaxArtifactBytes ceiling.
        var fake = new FakeDagFeatureSource("source.ogc-features",
        [
            new DagSourceFeature
            {
                GeometryGeoJson = """{"type":"Point","coordinates":[1,2]}""",
                Attributes = new Dictionary<string, object?> { ["name"] = "overflow-trigger" }
            }
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(fake as IDagFeatureSource);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var options = new GeoprocessingExecutorOptions { MaxArtifactBytes = 10 };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);

        var executor = RemoteSourceExecutor.ForProcess(
            "source.ogc-features",
            scopeFactory,
            monitor,
            NullLogger<RemoteSourceExecutor>.Instance);

        var (status, _, _) = await RunExecutorAsync(executor, "source.ogc-features",
            ("serviceUrl", "https://example.com/ogc"),
            ("collectionId", "test-collection"));

        status.Should().Be(ExecutionJobStatus.Failed,
            "the streaming byte-estimate guard must fire before the full feature list is serialized");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RemoteSourceExecutor BuildExecutor(string processId, IDagFeatureSource source)
    {
        var services = new ServiceCollection();
        services.AddSingleton(source);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);

        return RemoteSourceExecutor.ForProcess(
            processId,
            scopeFactory,
            monitor,
            NullLogger<RemoteSourceExecutor>.Instance);
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri, DagSourceRequest? Captured)> RunAsync(
        FakeDagFeatureSource source,
        params (string Name, string Value)[] inputs)
    {
        var executor = BuildExecutor(source.SourceId, source);
        var (status, uri, _) = await RunExecutorAsync(executor, source.SourceId, inputs);
        return (status, uri, source.LastRequest);
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri, object? Unused)> RunExecutorAsync(
        RemoteSourceExecutor executor,
        string processId,
        params (string Name, string Value)[] inputs)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");
        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        foreach (var (name, value) in inputs)
        {
            parameters[prefix + name] = value;
        }

        var record = new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri, null);
    }

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        return new GeoJsonReader().Read<FeatureCollection>(json).ToList();
    }

    private sealed class FakeDagFeatureSource : IDagFeatureSource
    {
        private readonly IReadOnlyList<DagSourceFeature> _features;

        public FakeDagFeatureSource(string sourceId, IReadOnlyList<DagSourceFeature> features)
        {
            SourceId = sourceId;
            _features = features;
        }

        public string SourceId { get; }

        public DagSourceRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
            DagSourceRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            foreach (var feature in _features)
            {
                yield return feature;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
