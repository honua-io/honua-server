// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// Minimal PDF 1.4 renderer for compliance reports. Generates a self-contained PDF
/// with no external dependencies — auditors and procurement only need the bytes,
/// not a typesetting library.
/// </summary>
/// <remarks>
/// <para>
/// The renderer emits a single page per control plus a summary cover page using
/// the PDF base font <c>Helvetica</c> (built into every conforming reader). Line
/// wrapping is approximate — this is an evidence document, not a print layout.
/// Avoiding QuestPDF, PdfSharp, etc. keeps the dependency footprint clean and
/// AOT-trim-safe.
/// </para>
/// <para>
/// The implementation hand-rolls PDF objects rather than streaming a content
/// model so the structure of the document stays auditable in one screen of code.
/// </para>
/// </remarks>
internal sealed class PdfComplianceReportRenderer : IComplianceReportRenderer
{
    private const string FontName = "Helvetica";
    private const int FontSize = 10;
    private const int LineHeight = 12;
    private const int PageWidth = 612;   // US Letter, points
    private const int PageHeight = 792;
    private const int MarginX = 54;
    private const int MarginYTop = 738;
    private const int MarginYBottom = 54;
    private const int CharsPerLine = 92;

    public ComplianceReportFormat Format => ComplianceReportFormat.Pdf;

    public ComplianceReportArtifact Render(ComplianceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var lines = BuildReportLines(snapshot);
        var pages = SplitIntoPages(lines);

        var pdf = BuildPdf(pages);

        return new ComplianceReportArtifact
        {
            Format = ComplianceReportFormat.Pdf,
            ContentType = "application/pdf",
            FileExtension = "pdf",
            FileNameStem = $"honua-compliance-{snapshot.GeneratedAt:yyyyMMddTHHmmssZ}",
            Content = pdf,
        };
    }

    private static List<string> BuildReportLines(ComplianceSnapshot snapshot)
    {
        var lines = new List<string>(256);

        lines.Add("Honua Compliance Readiness Report");
        lines.Add("=================================");
        lines.Add(string.Empty);
        lines.Add($"Generated: {snapshot.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z");
        lines.Add($"Server version: {snapshot.ServerVersion}");
        lines.Add(string.Empty);

        lines.Add("Readiness summary");
        lines.Add("-----------------");
        lines.Add($"Implemented: {snapshot.Summary.Implemented}");
        lines.Add($"Partially implemented: {snapshot.Summary.PartiallyImplemented}");
        lines.Add($"Not implemented: {snapshot.Summary.NotImplemented}");
        lines.Add($"Not applicable: {snapshot.Summary.NotApplicable}");
        lines.Add($"Unknown: {snapshot.Summary.Unknown}");
        lines.Add(string.Format(CultureInfo.InvariantCulture,
            "Implemented controls (of applicable): {0:0.##}%", snapshot.Summary.ReadinessPercent));
        lines.Add(string.Empty);

        lines.Add("Encryption posture");
        lines.Add("------------------");
        lines.Add($"FIPS mode: {(snapshot.Encryption.FipsMode ? "enabled" : "disabled")} ({snapshot.Encryption.FipsSource})");
        lines.Add($"Algorithms: {string.Join(", ", snapshot.Encryption.Algorithms)}");
        lines.Add($"Active key version: {snapshot.Encryption.ActiveKeyVersion}");
        lines.Add($"Retained key versions: {snapshot.Encryption.RetainedKeyVersions}");
        lines.Add(string.Empty);

        lines.Add("Data residency");
        lines.Add("--------------");
        lines.Add($"Enforced: {snapshot.Residency.Enforced}");
        lines.Add($"Primary region: {snapshot.Residency.PrimaryRegion}");
        lines.Add($"Allowed regions: {string.Join(", ", snapshot.Residency.AllowedRegions)}");
        lines.Add(string.Empty);

        lines.Add("Control evidence");
        lines.Add("----------------");
        foreach (var row in snapshot.Controls)
        {
            lines.Add(string.Empty);
            lines.Add($"[{row.Control.Framework}] {row.Control.ControlId} — {row.Control.Title}");
            lines.Add($"Status: {row.Status}");
            AppendWrapped(lines, $"Description: {row.Control.Description}");

            if (row.Gaps.Count > 0)
            {
                lines.Add("Gaps:");
                foreach (var gap in row.Gaps)
                {
                    AppendWrapped(lines, $"  - {gap}");
                }
            }

            if (row.Evidence.Count > 0)
            {
                lines.Add("Evidence:");
                foreach (var evidence in row.Evidence)
                {
                    AppendWrapped(lines, $"  - [{evidence.Source}] {evidence.Claim}");
                    if (!string.IsNullOrEmpty(evidence.Detail))
                    {
                        AppendWrapped(lines, $"      detail: {evidence.Detail}");
                    }
                }
            }
        }

        return lines;
    }

    private static void AppendWrapped(List<string> lines, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return;
        }

        var remaining = text.AsSpan();
        while (remaining.Length > CharsPerLine)
        {
            var split = remaining[..CharsPerLine].LastIndexOf(' ');
            if (split <= 0)
            {
                split = CharsPerLine;
            }

            lines.Add(remaining[..split].ToString());
            remaining = remaining[split..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            lines.Add(remaining.ToString());
        }
    }

    private static List<List<string>> SplitIntoPages(List<string> lines)
    {
        var maxLinesPerPage = (MarginYTop - MarginYBottom) / LineHeight;
        var pages = new List<List<string>>();
        var current = new List<string>(maxLinesPerPage);

        foreach (var line in lines)
        {
            current.Add(line);
            if (current.Count >= maxLinesPerPage)
            {
                pages.Add(current);
                current = new List<string>(maxLinesPerPage);
            }
        }

        if (current.Count > 0 || pages.Count == 0)
        {
            pages.Add(current);
        }

        return pages;
    }

    private static byte[] BuildPdf(List<List<string>> pages)
    {
        var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        var offsets = new List<long>();
        WriteHeader(writer);

        // Object numbering reserved up front so cross-references resolve cleanly.
        // 1 = Catalog, 2 = Pages, 3 = Font, then pairs of (Page, Contents) per page.
        var pageObjectIds = new List<int>(pages.Count);
        var contentObjectIds = new List<int>(pages.Count);
        var firstPageObjectId = 4;
        for (var i = 0; i < pages.Count; i++)
        {
            pageObjectIds.Add(firstPageObjectId + (i * 2));
            contentObjectIds.Add(firstPageObjectId + (i * 2) + 1);
        }

        // Object 1 — document catalog
        offsets.Add(output.Position);
        WriteObject(writer, 1, "<< /Type /Catalog /Pages 2 0 R >>");

        // Object 2 — page tree
        offsets.Add(output.Position);
        var kids = new StringBuilder();
        kids.Append('[');
        for (var i = 0; i < pageObjectIds.Count; i++)
        {
            if (i > 0)
            {
                kids.Append(' ');
            }

            kids.Append(pageObjectIds[i]).Append(" 0 R");
        }

        kids.Append(']');
        WriteObject(writer, 2,
            $"<< /Type /Pages /Count {pageObjectIds.Count} /Kids {kids} >>");

        // Object 3 — font resource (Helvetica is one of the 14 built-in PDF fonts)
        offsets.Add(output.Position);
        WriteObject(writer, 3,
            $"<< /Type /Font /Subtype /Type1 /BaseFont /{FontName} >>");

        for (var i = 0; i < pages.Count; i++)
        {
            var contents = BuildPageContents(pages[i]);
            offsets.Add(output.Position);
            WriteObject(writer, pageObjectIds[i],
                $"<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {PageWidth} {PageHeight}] " +
                $"/Resources << /Font << /F1 3 0 R >> >> " +
                $"/Contents {contentObjectIds[i]} 0 R >>");

            offsets.Add(output.Position);
            WriteStreamObject(writer, contentObjectIds[i], contents);
        }

        var xrefOffset = output.Position;
        WriteXref(writer, offsets, totalObjects: 3 + (pages.Count * 2));
        WriteTrailer(writer, totalObjects: 3 + (pages.Count * 2), xrefOffset);

        writer.Flush();
        return output.ToArray();
    }

    private static void WriteHeader(BinaryWriter writer)
    {
        // PDF 1.4 header plus a binary marker so utilities treat the file as binary-safe.
        writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n%ÿÿÿÿ\n"));
    }

    private static void WriteObject(BinaryWriter writer, int id, string body)
    {
        writer.Write(Encoding.ASCII.GetBytes($"{id} 0 obj\n{body}\nendobj\n"));
    }

    private static void WriteStreamObject(BinaryWriter writer, int id, string body)
    {
        var bytes = Encoding.ASCII.GetBytes(body);
        writer.Write(Encoding.ASCII.GetBytes($"{id} 0 obj\n<< /Length {bytes.Length} >>\nstream\n"));
        writer.Write(bytes);
        writer.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
    }

    private static string BuildPageContents(List<string> pageLines)
    {
        var sb = new StringBuilder(pageLines.Count * 64);
        sb.Append("BT\n");
        sb.Append(CultureInfo.InvariantCulture, $"/F1 {FontSize} Tf\n");
        sb.Append(CultureInfo.InvariantCulture, $"{LineHeight} TL\n");
        sb.Append(CultureInfo.InvariantCulture, $"{MarginX} {MarginYTop} Td\n");

        for (var i = 0; i < pageLines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("T*\n");
            }

            sb.Append('(').Append(EscapePdfText(pageLines[i])).Append(") Tj\n");
        }

        sb.Append("ET\n");
        return sb.ToString();
    }

    private static string EscapePdfText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                    {
                        // PDF base font (Helvetica) supports WinAnsiEncoding; for AOT
                        // simplicity we drop characters outside the printable ASCII
                        // range rather than emitting an Identity-H font dictionary.
                        sb.Append('?');
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static void WriteXref(BinaryWriter writer, List<long> offsets, int totalObjects)
    {
        writer.Write(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects + 1}\n"));
        writer.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));

        for (var i = 0; i < offsets.Count; i++)
        {
            writer.Write(Encoding.ASCII.GetBytes(
                $"{offsets[i].ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n"));
        }
    }

    private static void WriteTrailer(BinaryWriter writer, int totalObjects, long xrefOffset)
    {
        writer.Write(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n"));
    }
}
