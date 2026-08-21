// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin.OperationCatalog;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class AdminOperationRoundTripTests
{
    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task ServerStatus_ExecutesThroughCatalogAndReturnsAdminEndpointResponse()
    {
        var tenantCatalog = Substitute.For<ITenantCatalog>();
        tenantCatalog.GetAsync("public", Arg.Any<CancellationToken>())
            .Returns(new TenantRecord
            {
                TenantId = "public",
                DisplayName = "Public",
                Status = TenantStatus.Active,
            });
        var fixture = new WebAppFixture().ReplaceService<ITenantCatalog>(tenantCatalog);
        await fixture.InitializeAsync();
        try
        {
            var client = fixture.CreateAdminClient();
            var directResponse = await client.GetAsync("/api/v1/admin/version");
            directResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var directDocument = JsonDocument.Parse(await directResponse.Content.ReadAsStringAsync());

            var operationResponse = await client.PostAsJsonAsync(
                "/api/v1/operations/admin.server.status/submit",
                new { parameters = new Dictionary<string, string?>() });

            operationResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var operationDocument = JsonDocument.Parse(await operationResponse.Content.ReadAsStringAsync());
            var handle = operationDocument.RootElement.GetProperty("data");
            handle.GetProperty("operationId").GetString().Should().Be("admin.server.status");
            handle.GetProperty("status").GetString().Should().Be(
                "Completed",
                "the in-process endpoint returned: {0}",
                handle.GetRawText());

            var responseText = handle.GetProperty("result").GetProperty("details")
                .GetProperty("response").GetString();
            responseText.Should().NotBeNullOrWhiteSpace();
            using var invokedDocument = JsonDocument.Parse(responseText!);
            var directData = directDocument.RootElement.GetProperty("data");
            var invokedData = invokedDocument.RootElement.GetProperty("data");
            invokedData.GetProperty("version").GetString().Should().Be(directData.GetProperty("version").GetString());
            invokedData.GetProperty("metadataApiVersion").GetString()
                .Should().Be(directData.GetProperty("metadataApiVersion").GetString());
            invokedData.GetProperty("metadataSchemaVersion").GetString()
                .Should().Be(directData.GetProperty("metadataSchemaVersion").GetString());
            invokedData.GetProperty("serverTime").GetDateTimeOffset().Should().BeCloseTo(
                directData.GetProperty("serverTime").GetDateTimeOffset(),
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
