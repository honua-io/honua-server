// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using SkiaSharp;

namespace Honua.Server.Features.Studio.Export;

/// <summary>
/// Composes a Studio content version (map, dashboard, or report) into a shareable raster (PNG)
/// or document (PDF) deliverable.
/// </summary>
/// <remarks>
/// <para>
/// Rendering reuses SkiaSharp — the same raster surface, font, and encoding primitives that back the
/// raster map render pipeline (the Lambda fontconfig/freetype surface fixed in #1728) — so the
/// composer works headless inside the AOT Lambda image without an additional render engine.
/// </para>
/// <para>
/// PDF composition uses <see cref="SKDocument.CreatePdf(System.IO.Stream)"/>, drawing the same
/// page layout onto a PDF canvas. This keeps the dependency surface to the already-referenced
/// SkiaSharp package (no new PDF library) and produces a vector-text PDF rather than a raster wrapper.
/// </para>
/// </remarks>
public static class StudioDeliverableComposer
{
    private const float PageWidth = 1240f; // A4 @ ~150 DPI portrait
    private const float PageHeight = 1754f;
    private const float Margin = 72f;
    private static readonly SKColor HeaderColor = new(0x0F, 0x62, 0x57);
    private static readonly SKColor BodyColor = new(0x20, 0x24, 0x28);
    private static readonly SKColor MutedColor = new(0x5C, 0x66, 0x70);
    private static readonly SKColor RuleColor = new(0xD5, 0xDC, 0xE1);

    /// <summary>
    /// Composes the supplied content version into a deliverable artifact in the requested format.
    /// </summary>
    /// <param name="version">Immutable Studio content version to render.</param>
    /// <param name="format">Output format.</param>
    /// <returns>The encoded deliverable artifact.</returns>
    public static StudioDeliverableArtifact Compose(StudioContentVersion version, StudioDeliverableFormat format)
    {
        ArgumentNullException.ThrowIfNull(version);

        var layout = BuildLayout(version);
        var bytes = format switch
        {
            StudioDeliverableFormat.Pdf => RenderPdf(layout),
            _ => RenderPng(layout),
        };

        var family = version.Envelope.Family;
        var extension = format == StudioDeliverableFormat.Pdf ? "pdf" : "png";
        var contentType = format == StudioDeliverableFormat.Pdf ? "application/pdf" : "image/png";
        var slug = Slugify(layout.Title);

        return new StudioDeliverableArtifact
        {
            Content = bytes,
            ContentType = contentType,
            FileName = $"{slug}.{extension}",
            Family = family,
            Format = format,
        };
    }

    private static byte[] RenderPng(DeliverableLayout layout)
    {
        var info = new SKImageInfo((int)PageWidth, (int)PageHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Skia failed to allocate a deliverable render surface.");
        DrawPage(surface.Canvas, layout);
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray()
            ?? throw new InvalidOperationException("Skia failed to encode the deliverable PNG.");
    }

    private static byte[] RenderPdf(DeliverableLayout layout)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream)
            ?? throw new InvalidOperationException("Skia failed to create a PDF document."))
        {
            var canvas = document.BeginPage(PageWidth, PageHeight);
            DrawPage(canvas, layout);
            document.EndPage();
            document.Close();
        }

        return stream.ToArray();
    }

    private static void DrawPage(SKCanvas canvas, DeliverableLayout layout)
    {
        canvas.Clear(SKColors.White);

        using var titleFont = new SKFont(SKTypeface.Default, 34f);
        using var badgeFont = new SKFont(SKTypeface.Default, 16f);
        using var labelFont = new SKFont(SKTypeface.Default, 14f);
        using var bodyFont = new SKFont(SKTypeface.Default, 18f);
        using var footerFont = new SKFont(SKTypeface.Default, 12f);

        using var headerPaint = new SKPaint { Color = HeaderColor, IsAntialias = true };
        using var bodyPaint = new SKPaint { Color = BodyColor, IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = MutedColor, IsAntialias = true };
        using var rulePaint = new SKPaint { Color = RuleColor, IsAntialias = true, StrokeWidth = 1.5f };

        var y = Margin + 24f;

        // Family badge.
        canvas.DrawText(layout.FamilyLabel.ToUpperInvariant(), Margin, y, badgeFont, mutedPaint);
        y += 44f;

        // Title (wrapped).
        foreach (var line in WrapText(layout.Title, titleFont, PageWidth - (2 * Margin)))
        {
            canvas.DrawText(line, Margin, y, titleFont, headerPaint);
            y += 42f;
        }

        y += 8f;
        canvas.DrawLine(Margin, y, PageWidth - Margin, y, rulePaint);
        y += 36f;

        // Metadata rows.
        foreach (var (label, value) in layout.Metadata)
        {
            canvas.DrawText(label, Margin, y, labelFont, mutedPaint);
            canvas.DrawText(value, Margin + 220f, y, labelFont, bodyPaint);
            y += 28f;
        }

        y += 24f;

        // Body summary lines.
        foreach (var paragraph in layout.BodyLines)
        {
            if (paragraph.Length == 0)
            {
                y += 14f;
                continue;
            }

            foreach (var line in WrapText(paragraph, bodyFont, PageWidth - (2 * Margin)))
            {
                if (y > PageHeight - Margin - 40f)
                {
                    return;
                }

                canvas.DrawText(line, Margin, y, bodyFont, bodyPaint);
                y += 28f;
            }
        }

        // Footer.
        canvas.DrawLine(Margin, PageHeight - Margin, PageWidth - Margin, PageHeight - Margin, rulePaint);
        canvas.DrawText(layout.Footer, Margin, PageHeight - Margin + 22f, footerFont, mutedPaint);
    }

    private static DeliverableLayout BuildLayout(StudioContentVersion version)
    {
        var envelope = version.Envelope;
        var family = envelope.Family;
        var title = ResolveTitle(version);

        var metadata = new List<(string Label, string Value)>
        {
            ("Type", FamilyLabel(family)),
            ("Version", version.VersionNumber.ToString(CultureInfo.InvariantCulture)),
            ("Created", version.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)),
        };

        if (!string.IsNullOrWhiteSpace(envelope.Format))
        {
            metadata.Add(("Format", envelope.Format!));
        }

        if (!string.IsNullOrWhiteSpace(version.WorkspaceId))
        {
            metadata.Add(("Workspace", version.WorkspaceId!));
        }

        if (envelope.Bindings.Count > 0)
        {
            metadata.Add(("Data sources", envelope.Bindings.Count.ToString(CultureInfo.InvariantCulture)));
        }

        var bodyLines = BuildBodySummary(family, envelope.Body);

        return new DeliverableLayout(
            title,
            FamilyLabel(family),
            metadata,
            bodyLines,
            $"Honua Studio deliverable · {FamilyLabel(family)} · item {version.ItemId}");
    }

    private static List<string> BuildBodySummary(StudioPackageFamily family, JsonElement? body)
    {
        var lines = new List<string>();
        if (body is not { ValueKind: JsonValueKind.Object } element)
        {
            lines.Add("No structured content was captured for this deliverable.");
            return lines;
        }

        switch (family)
        {
            case StudioPackageFamily.Map:
                AppendMapSummary(element, lines);
                break;
            case StudioPackageFamily.Dashboard:
                AppendDashboardSummary(element, lines);
                break;
            case StudioPackageFamily.Report:
                AppendReportSummary(element, lines);
                break;
            default:
                lines.Add($"Contents: {DescribeObject(element)}");
                break;
        }

        if (lines.Count == 0)
        {
            lines.Add("No renderable content was found in the package body.");
        }

        return lines;
    }

    private static void AppendMapSummary(JsonElement body, List<string> lines)
    {
        if (TryGetString(body, "description", out var description))
        {
            lines.Add(description);
            lines.Add(string.Empty);
        }

        if (body.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
        {
            lines.Add($"Layers ({layers.GetArrayLength()}):");
            foreach (var layer in layers.EnumerateArray())
            {
                var name = TryGetString(layer, "title", out var t) ? t
                    : TryGetString(layer, "name", out var n) ? n
                    : TryGetString(layer, "id", out var id) ? id
                    : "Untitled layer";
                lines.Add($"  • {name}");
            }
        }

        if (body.TryGetProperty("basemap", out var basemap) && basemap.ValueKind == JsonValueKind.String)
        {
            lines.Add(string.Empty);
            lines.Add($"Basemap: {basemap.GetString()}");
        }
    }

    private static void AppendDashboardSummary(JsonElement body, List<string> lines)
    {
        if (TryGetString(body, "description", out var description))
        {
            lines.Add(description);
            lines.Add(string.Empty);
        }

        if (body.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
        {
            lines.Add($"Widgets ({widgets.GetArrayLength()}):");
            foreach (var widget in widgets.EnumerateArray())
            {
                var title = TryGetString(widget, "title", out var t) ? t : "Untitled widget";
                var type = TryGetString(widget, "type", out var ty) ? $" [{ty}]" : string.Empty;
                lines.Add($"  • {title}{type}");
            }
        }
        else if (body.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Array)
        {
            lines.Add($"Panels ({panels.GetArrayLength()}):");
            foreach (var panel in panels.EnumerateArray())
            {
                var title = TryGetString(panel, "title", out var t) ? t : "Untitled panel";
                lines.Add($"  • {title}");
            }
        }
    }

    private static void AppendReportSummary(JsonElement body, List<string> lines)
    {
        if (TryGetString(body, "summary", out var summary))
        {
            lines.Add(summary);
            lines.Add(string.Empty);
        }

        if (body.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
        {
            lines.Add($"Sections ({sections.GetArrayLength()}):");
            foreach (var section in sections.EnumerateArray())
            {
                var heading = TryGetString(section, "heading", out var h) ? h
                    : TryGetString(section, "title", out var t) ? t
                    : "Untitled section";
                lines.Add($"  • {heading}");
            }
        }
        else if (body.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Array)
        {
            lines.Add($"Panels ({panels.GetArrayLength()}):");
            foreach (var panel in panels.EnumerateArray())
            {
                var title = TryGetString(panel, "title", out var t) ? t : "Untitled panel";
                lines.Add($"  • {title}");
            }
        }
    }

    private static string ResolveTitle(StudioContentVersion version)
    {
        if (version.Envelope.Body is { ValueKind: JsonValueKind.Object } body)
        {
            if (TryGetString(body, "title", out var title))
            {
                return title;
            }

            if (TryGetString(body, "name", out var name))
            {
                return name;
            }
        }

        return string.IsNullOrWhiteSpace(version.PackageKey)
            ? FamilyLabel(version.Envelope.Family)
            : version.PackageKey;
    }

    private static string DescribeObject(JsonElement element)
    {
        var names = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            names.Add(property.Name);
            if (names.Count >= 12)
            {
                break;
            }
        }

        return names.Count == 0 ? "(empty)" : string.Join(", ", names);
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var node)
            && node.ValueKind == JsonValueKind.String)
        {
            var s = node.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                value = s;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string FamilyLabel(StudioPackageFamily family) => family switch
    {
        StudioPackageFamily.Map => "Map",
        StudioPackageFamily.Dashboard => "Dashboard",
        StudioPackageFamily.Report => "Report",
        _ => family.ToString(),
    };

    private static IEnumerable<string> WrapText(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            yield return text;
            yield break;
        }

        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= maxWidth || current.Length == 0)
            {
                current = candidate;
            }
            else
            {
                yield return current;
                current = word;
            }
        }

        if (current.Length > 0)
        {
            yield return current;
        }
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "studio-deliverable";
        }

        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        if (slug.Length > 64)
        {
            slug = slug[..64].Trim('-');
        }

        return slug.Length == 0 ? "studio-deliverable" : slug;
    }

    private sealed record DeliverableLayout(
        string Title,
        string FamilyLabel,
        IReadOnlyList<(string Label, string Value)> Metadata,
        IReadOnlyList<string> BodyLines,
        string Footer);
}
