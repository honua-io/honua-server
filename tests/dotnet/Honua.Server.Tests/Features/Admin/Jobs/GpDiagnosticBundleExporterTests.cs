// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing.Diagnostics;
using Honua.Server.Features.Admin.Jobs;

namespace Honua.Server.Tests.Features.Admin.Jobs;

/// <summary>
/// The exporter reads the existing job diagnostics projection and still refuses input bytes
/// that nobody authorized.
/// </summary>
[Trait("Tier", "Fast")]
public sealed class GpDiagnosticBundleExporterTests
{
    private const string Bundle = """
        {
          "schemaVersion": "1.0",
          "contentClassification": "internal",
          "consent": { "redactionAcknowledged": true, "shareWithSupport": false },
          "envelopes": [ { "method": "GET", "normalizedPath": "/api/v1/admin/jobs/{id}/diagnostics" } ]
        }
        """;

    [Fact]
    public void Export_Projection_AttachesSanitizedEvidenceWithoutInputBytes()
    {
        var projection = Projection();
        projection = projection with
        {
            Identity = projection.Identity with
            {
                ResourceRefs = ["layer:parcels", "Host=db.internal;Username=app;Password=super-secret"]
            },
            Failure = projection.Failure! with { Message = "gdalwarp failed Password=super-secret" }
        };

        var json = GpDiagnosticBundleExporter.Export(Bundle, projection).ToJson();

        json.Should().NotContain("super-secret");
        json.Should().NotContain("contentBase64");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("schemaVersion").GetString().Should().Be("1.0");
        var evidence = document.RootElement.GetProperty("gpEvidence");
        evidence.GetProperty("schema").GetString().Should().Be(GpDiagnosticEvidence.SchemaId);
        evidence.GetProperty("jobId").GetString().Should().Be("job-7");
        evidence.GetProperty("runtime").GetProperty("serverVersion").GetString().Should().Be("2026.2.0-test");
        evidence.GetProperty("runtime").GetProperty("runtime").GetString().Should().Be("python");
        evidence.GetProperty("runtime").GetProperty("workerImage").GetString()
            .Should().Be("ghcr.io/honua-io/honua-gp-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        evidence.GetProperty("release").GetProperty("sourceRepository").GetString().Should().Be("honua-io/honua-samples");
        evidence.GetProperty("inputDataIncluded").GetBoolean().Should().BeFalse();
        var references = evidence.GetProperty("inputs").EnumerateArray().Select(item => item.GetProperty("reference").GetString()).ToArray();
        references.Should().Contain("layer:parcels");
        references.Should().NotContain(item => item != null && item.Contains("super-secret", StringComparison.Ordinal));
        document.RootElement.GetProperty("envelopes")[0].GetProperty("normalizedPath").GetString()
            .Should().Be("/api/v1/admin/jobs/{id}/diagnostics");
    }

    [Fact]
    public void Export_InputExportRequestedWithoutAuthorization_Refuses()
    {
        var inputs = new GpDiagnosticInputDraft[]
        {
            new()
            {
                Id = "parcels",
                Content = "parcels-ok Password=super-secret"u8.ToArray()
            }
        };

        var act = () => GpDiagnosticBundleExporter.Export(
            Bundle,
            Projection(),
            new GpDiagnosticInputExportRequest { IncludeInputData = true },
            inputs);

        var exception = act.Should().Throw<GpDiagnosticExportUnauthorizedException>().Which;
        exception.Message.Should().NotContain("super-secret");
        exception.Message.Should().NotContain("parcels-ok");
    }

    private static ConsoleJobDiagnostics Projection()
    {
        var now = new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        return new ConsoleJobDiagnostics
        {
            JobId = "job-7",
            CorrelationId = "corr-7",
            GeneratedAt = now,
            View = "operator",
            Identity = new ConsoleJobDiagnosticIdentity
            {
                Kind = "Geoprocessing",
                Backend = "local",
                TargetKind = "KubernetesJob",
                WorkloadName = "buffer",
                Priority = "Normal",
                ContractVersion = 1,
                ResourceRefs = []
            },
            Release = new ConsoleJobDiagnosticRelease
            {
                State = "partial",
                ServerVersion = "2026.2.0-test",
                Runtime = "python",
                SourceRepository = "honua-io/honua-samples",
                SourceRef = "0123456789abcdef0123456789abcdef01234567",
                WorkerImage = "ghcr.io/honua-io/honua-gp-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ProcessDefinitions = ["buffer-features"],
                Missing = ["release.dependencyLockDigest"]
            },
            Lifecycle = new ConsoleJobDiagnosticLifecycle
            {
                Status = "Failed",
                Phase = "failed",
                CreatedAt = now,
                UpdatedAt = now,
                TimingSource = "measured"
            },
            Attempt = new ConsoleJobDiagnosticAttempt
            {
                Number = 1,
                MaxAttempts = 1,
                Retry = new ConsoleJobDiagnosticRetry { Allowed = false, WouldRepeat = "would not retry" }
            },
            Worker = new ConsoleJobDiagnosticWorker
            {
                HeartbeatIntervalSeconds = 30,
                HeartbeatTimeoutSeconds = 90,
                Liveness = "finished",
                LivenessReason = "job is Failed"
            },
            Failure = new ConsoleJobDiagnosticFailure
            {
                Message = "gdalwarp failed",
                Classification = "execution_failed",
                Recovery = "fix the input"
            },
            Steps = [],
            Events = new ConsoleJobDiagnosticEvents
            {
                State = "available",
                Items =
                [
                    new ConsoleJobLogEntry
                    {
                        Timestamp = now,
                        Level = "error",
                        Message = "native step failed"
                    }
                ],
                TotalCount = 1,
                Limit = 50,
                Truncated = false
            },
            Artifacts = new ConsoleJobDiagnosticArtifacts
            {
                State = "none",
                Items = [],
                AvailableCount = 0,
                ExpiredCount = 0,
                UnavailableCount = 0
            },
            Evidence = new ConsoleJobDiagnosticEvidence
            {
                Missing = ["release.dependencyLockDigest: not recorded"],
                Truncated = [],
                Notes = []
            },
            Links = new ConsoleJobLinks
            {
                Self = "/api/v1/admin/jobs/job-7",
                Logs = "/api/v1/admin/jobs/job-7/logs",
                Artifacts = "/api/v1/admin/jobs/job-7/artifacts",
                Actions = "/api/v1/admin/jobs/job-7/actions",
                Steps = "/api/v1/admin/jobs/job-7/steps",
                Diagnostics = "/api/v1/admin/jobs/job-7/diagnostics"
            }
        };
    }
}
