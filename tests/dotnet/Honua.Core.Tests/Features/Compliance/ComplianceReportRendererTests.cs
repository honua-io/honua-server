// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Compliance.Domain;
using Honua.Core.Features.Compliance.Services;

namespace Honua.Core.Tests.Features.Compliance;

/// <summary>
/// Unit tests for the CSV and PDF compliance renderers — covers the
/// "compliance report export produces PDF and CSV outputs" acceptance criterion.
/// </summary>
public sealed class ComplianceReportRendererTests
{
    [Fact]
    public void Csv_Renderer_ProducesBomPrefixedUtf8WithHeader()
    {
        var snapshot = BuildSampleSnapshot();
        var renderer = new CsvComplianceReportRenderer();

        var artifact = renderer.Render(snapshot);

        artifact.Format.Should().Be(ComplianceReportFormat.Csv);
        artifact.ContentType.Should().Be("text/csv; charset=utf-8");
        artifact.FileExtension.Should().Be("csv");
        artifact.Content.Length.Should().BeGreaterThan(3);

        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        artifact.Content.Take(3).Should().BeEquivalentTo(bom);

        var body = Encoding.UTF8.GetString(artifact.Content);
        body.Should().Contain("Framework,ControlId,Title,Status,CollectedAt,Source,Claim,Detail");
        body.Should().Contain("Soc2");
        body.Should().Contain("FedRamp");
    }

    [Fact]
    public void Csv_Renderer_EscapesQuotesAndCommas()
    {
        var snapshot = new ComplianceSnapshot
        {
            GeneratedAt = DateTimeOffset.Parse("2026-05-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ServerVersion = "test",
            Encryption = SampleEncryption(),
            Residency = SampleResidency(),
            Summary = new ComplianceReadinessSummary
            {
                Implemented = 0,
                PartiallyImplemented = 0,
                NotImplemented = 0,
                NotApplicable = 0,
                Unknown = 1,
                ReadinessPercent = 0,
            },
            Controls = new List<ComplianceControlEvidenceRow>
            {
                new()
                {
                    Control = new ComplianceControl
                    {
                        ControlId = "test.control",
                        Framework = ComplianceFramework.Soc2,
                        Title = "Title, with comma and \"quote\"",
                        Description = "desc",
                    },
                    Status = ComplianceControlStatus.Unknown,
                    Evidence = new List<ComplianceEvidence>
                    {
                        new()
                        {
                            ControlId = "test.control",
                            CollectedAt = DateTimeOffset.UtcNow,
                            Source = "test",
                            Claim = "Claim with \"embedded\" quotes",
                            Status = ComplianceControlStatus.Unknown,
                            Detail = "multi\nline",
                        },
                    },
                    Gaps = Array.Empty<string>(),
                },
            },
        };

        var artifact = new CsvComplianceReportRenderer().Render(snapshot);
        var body = Encoding.UTF8.GetString(artifact.Content);

        body.Should().Contain("\"Title, with comma and \"\"quote\"\"\"");
        body.Should().Contain("\"Claim with \"\"embedded\"\" quotes\"");
        body.Should().Contain("\"multi\nline\"");
    }

    [Fact]
    public void Pdf_Renderer_ProducesValidPdfHeaderAndTrailer()
    {
        var snapshot = BuildSampleSnapshot();
        var renderer = new PdfComplianceReportRenderer();

        var artifact = renderer.Render(snapshot);

        artifact.Format.Should().Be(ComplianceReportFormat.Pdf);
        artifact.ContentType.Should().Be("application/pdf");
        artifact.FileExtension.Should().Be("pdf");

        var body = Encoding.ASCII.GetString(artifact.Content);
        body.Should().StartWith("%PDF-1.4");
        body.Should().Contain("%%EOF");
        body.Should().Contain("/Type /Catalog");
        body.Should().Contain("/Type /Pages");
        body.Should().Contain("Helvetica");
    }

    [Fact]
    public void Pdf_Renderer_IncludesAllControlIds()
    {
        var snapshot = BuildSampleSnapshot();
        var renderer = new PdfComplianceReportRenderer();

        var artifact = renderer.Render(snapshot);
        var body = Encoding.ASCII.GetString(artifact.Content);

        foreach (var row in snapshot.Controls)
        {
            body.Should().Contain(row.Control.ControlId, $"PDF must surface control id {row.Control.ControlId}");
        }
    }

    [Fact]
    public void Pdf_Renderer_EscapesParensInText()
    {
        var snapshot = new ComplianceSnapshot
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ServerVersion = "test (build 1)",
            Encryption = SampleEncryption(),
            Residency = SampleResidency(),
            Summary = new ComplianceReadinessSummary
            {
                Implemented = 0,
                PartiallyImplemented = 0,
                NotImplemented = 0,
                NotApplicable = 0,
                Unknown = 0,
                ReadinessPercent = 0,
            },
            Controls = Array.Empty<ComplianceControlEvidenceRow>(),
        };

        var artifact = new PdfComplianceReportRenderer().Render(snapshot);
        var body = Encoding.ASCII.GetString(artifact.Content);

        // The literal "(build 1)" must be backslash-escaped inside the PDF text object.
        body.Should().Contain("\\(build 1\\)");
    }

    private static ComplianceSnapshot BuildSampleSnapshot()
    {
        var now = DateTimeOffset.Parse("2026-05-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return new ComplianceSnapshot
        {
            GeneratedAt = now,
            ServerVersion = "0.1.0-test",
            Encryption = SampleEncryption(),
            Residency = SampleResidency(),
            Summary = new ComplianceReadinessSummary
            {
                Implemented = 1,
                PartiallyImplemented = 1,
                NotImplemented = 0,
                NotApplicable = 0,
                Unknown = 0,
                ReadinessPercent = 50,
            },
            Controls = new List<ComplianceControlEvidenceRow>
            {
                new()
                {
                    Control = new ComplianceControl
                    {
                        ControlId = "soc2.cc6.1",
                        Framework = ComplianceFramework.Soc2,
                        Title = "Access controls",
                        Description = "test",
                    },
                    Status = ComplianceControlStatus.Implemented,
                    Evidence = new List<ComplianceEvidence>
                    {
                        new()
                        {
                            ControlId = "soc2.cc6.1",
                            CollectedAt = now,
                            Source = "dependency-gate",
                            Claim = "dependencies satisfied",
                            Status = ComplianceControlStatus.Implemented,
                            Detail = string.Empty,
                        },
                    },
                    Gaps = Array.Empty<string>(),
                },
                new()
                {
                    Control = new ComplianceControl
                    {
                        ControlId = "fedramp.sc-28",
                        Framework = ComplianceFramework.FedRamp,
                        Title = "Encryption at rest",
                        Description = "test",
                    },
                    Status = ComplianceControlStatus.PartiallyImplemented,
                    Evidence = new List<ComplianceEvidence>
                    {
                        new()
                        {
                            ControlId = "fedramp.sc-28",
                            CollectedAt = now,
                            Source = "encryption-posture",
                            Claim = "FIPS unverified",
                            Status = ComplianceControlStatus.PartiallyImplemented,
                            Detail = "fips-source=unverified",
                        },
                    },
                    Gaps = new List<string> { "FIPS 140-2 mode is not attested" },
                },
            },
        };
    }

    private static EncryptionPosture SampleEncryption() => new()
    {
        FipsMode = false,
        FipsSource = "unverified",
        Algorithms = new List<string> { "aes-256-gcm" },
        ActiveKeyVersion = 1,
        RetainedKeyVersions = 1,
    };

    private static DataResidencyPolicy SampleResidency() => new()
    {
        Enforced = false,
        PrimaryRegion = "unspecified",
        AllowedRegions = Array.Empty<string>(),
    };
}
