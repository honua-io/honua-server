// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Reporting.Domain;

public enum AnalysisReportFormat
{
    Markdown = 0,

    Html = 1
}

[JsonConverter(typeof(JsonStringEnumConverter<NarrativeMode>))]
public enum NarrativeMode
{
    [JsonStringEnumMemberName(ReportingConstants.NarrativeModeDeterministicTag)]
    Deterministic = 0,

    [JsonStringEnumMemberName(ReportingConstants.NarrativeModeLlmAssistedTag)]
    LlmAssisted = 1,

    [JsonStringEnumMemberName(ReportingConstants.NarrativeModeFallbackFromLlmErrorTag)]
    FallbackFromLlmError = 2
}

// The section discriminator is a string so a round-trip does not depend on .NET type identity.
public static class AnalysisReportSectionKinds
{
    public const string Heading = "heading";

    public const string Paragraph = "paragraph";

    public const string KeyMetric = "key-metric";

    public const string Table = "table";

    public const string Chart = "chart";

    public const string MapEmbed = "map-embed";

    public const string Narrative = "narrative";

    public const string ProvenanceFooter = "provenance-footer";
}
