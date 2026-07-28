// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Geocoding.Features.Geocoding.LocatorImport;

namespace Honua.Core.Tests.Features.Geocoding;

/// <summary>
/// Unit coverage for the classic Esri <c>.loc</c> locator definition parser (#2152): supported
/// match settings are recorded, unsupported constructs are reported explicitly (never silently
/// dropped), and binary (ArcGIS Pro) payloads are rejected with an operator-safe error.
/// </summary>
public sealed class EsriLocFileParserTests
{
    private const string ClassicLoc = """
        ; US Streets style address locator
        Version = 8.1
        CLSID = {AE5A3A0E-F756-11D2-9F4F-00C04F8ED1C4}
        UICLSID = {3D486637-6BB7-11D2-9F27-00C04F8ED1C4}
        Category = Address
        Fields = SingleLine
        MinimumMatchScore = 60
        MinimumCandidateScore = 10
        SpellingSensitivity = 80
        SideOffset = 20
        SideOffsetUnits = Feet
        EndOffset = 3
        MatchIfScoresTie = TRUE
        Interpolate = TRUE
        """;

    [Fact]
    public void Parse_ClassicLocator_RecordsMatchSettings()
    {
        var report = new List<LocatorTranslationEntry>();

        var definition = EsriLocFileParser.Parse(Encoding.UTF8.GetBytes(ClassicLoc), "USStreets", report);

        Assert.Equal("USStreets", definition.Name);
        Assert.Equal("8.1", definition.Version);
        Assert.Equal("{AE5A3A0E-F756-11D2-9F4F-00C04F8ED1C4}", definition.StyleId);
        Assert.Equal("Address", definition.Category);
        Assert.Equal(60, definition.MatchSettings.MinimumMatchScore);
        Assert.Equal(10, definition.MatchSettings.MinimumCandidateScore);
        Assert.Equal(80, definition.MatchSettings.SpellingSensitivity);
        Assert.Equal(20, definition.MatchSettings.SideOffset);
        Assert.Equal("Feet", definition.MatchSettings.SideOffsetUnits);
        Assert.Equal(3, definition.MatchSettings.EndOffset);
        Assert.True(definition.MatchSettings.MatchIfScoresTie);
        Assert.True(definition.MatchSettings.Interpolate);
        Assert.All(report, entry => Assert.Equal(LocatorTranslationStatus.Supported, entry.Status));
    }

    [Fact]
    public void Parse_UnknownKey_ReportedUnsupported()
    {
        var report = new List<LocatorTranslationEntry>();

        _ = EsriLocFileParser.Parse(
            Encoding.UTF8.GetBytes("Version = 8.1\nBatchPresenceThreshold = 0.8\n"), "Test", report);

        var unsupported = Assert.Single(report, e => e.Status == LocatorTranslationStatus.Unsupported);
        Assert.Equal("BatchPresenceThreshold", unsupported.Item);
    }

    [Fact]
    public void Parse_EveryKey_AppearsInReport()
    {
        var report = new List<LocatorTranslationEntry>();

        _ = EsriLocFileParser.Parse(Encoding.UTF8.GetBytes(ClassicLoc), "Test", report);

        // Nothing is silently dropped: every non-comment key lands in the report.
        Assert.Equal(13, report.Count);
    }

    [Fact]
    public void Parse_ReferenceDataKeys_ReportedIgnored()
    {
        var report = new List<LocatorTranslationEntry>();

        _ = EsriLocFileParser.Parse(
            Encoding.UTF8.GetBytes("Version = 8.1\nReferenceData.Table = streets.dbf\nData.Path = C:\\data\n"),
            "Test",
            report);

        Assert.Equal(2, report.Count(e => e.Status == LocatorTranslationStatus.Ignored));
    }

    [Fact]
    public void Parse_CompositeAndAltNameKeys_ReportedUnsupported()
    {
        var report = new List<LocatorTranslationEntry>();

        _ = EsriLocFileParser.Parse(
            Encoding.UTF8.GetBytes("Version = 8.1\nCompositeLocator.Participants = A;B\nAltName.Table = alt\n"),
            "Test",
            report);

        Assert.Equal(2, report.Count(e => e.Status == LocatorTranslationStatus.Unsupported));
    }

    [Fact]
    public void Parse_NonWgs84CoordinateSystem_ReportedUnsupported()
    {
        var report = new List<LocatorTranslationEntry>();

        _ = EsriLocFileParser.Parse(
            Encoding.UTF8.GetBytes("Version = 8.1\nCoordinateSystem = PROJCS[\"NAD_1983_StatePlane\"]\n"),
            "Test",
            report);

        var entry = Assert.Single(report, e => e.Item == "CoordinateSystem");
        Assert.Equal(LocatorTranslationStatus.Unsupported, entry.Status);
    }

    [Fact]
    public void Parse_Wgs84CoordinateSystem_Supported()
    {
        var report = new List<LocatorTranslationEntry>();

        _ = EsriLocFileParser.Parse(
            Encoding.UTF8.GetBytes("Version = 8.1\nCoordinateSystem = GEOGCS[\"GCS_WGS_1984\"]\n"),
            "Test",
            report);

        var entry = Assert.Single(report, e => e.Item == "CoordinateSystem");
        Assert.Equal(LocatorTranslationStatus.Supported, entry.Status);
    }

    [Fact]
    public void Parse_InvalidNumericValue_ReportedUnsupported()
    {
        var report = new List<LocatorTranslationEntry>();

        var definition = EsriLocFileParser.Parse(
            Encoding.UTF8.GetBytes("Version = 8.1\nMinimumMatchScore = high\n"), "Test", report);

        Assert.Null(definition.MatchSettings.MinimumMatchScore);
        var entry = Assert.Single(report, e => e.Item == "MinimumMatchScore");
        Assert.Equal(LocatorTranslationStatus.Unsupported, entry.Status);
    }

    [Fact]
    public void Parse_BinaryPayload_ThrowsOperatorSafeError()
    {
        var report = new List<LocatorTranslationEntry>();
        byte[] binary = [0x50, 0x4B, 0x00, 0x01, 0x02, 0x03];

        var ex = Assert.Throws<EsriLocatorImportException>(
            () => EsriLocFileParser.Parse(binary, "Test", report));

        Assert.Contains("binary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NoKeyValuePairs_Throws()
    {
        var report = new List<LocatorTranslationEntry>();

        Assert.Throws<EsriLocatorImportException>(
            () => EsriLocFileParser.Parse(Encoding.UTF8.GetBytes("; only a comment\n"), "Test", report));
    }

    [Fact]
    public void Parse_EmptyContent_Throws()
    {
        var report = new List<LocatorTranslationEntry>();

        Assert.Throws<EsriLocatorImportException>(
            () => EsriLocFileParser.Parse([], "Test", report));
    }
}
