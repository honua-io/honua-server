// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.OData;

[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataCrudExceptionMappingTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WhenWriterThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = CreateFixtureWithCreateException(() => new InvalidOperationException("unexpected writer failure"));
        await fixture.InitializeAsync();

        try
        {
            var request = new ODataFeatureRequest
            {
                Attributes = new Dictionary<string, object?>
                {
                    ["name"] = "Create Fail"
                }
            };

            var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
            var response = await fixture.Client.PostAsync(
                "/odata/Layers(0)/Features",
                new StringContent(json, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("unexpected writer failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId})")]
    public async Task UpdateFeature_WhenWriterThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = CreateFixtureWithUpdateException(() => new InvalidOperationException("unexpected writer failure"));
        await fixture.InitializeAsync();

        try
        {
            var existingId = await fixture.InsertFeatureAsync(0, "Update Fail");
            var request = new ODataFeatureRequest
            {
                Attributes = new Dictionary<string, object?>
                {
                    ["name"] = "Update Fail"
                }
            };

            var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
            using var message = new HttpRequestMessage(HttpMethod.Patch, $"/odata/Features(0,{existingId})")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var response = await fixture.Client.SendAsync(message);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("unexpected writer failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixtureWithCreateException(Func<Exception> exceptionFactory)
    {
        var writer = Substitute.For<IFeatureWriter>();
        writer.CreateAsync(Arg.Any<int>(), Arg.Any<Feature>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Feature>(exceptionFactory()));

        return new WebAppFixture()
            .ReplaceService<IFeatureWriter>(writer);
    }

    private static WebAppFixture CreateFixtureWithUpdateException(Func<Exception> exceptionFactory)
    {
        var writer = Substitute.For<IFeatureWriter>();
        writer.UpdateAsync(Arg.Any<int>(), Arg.Any<Feature>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Feature>(exceptionFactory()));

        return new WebAppFixture()
            .ReplaceService<IFeatureWriter>(writer);
    }
}
