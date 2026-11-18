// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Exports;

namespace Honua.Server.Features.Admin.Exports;

// The layer's public metadata (with the service's governance fallbacks applied), who hosts it,
// and its stored MapLibre style, read once at publish time.
internal sealed record ExportLayerFacts(MetadataV2ObjectMetadata Metadata, string HostName, string? StoredMapLibreStyle);

// What the companion files are generated from: the validated descriptor and the layer's facts.
internal sealed record ExportCompanionInput(ExportVersion Descriptor, ExportLayerFacts Layer)
{
    public MetadataV2ObjectMetadata Metadata => Layer.Metadata;

    public string HostName => Layer.HostName;

    public string? StoredMapLibreStyle => Layer.StoredMapLibreStyle;

    // A rendered PNG of the captured features, when the publish produced one.
    public byte[]? Thumbnail { get; init; }

    // Only public fields reach the artifacts, so only they are described.
    public IReadOnlyList<ExportField> PublicFields => [.. Descriptor.Fields.Where(candidate => candidate.Public)];

    public string DisplayTitle => Text(Descriptor.Title) ?? Text(Metadata.Title) ?? Descriptor.PublicationId;

    public string? Description => Text(Metadata.Description);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record ExportCompanionFile(string FileName, string MediaType, byte[] Content)
{
    public static ExportCompanionFile Text(string fileName, string mediaType, string content)
        => new(fileName, mediaType, Encoding.UTF8.GetBytes(content));
}

// The files that travel with every export version so a person or an agent can use the data
// without Honua: a README, an AGENTS.md with ready-to-run queries, a MapLibre style and a thumbnail.
internal static class ExportCompanionFiles
{
    internal const string ReadmeFileName = "README.md";
    internal const string AgentsFileName = "AGENTS.md";
    internal const string StyleFileName = "style.json";
    internal const string MarkdownMediaType = "text/markdown; charset=utf-8";
    internal const string StyleMediaType = "application/vnd.mapbox.style+json";

    public static IReadOnlyList<string> FileNames { get; } = [ReadmeFileName, AgentsFileName, StyleFileName, ExportThumbnail.FileName];

    public static IReadOnlyList<ExportCompanionFile> Render(ExportCompanionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        List<ExportCompanionFile> files =
        [
            ExportCompanionFile.Text(ReadmeFileName, MarkdownMediaType, ExportReadme.Render(input)),
            ExportCompanionFile.Text(AgentsFileName, MarkdownMediaType, ExportAgentsGuide.Render(input)),
            ExportCompanionFile.Text(StyleFileName, StyleMediaType, ExportStyleAsset.Render(input))
        ];
        if (input.Thumbnail is { } thumbnail)
        {
            files.Add(new ExportCompanionFile(ExportThumbnail.FileName, ExportThumbnail.MediaType, thumbnail));
        }

        return files;
    }
}

// Markdown helpers shared by the README and AGENTS.md generators. Metadata text is
// author-supplied, so it is escaped rather than trusted as markup.
internal static class ExportMarkdown
{
    public static string Inline(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ReplaceLineEndings(" "))
        {
            builder.Append(character switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                '\\' or '`' or '*' or '_' or '[' or ']' or '|' or '#' => "\\" + character,
                _ => character.ToString()
            });
        }

        return builder.ToString().Trim();
    }

    // Paragraph breaks survive; everything else is escaped as inline text.
    public static string Block(string value)
        => string.Join("\n\n", value.ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Inline));

    public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Bounds(IReadOnlyList<double> bounds)
        => string.Join(", ", bounds.Select(Number));
}
