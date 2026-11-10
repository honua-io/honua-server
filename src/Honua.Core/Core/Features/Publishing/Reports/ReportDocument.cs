// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Reports;

public sealed class ReportDocument
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = ReportDocumentFormats.V1;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("narrative")]
    public string? Narrative { get; init; }

    [JsonPropertyName("routeSlug")]
    public string? RouteSlug { get; init; }

    [JsonPropertyName("bindings")]
    public ReportBinding[] Bindings { get; init => field = value ?? []; } = [];

    [JsonPropertyName("panels")]
    public ReportPanel[] Panels { get; init => field = value ?? []; } = [];
}

public sealed class ReportBinding
{
    [JsonPropertyName("alias")]
    public string Alias { get; init; } = string.Empty;

    [JsonPropertyName("contentRef")]
    public string ContentRef { get; init; } = string.Empty;

    [JsonPropertyName("versionPin")]
    public string? VersionPin { get; init; }
}

public sealed class ReportPanel
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ReportPanelKinds.Chart;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("bindingAlias")]
    public string? BindingAlias { get; init; }

    [JsonPropertyName("chartSpec")]
    public JsonElement? ChartSpec { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }
}

public static class ReportDocumentFormats
{
    public const string V1 = "honua.report-document.v1";
}

public static class ReportPanelKinds
{
    public const string Chart = "chart";

    public const string Map = "map";

    public const string Table = "table";

    public const string Text = "text";

    public static readonly string[] All = [Chart, Map, Table, Text];
}
