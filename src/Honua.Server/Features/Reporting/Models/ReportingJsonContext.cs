// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Reporting.Domain;
using Honua.Server.Features.Reporting.Mcp;

namespace Honua.Server.Features.Reporting.Models;

/// <summary>
/// AOT-friendly JSON context for the reporting feature. Covers the OpenAI
/// narrative wire models, the on-the-wire AnalysisReport envelope (used for
/// HTTP/MCP responses), and the polymorphic section subtypes.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReportingOpenAiChatRequest))]
[JsonSerializable(typeof(ReportingOpenAiChatResponse))]
[JsonSerializable(typeof(ReportingOpenAiMessage))]
[JsonSerializable(typeof(ReportingOpenAiResponseFormat))]
[JsonSerializable(typeof(ReportingOpenAiChoice))]
[JsonSerializable(typeof(NarrativePromptPayload))]
[JsonSerializable(typeof(NarrativePromptSlot))]
[JsonSerializable(typeof(NarrativeFillResponse))]
[JsonSerializable(typeof(AnalysisReport))]
[JsonSerializable(typeof(AnalysisReportSection))]
[JsonSerializable(typeof(HeadingSection))]
[JsonSerializable(typeof(ParagraphSection))]
[JsonSerializable(typeof(KeyMetricSection))]
[JsonSerializable(typeof(TableSection))]
[JsonSerializable(typeof(ChartSection))]
[JsonSerializable(typeof(ChartSeries))]
[JsonSerializable(typeof(MapEmbedSection))]
[JsonSerializable(typeof(NarrativeSection))]
[JsonSerializable(typeof(ProvenanceFooterSection))]
[JsonSerializable(typeof(McpAnalysisReport))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ReportingJsonContext : JsonSerializerContext
{
}
