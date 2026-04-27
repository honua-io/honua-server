// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Reporting.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Reporting;

/// <summary>
/// Pins the polymorphic JSON shape of <see cref="AnalysisReportSection"/> so
/// AOT serializers can round-trip the discriminated union without reflection.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class AnalysisReportSectionJsonTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void HeadingSection_Serializes_WithKindDiscriminator()
    {
        var section = (AnalysisReportSection)new HeadingSection { Text = "Title", Level = 2 };

        var json = JsonSerializer.Serialize(section, _options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("kind").GetString().Should().Be(AnalysisReportSectionKinds.Heading);
        doc.RootElement.GetProperty("text").GetString().Should().Be("Title");
        doc.RootElement.GetProperty("level").GetInt32().Should().Be(2);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void NarrativeSection_Serializes_WithLlmAndDeterministicText()
    {
        var section = (AnalysisReportSection)new NarrativeSection
        {
            SlotId = "summary",
            DeterministicText = "Deterministic",
            LlmText = "LLM",
            Mode = NarrativeMode.LlmAssisted
        };

        var json = JsonSerializer.Serialize(section, _options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("kind").GetString().Should().Be(AnalysisReportSectionKinds.Narrative);
        doc.RootElement.GetProperty("slotId").GetString().Should().Be("summary");
        doc.RootElement.GetProperty("deterministicText").GetString().Should().Be("Deterministic");
        doc.RootElement.GetProperty("llmText").GetString().Should().Be("LLM");
        // Enum-on-wire contract: NarrativeMode must serialize as the documented
        // kebab-case-lower tag (not the numeric enum value).
        doc.RootElement.GetProperty("mode").GetString()
            .Should().Be(ReportingConstants.NarrativeModeLlmAssistedTag);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void NarrativeSection_RoundTrips_NarrativeModeStringTag()
    {
        var original = (AnalysisReportSection)new NarrativeSection
        {
            SlotId = "summary",
            DeterministicText = "Deterministic",
            Mode = NarrativeMode.FallbackFromLlmError
        };

        var json = JsonSerializer.Serialize(original, _options);
        var restored = JsonSerializer.Deserialize<AnalysisReportSection>(json, _options);

        var restoredNarrative = restored.Should().BeOfType<NarrativeSection>().Subject;
        restoredNarrative.Mode.Should().Be(NarrativeMode.FallbackFromLlmError);
        json.Should().Contain($"\"mode\":\"{ReportingConstants.NarrativeModeFallbackFromLlmErrorTag}\"");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ChartSection_SerializesChartKind_AsLowercaseStringTag()
    {
        var section = (AnalysisReportSection)new ChartSection
        {
            ChartKind = ReportChartKind.Bar,
            Categories = new[] { "A" },
            Series = new[] { new ChartSeries { Name = "S", Values = new[] { 1.0 } } }
        };

        var json = JsonSerializer.Serialize(section, _options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("kind").GetString().Should().Be(AnalysisReportSectionKinds.Chart);
        doc.RootElement.GetProperty("chartKind").GetString().Should().Be("bar");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void TableSection_RoundTripsRowsAndTruncation()
    {
        var original = (AnalysisReportSection)new TableSection
        {
            Caption = "Top rows",
            Columns = new[] { "A", "B" },
            Rows = new List<IReadOnlyList<string>>
            {
                new[] { "1", "2" },
                new[] { "3", "4" }
            },
            TruncatedRowCount = 5
        };

        var json = JsonSerializer.Serialize(original, _options);
        var restored = JsonSerializer.Deserialize<AnalysisReportSection>(json, _options);

        var restoredTable = restored.Should().BeOfType<TableSection>().Subject;
        restoredTable.Columns.Should().Equal("A", "B");
        restoredTable.Rows.Should().HaveCount(2);
        restoredTable.Rows[0].Should().Equal("1", "2");
        restoredTable.TruncatedRowCount.Should().Be(5);
    }
}
