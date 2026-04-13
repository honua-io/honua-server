// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for GPServer parameter translation (GP types ↔ canonical opaque inputs).
/// </summary>
[Protocol(Protocols.GPServer)]
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
    public void ResolveOutputParameterName_FallsBackToLabel()
    {
        var artifact = new ArtifactRef
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "Output Features",
            Metadata = new Dictionary<string, string>()
        };

        GPServerParameterTranslation.ResolveOutputParameterName(artifact)
            .Should().Be("Output Features");
    }
}
