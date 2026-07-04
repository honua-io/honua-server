// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage (no Testcontainers/Docker needed) for the secure-connection resolution
/// failure path of <see cref="ExternalPostgisSinkExecutor"/> (#2404 PA-210): a resolver
/// exception must be logged, not silently swallowed, while still returning a sanitized
/// job failure to the caller.
/// </summary>
public sealed class ExternalPostgisSinkExecutorConnectionResolutionTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [Fact]
    public async Task ExecuteAsync_WhenResolverThrows_LogsFailureAndReturnsSanitizedError()
    {
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver
            .ResolveConnectionStringAsync("external-target", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("secret store unreachable")));
        var logger = new RecordingLogger<ExternalPostgisSinkExecutor>();
        var executor = new ExternalPostgisSinkExecutor(Options(), resolver, logger);

        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var input = BuildInputUri(
            new Feature(factory.CreatePoint(new Coordinate(13.405, 52.52)), new AttributesTable { { "name", "berlin" } }));

        var record = Record(
            ("input", input),
            ("connectionName", "external-target"),
            ("schema", "public"),
            ("table", "external_out"),
            ("targetSrid", "4326"));

        var result = await executor.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        Assert.Equal(ExecutionJobStatus.Failed, result.Status);
        Assert.Contains("secure connection could not be resolved", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret store unreachable", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Exception is InvalidOperationException { Message: "secret store unreachable" });
    }

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static string BuildInputUri(params IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static ExecutionJobRecord Record(params (string Name, string Value)[] inputs)
    {
        const string processId = ExternalPostgisSinkExecutor.HandledProcessId;
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

        return new ExecutionJobRecord
        {
            OperationId = "op-ext",
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
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
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
