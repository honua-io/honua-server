// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Contract;

namespace Honua.Server.Tests.Contract;

[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public sealed class FeatureServerContractTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoJsonFormat_CompliesWithGeoJsonContract()
    {
        var result = await ContractTestScenarios.ValidateGeoJsonSchema(
            _fixture.Client,
            "/rest/services/test/FeatureServer/0/query?f=geojson&resultRecordCount=1");

        result.AllValid.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task MetadataAndQueryEndpoints_ReturnExpectedHttpSemantics()
    {
        var expectations = new Dictionary<string, HttpSemanticsExpectation>
        {
            ["/rest/services/test/FeatureServer?f=json"] = new()
            {
                ExpectedStatusCodes = [HttpStatusCode.OK],
                ExpectedContentType = "application/json",
                RequiredHeaders = ["Content-Type"]
            },
            ["/rest/services/test/FeatureServer/0?f=json"] = new()
            {
                ExpectedStatusCodes = [HttpStatusCode.OK],
                ExpectedContentType = "application/json",
                RequiredHeaders = ["Content-Type"]
            },
            ["/rest/services/test/FeatureServer/0/query?f=geojson&resultRecordCount=1"] = new()
            {
                ExpectedStatusCodes = [HttpStatusCode.OK],
                ExpectedContentType = "application/geo+json",
                RequiredHeaders = ["Content-Type"]
            }
        };

        var result = await ContractTestScenarios.ValidateHttpSemantics(_fixture.Client, expectations);
        result.AllValid.Should().BeTrue();
    }
}
