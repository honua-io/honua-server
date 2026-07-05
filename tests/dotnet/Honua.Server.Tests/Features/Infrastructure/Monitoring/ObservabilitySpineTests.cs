// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Verifies the WS4 observability-spine additions (#2463): the GIS-aware serving-plane
/// request-duration histogram, the GP-plane execution queue-depth gauge, and the curated
/// Grafana dashboard bundle. Metric-emission tests subscribe through the shared
/// <c>Honua</c> meter so they prove the instruments reach the existing Prometheus/OTLP exporters.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
[Collection("HonuaTelemetry")]
public sealed class ObservabilitySpineTests
{
    private const string HonuaMeterName = "Honua";

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void RecordServingRequest_EmitsHistogram_WithProtocolOperationStatusClass()
    {
        var samples = new List<(double Value, Dictionary<string, object?> Tags)>();

        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HonuaMeterName
                    && instrument.Name == "honua_serving_request_duration_ms")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
                samples.Add((value, TagsToDictionary(tags))));
            listener.Start();

            HonuaTelemetry.RecordServingRequest(
                HonuaTelemetry.Protocols.FeatureServer, "query", statusCode: 200, durationMs: 42.5);
        }

        var sample = Assert.Single(samples);
        Assert.Equal(42.5, sample.Value);
        Assert.Equal("FeatureServer", sample.Tags[HonuaTelemetry.Tags.Protocol]);
        Assert.Equal("query", sample.Tags[HonuaTelemetry.Tags.Operation]);
        Assert.Equal("2xx", sample.Tags["status_class"]);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void RecordServingRequest_MissingProtocolOrNegativeDuration_IsSkipped()
    {
        var count = 0;

        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HonuaMeterName
                    && instrument.Name == "honua_serving_request_duration_ms")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref count));
            listener.Start();

            HonuaTelemetry.RecordServingRequest(protocol: "", operation: "query", statusCode: 200, durationMs: 10);
            HonuaTelemetry.RecordServingRequest(
                HonuaTelemetry.Protocols.FeatureServer, "query", statusCode: 200, durationMs: -1);
        }

        Assert.Equal(0, count);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ComputeQueueDepth_BucketsActiveJobs_ByStatusAndBackend_IgnoringTerminal()
    {
        var jobs = new List<ExecutionJobRecord>
        {
            Job(ExecutionJobStatus.Queued, "aws-batch"),
            Job(ExecutionJobStatus.Queued, "aws-batch"),
            Job(ExecutionJobStatus.Running, "aws-batch"),
            Job(ExecutionJobStatus.Provisioning, "local"),
            Job(ExecutionJobStatus.Succeeded, "aws-batch"),
            Job(ExecutionJobStatus.Failed, "local"),
        };

        var snapshot = ControlPlaneTelemetry.ComputeQueueDepth(jobs);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(2, EntryCount(snapshot, "Queued", "aws-batch"));
        Assert.Equal(1, EntryCount(snapshot, "Running", "aws-batch"));
        Assert.Equal(1, EntryCount(snapshot, "Provisioning", "local"));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ExecutionQueueDepthGauge_ObservesSnapshot_WithStatusAndBackendTags()
    {
        var jobs = new List<ExecutionJobRecord>
        {
            Job(ExecutionJobStatus.Queued, "aws-batch"),
            Job(ExecutionJobStatus.Running, "local"),
        };

        var measurements = new List<(int Value, Dictionary<string, object?> Tags)>();

        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HonuaMeterName
                    && instrument.Name == "honua.execution.queue.depth")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
                measurements.Add((value, TagsToDictionary(tags))));
            listener.Start();

            ControlPlaneTelemetry.UpdateQueueDepth(ControlPlaneTelemetry.ComputeQueueDepth(jobs));
            listener.RecordObservableInstruments();
        }

        Assert.Equal(2, measurements.Count);
        Assert.Contains(measurements, m =>
            (int)m.Value == 1
            && Equals(m.Tags[ControlPlaneTelemetry.Tags.ExecutionJobStatus], "Queued")
            && Equals(m.Tags[ControlPlaneTelemetry.Tags.Backend], "aws-batch"));
        Assert.Contains(measurements, m =>
            (int)m.Value == 1
            && Equals(m.Tags[ControlPlaneTelemetry.Tags.ExecutionJobStatus], "Running")
            && Equals(m.Tags[ControlPlaneTelemetry.Tags.Backend], "local"));

        // Reset shared static snapshot so a later observation does not leak into other tests.
        ControlPlaneTelemetry.UpdateQueueDepth(Array.Empty<ExecutionQueueDepthEntry>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void CuratedDashboards_AreWellFormedJson_WithRequiredFields()
    {
        var dashboardsDir = FindMonitoringDashboardsDirectory();
        string[] curated =
        [
            "honua-serving-overview.json",
            "honua-gp-jobs-overview.json",
            "honua-ops-alerts-overview.json",
        ];

        foreach (var name in curated)
        {
            var path = Path.Combine(dashboardsDir, name);
            Assert.True(File.Exists(path), $"Curated dashboard missing: {path}");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.True(root.TryGetProperty("uid", out var uid) && !string.IsNullOrWhiteSpace(uid.GetString()),
                $"{name} must declare a non-empty uid.");
            Assert.True(root.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()),
                $"{name} must declare a non-empty title.");
            Assert.True(root.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Array,
                $"{name} must declare a panels array.");
            Assert.True(panels.GetArrayLength() > 0, $"{name} must declare at least one panel.");
        }

        // Every dashboard JSON in the provisioning folder must at least parse, so a malformed
        // asset fails CI instead of silently breaking Grafana provisioning at deploy time.
        foreach (var path in Directory.EnumerateFiles(dashboardsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var exception = Record.Exception(() => JsonDocument.Parse(File.ReadAllText(path)).Dispose());
            Assert.True(exception is null, $"Malformed dashboard JSON: {path} ({exception?.Message})");
        }
    }

    private static int EntryCount(IReadOnlyList<ExecutionQueueDepthEntry> snapshot, string status, string backend)
    {
        foreach (var entry in snapshot)
        {
            if (entry.Status == status && entry.Backend == backend)
            {
                return entry.Count;
            }
        }

        return 0;
    }

    private static ExecutionJobRecord Job(ExecutionJobStatus status, string backend)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Status = status,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = backend,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "test-workload"
            }
        };
    }

    private static string FindMonitoringDashboardsDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName, "docker", "monitoring", "grafana", "dashboards");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate docker/monitoring/grafana/dashboards from the test execution directory.");
    }

    private static Dictionary<string, object?> TagsToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dictionary = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags)
        {
            dictionary[tag.Key] = tag.Value;
        }

        return dictionary;
    }
}
