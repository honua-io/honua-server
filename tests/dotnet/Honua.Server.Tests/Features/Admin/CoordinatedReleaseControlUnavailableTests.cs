// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class CoordinatedReleaseControlUnavailableTests
{
    [IntegrationTheory]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approval-review/{gate}")]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}")]
    [InlineData("GET", "approval-review")]
    [InlineData("POST", "approve")]
    public async Task Gate_DurableStoreUnavailable_Returns503(string method, string route)
    {
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IWorkflowOperationStore>();
            services.RemoveAll<CoordinatedReleaseControlService>();
            services.AddSingleton<CoordinatedReleaseControlService>();
        });
        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();
            using var request = new HttpRequestMessage(new HttpMethod(method),
                $"/api/v1/admin/metadata/coordinated-releases/operations/unavailable/{route}/container");
    
            using var response = await client.SendAsync(request);
    
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
