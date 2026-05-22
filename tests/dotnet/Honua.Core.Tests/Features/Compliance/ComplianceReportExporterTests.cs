// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.Compliance;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Honua.Core.Features.Compliance.Services;

namespace Honua.Core.Tests.Features.Compliance;

/// <summary>
/// Unit tests for <see cref="DefaultComplianceReportExporter"/>. The exporter
/// orchestrates a fresh evidence collection and dispatches to the right renderer.
/// </summary>
public sealed class ComplianceReportExporterTests
{
    [Fact]
    public async Task ExportAsync_Csv_ReturnsCsvArtifact()
    {
        var exporter = BuildExporter();

        var artifact = await exporter.ExportAsync(ComplianceReportFormat.Csv, CancellationToken.None);

        artifact.Format.Should().Be(ComplianceReportFormat.Csv);
        artifact.ContentType.Should().Be("text/csv; charset=utf-8");
        artifact.Content.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExportAsync_Pdf_ReturnsPdfArtifact()
    {
        var exporter = BuildExporter();

        var artifact = await exporter.ExportAsync(ComplianceReportFormat.Pdf, CancellationToken.None);

        artifact.Format.Should().Be(ComplianceReportFormat.Pdf);
        artifact.ContentType.Should().Be("application/pdf");
        artifact.Content.Length.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task ExportAsync_UnknownFormat_Throws()
    {
        var exporter = new DefaultComplianceReportExporter(
            new SingleControlCollector(),
            Array.Empty<IComplianceReportRenderer>());

        var act = async () => await exporter.ExportAsync(ComplianceReportFormat.Csv, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static DefaultComplianceReportExporter BuildExporter()
    {
        var renderers = new IComplianceReportRenderer[]
        {
            new CsvComplianceReportRenderer(),
            new PdfComplianceReportRenderer(),
        };

        return new DefaultComplianceReportExporter(new SingleControlCollector(), renderers);
    }

    private sealed class SingleControlCollector : IComplianceEvidenceCollector
    {
        public Task<ComplianceSnapshot> CollectAsync(CancellationToken cancellationToken = default)
        {
            var control = new ComplianceControl
            {
                ControlId = "soc2.cc6.1",
                Framework = ComplianceFramework.Soc2,
                Title = "Access controls",
                Description = "Test control.",
            };
            var row = new ComplianceControlEvidenceRow
            {
                Control = control,
                Status = ComplianceControlStatus.Implemented,
                Evidence = new List<ComplianceEvidence>
                {
                    new()
                    {
                        ControlId = control.ControlId,
                        CollectedAt = DateTimeOffset.UtcNow,
                        Source = "test",
                        Claim = "ok",
                        Status = ComplianceControlStatus.Implemented,
                    },
                },
                Gaps = Array.Empty<string>(),
            };

            return Task.FromResult(new ComplianceSnapshot
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                ServerVersion = "test",
                Encryption = new EncryptionPosture
                {
                    FipsMode = false,
                    FipsSource = "unverified",
                    Algorithms = new List<string> { "aes-256-gcm" },
                    ActiveKeyVersion = 1,
                    RetainedKeyVersions = 1,
                },
                Residency = new DataResidencyPolicy
                {
                    Enforced = false,
                    PrimaryRegion = "test",
                    AllowedRegions = Array.Empty<string>(),
                },
                Summary = new ComplianceReadinessSummary
                {
                    Implemented = 1,
                    PartiallyImplemented = 0,
                    NotImplemented = 0,
                    NotApplicable = 0,
                    Unknown = 0,
                    ReadinessPercent = 100,
                },
                Controls = new List<ComplianceControlEvidenceRow> { row },
            });
        }
    }
}
