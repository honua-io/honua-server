// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// CSV evidence-matrix renderer. Output is RFC 4180 compliant — quote-wrapped
/// fields, embedded quotes doubled, and a UTF-8 BOM so Excel opens it without
/// asking the user about encoding.
/// </summary>
internal sealed class CsvComplianceReportRenderer : IComplianceReportRenderer
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public ComplianceReportFormat Format => ComplianceReportFormat.Csv;

    public ComplianceReportArtifact Render(ComplianceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var buffer = new MemoryStream();
        using (var writer = new StreamWriter(buffer, Utf8WithBom, bufferSize: 4096, leaveOpen: true))
        {
            writer.WriteLine("Framework,ControlId,Title,Status,CollectedAt,Source,Claim,Detail");

            foreach (var row in snapshot.Controls)
            {
                if (row.Evidence.Count == 0)
                {
                    WriteRow(writer,
                        row.Control.Framework.ToString(),
                        row.Control.ControlId,
                        row.Control.Title,
                        row.Status.ToString(),
                        snapshot.GeneratedAt,
                        "synthetic",
                        "No evidence collected.",
                        string.Empty);
                    continue;
                }

                foreach (var evidence in row.Evidence)
                {
                    WriteRow(writer,
                        row.Control.Framework.ToString(),
                        row.Control.ControlId,
                        row.Control.Title,
                        evidence.Status.ToString(),
                        evidence.CollectedAt,
                        evidence.Source,
                        evidence.Claim,
                        evidence.Detail);
                }
            }
        }

        return new ComplianceReportArtifact
        {
            Format = ComplianceReportFormat.Csv,
            ContentType = "text/csv; charset=utf-8",
            FileExtension = "csv",
            FileNameStem = $"honua-compliance-{snapshot.GeneratedAt:yyyyMMddTHHmmssZ}",
            Content = buffer.ToArray(),
        };
    }

    private static void WriteRow(
        StreamWriter writer,
        string framework,
        string controlId,
        string title,
        string status,
        DateTimeOffset collectedAt,
        string source,
        string claim,
        string detail)
    {
        writer.Write(Quote(framework));
        writer.Write(',');
        writer.Write(Quote(controlId));
        writer.Write(',');
        writer.Write(Quote(title));
        writer.Write(',');
        writer.Write(Quote(status));
        writer.Write(',');
        writer.Write(Quote(collectedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        writer.Write(',');
        writer.Write(Quote(source));
        writer.Write(',');
        writer.Write(Quote(claim));
        writer.Write(',');
        writer.WriteLine(Quote(detail));
    }

    private static string Quote(string? value)
    {
        var v = value ?? string.Empty;
        var needsQuoting = v.IndexOfAny(['"', ',', '\r', '\n']) >= 0;

        if (!needsQuoting)
        {
            return v;
        }

        return string.Concat("\"", v.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }
}
