// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Measures the token cost of the MCP surface so the A1 token-efficiency work is
/// evidenced by numbers, not assertions alone. Two axes are measured with the
/// principal-engineer estimator (serialized chars / 4 ≈ tokens):
/// <list type="number">
/// <item><description><c>tools/list</c> serialized token estimate — guards against
/// response-shaping regressions bloating the catalog, without forcing the
/// teaching descriptions (#2499) to be gutted to win tokens.</description></item>
/// <item><description>A representative <c>honua_query_features</c> result — proves
/// the text-content contract: the full FeatureCollection is in
/// <c>structuredContent</c> and, per MCP 2025-06-18 (Tools, "Structured Content"),
/// the same serialized JSON is also returned as a <c>text</c> block, preceded by a
/// compact summary for large pages.</description></item>
/// </list>
/// </summary>
public sealed partial class McpTaxonomyAlignmentTests
{
    private readonly ITestOutputHelper? _output;

    /// <summary>
    /// xUnit injects <see cref="ITestOutputHelper"/> so the measurement tests can
    /// report exact token numbers. The parameterless-friendly nullable keeps the
    /// other (static-helper-driven) conformance tests in this partial class
    /// unaffected.
    /// </summary>
    public McpTaxonomyAlignmentTests(ITestOutputHelper output) => _output = output;

    /// <summary>Principal-engineer token estimate: serialized characters / 4.</summary>
    private static int EstimateTokens(string serialized) => serialized.Length / 4;

    /// <summary>Token estimate from a raw character count.</summary>
    private static int EstimateTokens(int chars) => chars / 4;

    [UnitTest]
    public void ToolsList_SerializedTokenEstimate_StaysWithinBudget()
    {
        var descriptors = BuildTools().Select(t => t.Describe()).ToArray();
        var listResult = new McpToolsListResult { Tools = descriptors };

        var serialized = JsonSerializer.Serialize(listResult, McpJsonContext.Default.McpToolsListResult);
        var tokens = EstimateTokens(serialized);

        _output?.WriteLine($"tools/list: {descriptors.Length} tools, {serialized.Length:N0} chars, ~{tokens:N0} tokens");

        // The teaching descriptions (#2499) are worth their tokens; response
        // shaping — not description trimming — is the A1 lever. Guard DENSITY
        // (average tokens per tool), not a fixed total: a legitimately growing
        // tool surface must not trip the guard, while description bloat that
        // inflates the per-tool cost must. The 23-tool surface averaged ~640
        // tokens/tool; 700 leaves headroom without hiding drift.
        var perToolTokens = tokens / descriptors.Length;
        perToolTokens.Should().BeLessThan(700,
            "tools/list token DENSITY must not balloon; the A1 win comes from response shaping, not from gutting teaching descriptions");
    }

    [UnitTest]
    public void QueryFeatures_LargePage_ReturnsSummaryThenFullJsonText()
    {
        var output = BuildRepresentativeQueryPage(featureCount: 100);
        var payloadJson = JsonSerializer.Serialize(output, MapToolJsonContext.Default.McpQueryFeaturesOutput);
        payloadJson.Length.Should().BeGreaterThanOrEqualTo(McpToolHelpers.InlineTextThresholdChars);

        var result = McpToolHelpers.SuccessResult(
            output,
            MapToolJsonContext.Default.McpQueryFeaturesOutput,
            SummarizeForMeasurement);

        _output?.WriteLine($"query_features payload (structuredContent): {payloadJson.Length:N0} chars (~{EstimateTokens(payloadJson):N0} tokens)");
        _output?.WriteLine($"summary text block: {result.Content[0].Text?.Length ?? 0:N0} chars");

        result.IsError.Should().BeFalse();
        result.Content.Should().HaveCount(2, "a large result with a summarizer carries the summary and the full JSON");
        result.Content.Should().OnlyContain(block => block.Type == "text");

        var summary = result.Content[0].Text ?? string.Empty;
        summary.Should().Contain("county_parcels").And.Contain("resultOffset=100",
            "the summary must carry the layer address and the mechanical paging next-step");

        // The data block is the serialized JSON itself and equals structuredContent.
        var dataText = result.Content[1].Text ?? string.Empty;
        dataText.Should().Be(payloadJson);
        TextShouldEqualStructuredContent(dataText, result.StructuredContent);
    }

    [UnitTest]
    public void SuccessResult_LargePayloadWithoutSummarizer_ReturnsFullJsonText()
    {
        var output = BuildRepresentativeQueryPage(featureCount: 100);

        var result = McpToolHelpers.SuccessResult(output, MapToolJsonContext.Default.McpQueryFeaturesOutput);

        result.Content.Should().ContainSingle();
        var text = result.Content[0].Text ?? string.Empty;
        text.Length.Should().BeGreaterThanOrEqualTo(McpToolHelpers.InlineTextThresholdChars);
        text.Should().NotContain("elided");
        TextShouldEqualStructuredContent(text, result.StructuredContent);
    }

    [UnitTest]
    public void SuccessResult_SmallPayload_ReturnsSingleJsonTextAndSkipsSummarizer()
    {
        var output = BuildRepresentativeQueryPage(featureCount: 1);
        var payloadJson = JsonSerializer.Serialize(output, MapToolJsonContext.Default.McpQueryFeaturesOutput);
        payloadJson.Length.Should().BeLessThan(McpToolHelpers.InlineTextThresholdChars);
        var summarizerCalled = false;

        var result = McpToolHelpers.SuccessResult(
            output,
            MapToolJsonContext.Default.McpQueryFeaturesOutput,
            o =>
            {
                summarizerCalled = true;
                return SummarizeForMeasurement(o);
            });

        summarizerCalled.Should().BeFalse("small payloads need no summary");
        result.Content.Should().ContainSingle().Which.Text.Should().Be(payloadJson);
        TextShouldEqualStructuredContent(payloadJson, result.StructuredContent);
    }

    [UnitTest]
    public void SuccessJsonElement_LargePayloadWithSummarizer_ReturnsSummaryThenFullJsonText()
    {
        var output = BuildRepresentativeQueryPage(featureCount: 100);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(output, MapToolJsonContext.Default.McpQueryFeaturesOutput));

        var result = McpToolHelpers.SuccessJsonElement(document.RootElement, static _ => "Large page summary.");

        result.Content.Should().HaveCount(2);
        result.Content[0].Text.Should().Be("Large page summary.");
        TextShouldEqualStructuredContent(result.Content[1].Text ?? string.Empty, result.StructuredContent);
    }

    [UnitTest]
    public void SuccessJsonElement_LargePayloadWithoutSummarizer_ReturnsFullJsonText()
    {
        var output = BuildRepresentativeQueryPage(featureCount: 100);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(output, MapToolJsonContext.Default.McpQueryFeaturesOutput));

        var result = McpToolHelpers.SuccessJsonElement(document.RootElement);

        result.Content.Should().ContainSingle();
        TextShouldEqualStructuredContent(result.Content[0].Text ?? string.Empty, result.StructuredContent);
    }

    [UnitTest]
    public void SuccessJsonElement_SmallPayload_ReturnsSingleJsonText()
    {
        using var document = JsonDocument.Parse("""{"status":"ok","count":3}""");

        var result = McpToolHelpers.SuccessJsonElement(document.RootElement, static _ => "unused");

        result.Content.Should().ContainSingle().Which.Text.Should().Be("""{"status":"ok","count":3}""");
        TextShouldEqualStructuredContent(result.Content[0].Text ?? string.Empty, result.StructuredContent);
    }

    /// <summary>
    /// Asserts that a text block parses as JSON and is structurally equal to
    /// the result's <c>structuredContent</c>.
    /// </summary>
    private static void TextShouldEqualStructuredContent(string text, JsonElement? structuredContent)
    {
        structuredContent.Should().NotBeNull();
        var fromText = JsonNode.Parse(text);
        var fromStructured = JsonNode.Parse(structuredContent!.Value.GetRawText());
        JsonNode.DeepEquals(fromText, fromStructured).Should().BeTrue(
            "the text block must carry the same serialized JSON as structuredContent");
    }

    /// <summary>
    /// A representative query_features page: point features with quantized
    /// geometry and a handful of attributes, the shape the tool returns.
    /// </summary>
    private static McpQueryFeaturesOutput BuildRepresentativeQueryPage(int featureCount)
    {
        var features = new List<JsonNode>(featureCount);
        for (var i = 0; i < featureCount; i++)
        {
            features.Add(new JsonObject
            {
                ["type"] = "Feature",
                ["id"] = i,
                ["geometry"] = new JsonObject
                {
                    ["type"] = "Point",
                    ["coordinates"] = new JsonArray(-97.743061 + (i * 0.0001), 30.267153 + (i * 0.0001)),
                },
                ["properties"] = new JsonObject
                {
                    ["parcel_id"] = $"R{100000 + i}",
                    ["zoning"] = "R1",
                    ["acreage"] = 0.25 + (i * 0.01),
                },
            });
        }

        return new McpQueryFeaturesOutput
        {
            ServiceId = "county_parcels",
            ServiceLayerIndex = 0,
            ReturnedCount = featureCount,
            Limit = 100,
            ResultOffset = 0,
            ExceededTransferLimit = featureCount >= 100,
            NextOffset = featureCount >= 100 ? 100 : null,
            GeoJson = new McpGeoJsonFeatureCollection { Features = features },
        };
    }

    [UnitTest]
    public void QueryFeatures_GeometryQuantization_ShrinksCoordinatePayload()
    {
        // Full-precision (double round-trip) coordinates vs the default 6-dp
        // quantization the tool now applies, over a representative 100-point page.
        const int featureCount = 100;
        static string SerializePage(Func<int, JsonArray> coordinates)
        {
            var features = new List<JsonNode>(featureCount);
            for (var i = 0; i < featureCount; i++)
            {
                features.Add(new JsonObject
                {
                    ["type"] = "Feature",
                    ["id"] = i,
                    ["geometry"] = new JsonObject { ["type"] = "Point", ["coordinates"] = coordinates(i) },
                    ["properties"] = new JsonObject { ["parcel_id"] = $"R{100000 + i}" },
                });
            }

            var output = new McpQueryFeaturesOutput
            {
                ServiceId = "county_parcels",
                ServiceLayerIndex = 0,
                ReturnedCount = featureCount,
                Limit = 100,
                GeoJson = new McpGeoJsonFeatureCollection { Features = features },
            };
            return JsonSerializer.Serialize(output, MapToolJsonContext.Default.McpQueryFeaturesOutput);
        }

        var fullPrecision = SerializePage(i => new JsonArray(
            -97.743061928374651 + (i * 0.000012345678),
            30.267153827364512 + (i * 0.000012345678)));
        var quantized = SerializePage(i => new JsonArray(
            Math.Round(-97.743061928374651 + (i * 0.000012345678), 6, MidpointRounding.AwayFromZero),
            Math.Round(30.267153827364512 + (i * 0.000012345678), 6, MidpointRounding.AwayFromZero)));

        _output?.WriteLine($"geometry full precision: {fullPrecision.Length:N0} chars (~{EstimateTokens(fullPrecision):N0} tokens)");
        _output?.WriteLine($"geometry 6-dp quantized: {quantized.Length:N0} chars (~{EstimateTokens(quantized):N0} tokens)");
        _output?.WriteLine($"quantization reduction: {fullPrecision.Length - quantized.Length:N0} chars (~{EstimateTokens(fullPrecision.Length - quantized.Length):N0} tokens, {(fullPrecision.Length - quantized.Length) * 100.0 / fullPrecision.Length:F0}%)");

        quantized.Length.Should().BeLessThan(fullPrecision.Length,
            "6-dp quantization must trim full-precision coordinate noise from the payload");
    }

    /// <summary>
    /// Mirrors the live <c>QueryFeaturesTool</c> summary shape closely enough to
    /// measure the token delta without reaching into the tool's private helper.
    /// </summary>
    private static string SummarizeForMeasurement(McpQueryFeaturesOutput output)
    {
        var pagingNote = output.ExceededTransferLimit && output.NextOffset is { } next
            ? $"more available: re-query with resultOffset={next}"
            : "last page";
        return $"Returned {output.ReturnedCount} feature(s) from {output.ServiceId}/{output.ServiceLayerIndex} at offset {output.ResultOffset} ({pagingNote}, geometry included). GeoJSON FeatureCollection in structuredContent.geojson.";
    }
}
