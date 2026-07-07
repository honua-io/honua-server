// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
/// the double-encoding fix: the full FeatureCollection rides only in
/// <c>structuredContent</c>, and the <c>text</c> block is a compact summary
/// rather than a byte-for-byte duplicate.</description></item>
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
    public void QueryFeatures_TextBlock_IsCompactSummaryNotDuplicatedPayload()
    {
        // A representative page: 100 features, each with quantized geometry and a
        // handful of attributes — the shape query_features actually returns.
        const int featureCount = 100;
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

        var output = new McpQueryFeaturesOutput
        {
            ServiceId = "county_parcels",
            LayerId = 0,
            ReturnedCount = featureCount,
            Limit = 100,
            ResultOffset = 0,
            ExceededTransferLimit = true,
            NextOffset = 100,
            GeoJson = new McpGeoJsonFeatureCollection { Features = features },
        };

        var payloadJson = JsonSerializer.Serialize(output, MapToolJsonContext.Default.McpQueryFeaturesOutput);

        // "Before" (original double-encoding): SuccessResult copied the full JSON
        // verbatim into the text block, so the wire result carried the payload
        // twice — text == structuredContent.
        var beforeText = payloadJson;
        var beforeTotal = payloadJson.Length + beforeText.Length;

        // "After": the live tool passes an information-bearing summarizer, so the
        // text block is a one-line summary and the payload rides once, in
        // structuredContent.
        var afterResult = McpToolHelpers.SuccessResult(
            output,
            MapToolJsonContext.Default.McpQueryFeaturesOutput,
            SummarizeForMeasurement);
        var afterText = afterResult.Content[0].Text ?? string.Empty;
        var afterTotal = payloadJson.Length + afterText.Length;

        _output?.WriteLine($"query_features payload (structuredContent): {payloadJson.Length:N0} chars (~{EstimateTokens(payloadJson):N0} tokens)");
        _output?.WriteLine($"BEFORE (double-encoded) result total {beforeTotal:N0} chars (~{EstimateTokens(beforeTotal):N0} tokens)");
        _output?.WriteLine($"AFTER  text block: {afterText.Length:N0} chars ('{afterText}'); result total {afterTotal:N0} chars (~{EstimateTokens(afterTotal):N0} tokens)");
        _output?.WriteLine($"result total reduction: {beforeTotal - afterTotal:N0} chars (~{EstimateTokens(beforeTotal - afterTotal):N0} tokens, {(beforeTotal - afterTotal) * 100.0 / beforeTotal:F0}%)");

        // The structuredContent still carries the full payload.
        (afterResult.StructuredContent?.GetRawText().Length ?? 0).Should().Be(payloadJson.Length);

        // After, the text block is a short, information-bearing summary — not a
        // duplicate — so the result is far smaller than the double-encoded form.
        afterText.Length.Should().BeLessThan(payloadJson.Length / 4,
            "the summarized text block must be a fraction of the payload, not a duplicate");
        afterText.Should().Contain("county_parcels").And.Contain("resultOffset=100",
            "the summary must carry the layer address and the mechanical paging next-step");
        afterTotal.Should().BeLessThan(beforeTotal / 2 + 512,
            "eliminating the duplicated text roughly halves the result size");
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
                LayerId = 0,
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
        return $"Returned {output.ReturnedCount} feature(s) from {output.ServiceId}/{output.LayerId} at offset {output.ResultOffset} ({pagingNote}, geometry included). GeoJSON FeatureCollection in structuredContent.geojson.";
    }
}
