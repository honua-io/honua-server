// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Unit tests for GPServer parameter translation (GP types ↔ canonical opaque inputs).
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerParameterTranslationTests
{
    // -----------------------------------------------------------------------
    // Inbound translation
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_PreservesSimpleTypes()
    {
        var input = new Dictionary<string, string>
        {
            ["Distance"] = "100",
            ["Units"] = "Meters",
            ["Dissolve"] = "true"
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result.Should().HaveCount(3);
        result["Distance"].Should().Be("100");
        result["Units"].Should().Be("Meters");
        result["Dissolve"].Should().Be("true");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_UsesCaseInsensitiveKeys()
    {
        var input = new Dictionary<string, string>
        {
            ["INPUT_FEATURES"] = "test-layer"
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["input_features"].Should().Be("test-layer");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_ExtractsUrlFromGPDataFile()
    {
        var input = new Dictionary<string, string>
        {
            ["Input_File"] = """{"url":"https://example.com/data.zip"}"""
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["Input_File"].Should().Be("https://example.com/data.zip");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_ExtractsUrlFromGPRasterDataLayer()
    {
        var input = new Dictionary<string, string>
        {
            ["Raster"] = """{"url":"https://example.com/raster.tif","format":"tif"}"""
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["Raster"].Should().Be("https://example.com/raster.tif");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_NormalizesGPLinearUnit()
    {
        var input = new Dictionary<string, string>
        {
            ["Buffer_Distance"] = """{"distance":100,"units":"esriMeters"}"""
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["Buffer_Distance"].Should().Be("100 esriMeters");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_NormalizesGPArealUnit()
    {
        var input = new Dictionary<string, string>
        {
            ["Area_Threshold"] = """{"distance":500.5,"units":"esriSquareKilometers"}"""
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["Area_Threshold"].Should().Be("500.5 esriSquareKilometers");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_PreservesGPFeatureRecordSetLayerAsJson()
    {
        var json = """{"features":[{"attributes":{"FID":1}}],"fields":[{"name":"FID"}]}""";
        var input = new Dictionary<string, string>
        {
            ["Input_Features"] = json
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["Input_Features"].Should().Be(json);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_PreservesFeatureRecordSetWithUrl()
    {
        // Feature record sets with both "url" and "features"/"fields" should NOT
        // have the URL extracted — they pass through as full JSON.
        var json = """{"url":"https://example.com/fs","features":[]}""";
        var input = new Dictionary<string, string>
        {
            ["Input_Features"] = json
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["Input_Features"].Should().Be(json);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void TranslateInbound_PreservesInvalidJsonAsString()
    {
        var input = new Dictionary<string, string>
        {
            ["Notes"] = "{not valid json"
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["Notes"].Should().Be("{not valid json");
    }

    // -----------------------------------------------------------------------
    // GPMultiValue inbound unpacking (spec-aware)
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public void TranslateInbound_GPMultiValue_NormalisesJsonArrayInput()
    {
        var definition = MultiValueDefinition();
        var input = new Dictionary<string, string>
        {
            ["wkbs"] = """["AAAA","BBBB"]"""
        };

        var result = GPServerParameterTranslation.TranslateInbound(input, definition);

        using var doc = JsonDocument.Parse(result["wkbs"]);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["AAAA", "BBBB"]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public void TranslateInbound_GPMultiValue_UnpacksCommaDelimitedInput()
    {
        var definition = MultiValueDefinition();
        var input = new Dictionary<string, string>
        {
            ["wkbs"] = "AAAA,BBBB,CCCC"
        };

        var result = GPServerParameterTranslation.TranslateInbound(input, definition);

        using var doc = JsonDocument.Parse(result["wkbs"]);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["AAAA", "BBBB", "CCCC"]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public void TranslateInbound_GPMultiValue_DoesNotSplitScalarParameters()
    {
        // Without a definition the comma-delimited form must pass through —
        // otherwise scalar inputs like coordinate pairs would be corrupted.
        var input = new Dictionary<string, string>
        {
            ["coords"] = "1.5,2.5"
        };

        var result = GPServerParameterTranslation.TranslateInbound(input);

        result["coords"].Should().Be("1.5,2.5");
    }

    // -----------------------------------------------------------------------
    // GPChoice / AllowedValues validation
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public void TranslateInbound_GPChoice_AcceptsAllowedValueCaseInsensitively()
    {
        var definition = ChoiceDefinition();
        var input = new Dictionary<string, string>
        {
            ["target"] = "GeoJSON"
        };

        var result = GPServerParameterTranslation.TranslateInbound(input, definition);

        result["target"].Should().Be("GeoJSON");
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public void TranslateInbound_GPChoice_RejectsValueNotInAllowedList()
    {
        var definition = ChoiceDefinition();
        var input = new Dictionary<string, string>
        {
            ["target"] = "not-a-format"
        };

        var act = () => GPServerParameterTranslation.TranslateInbound(input, definition);

        act.Should().Throw<GeoprocessingValidationException>()
            .Where(ex => ex.Message.Contains("target", StringComparison.Ordinal)
                && ex.Message.Contains("not-a-format", StringComparison.Ordinal));
    }

    private static ProcessDefinition MultiValueDefinition() => new()
    {
        ProcessId = "test.multi",
        Title = "Test Multi",
        Description = "Test",
        Category = "test",
        Parameters =
        [
            new ProcessParameterSpec
            {
                Name = "wkbs",
                DisplayName = "WKBs",
                Description = "Multi-value WKB inputs",
                ValueType = ProcessParameterValueType.WkbArray,
                Required = true
            }
        ],
        OutputArtifactKinds = [ArtifactKind.FeatureLayer]
    };

    private static ProcessDefinition ChoiceDefinition() => new()
    {
        ProcessId = "test.choice",
        Title = "Test Choice",
        Description = "Test",
        Category = "test",
        Parameters =
        [
            new ProcessParameterSpec
            {
                Name = "target",
                DisplayName = "Target",
                Description = "Enumerated target",
                ValueType = ProcessParameterValueType.Text,
                Required = true,
                AllowedValues = ["wkt", "geojson", "wkb", "ewkt"]
            }
        ],
        OutputArtifactKinds = [ArtifactKind.Scalar]
    };

    // -----------------------------------------------------------------------
    // Outbound type mapping
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public void ToEsriDataType_MapsArtifactKindsCorrectly()
    {
        GPServerParameterTranslation.ToEsriDataType(ArtifactKind.FeatureLayer)
            .Should().Be("GPFeatureRecordSetLayer");

        GPServerParameterTranslation.ToEsriDataType(ArtifactKind.Table)
            .Should().Be("GPRecordSet");

        GPServerParameterTranslation.ToEsriDataType(ArtifactKind.Raster)
            .Should().Be("GPRasterDataLayer");

        GPServerParameterTranslation.ToEsriDataType(ArtifactKind.File)
            .Should().Be("GPDataFile");

        GPServerParameterTranslation.ToEsriDataType(ArtifactKind.Report)
            .Should().Be("GPDataFile");

        GPServerParameterTranslation.ToEsriDataType(ArtifactKind.Map)
            .Should().Be("GPDataFile");

        GPServerParameterTranslation.ToEsriDataType(ArtifactKind.Scalar)
            .Should().Be("GPString");
    }

    // -----------------------------------------------------------------------
    // Output parameter resolution
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public void ResolveOutputParameterName_UsesMetadataKey()
    {
        var artifact = new ArtifactRef
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "Output Features",
            Metadata = new Dictionary<string, string>
            {
                [GPServerParameterTranslation.OutputParameterMetadataKey] = "Output_FeatureSet"
            }
        };

        GPServerParameterTranslation.ResolveOutputParameterName(artifact)
            .Should().Be("Output_FeatureSet");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public void ResolveOutputParameterName_ThrowsWhenMetadataKeyMissing()
    {
        var artifact = new ArtifactRef
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "Output Features",
            Metadata = new Dictionary<string, string>()
        };

        var act = () => GPServerParameterTranslation.ResolveOutputParameterName(artifact);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*geoservices.output_parameter*");
    }
}
