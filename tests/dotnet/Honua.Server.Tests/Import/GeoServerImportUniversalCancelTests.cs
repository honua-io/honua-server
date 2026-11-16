// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Import;

// A queued import cancelled through the operation-level route, before any worker claims it, must read
// as cancelled through that route too: no worker remains to end its progress snapshot.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class GeoServerImportUniversalCancelTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task QueuedImport_CancelledBeforeClaim_ReadsCancelled()
    {
        await using var fixture = new WebAppFixture().WithProductionJobSubstrate()
            .WithTestLicense(HonuaEdition.Enterprise)
            .ReplaceService(Substitute.For<IGeoServerImportService>())
            .ConfigureServices(services =>
            {
                var worker = services.Single(d => d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(JobExecutionService));
                services.Remove(worker);
            });
        await fixture.InitializeAsync();

        var start = await fixture.Client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = true
        });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted, await start.Content.ReadAsStringAsync());
        using var started = await start.Content.ReadFromJsonAsync<JsonDocument>();
        var operationId = started!.RootElement.GetProperty("jobId").GetString();

        var queued = await fixture.Client.GetAsync($"/api/v1/admin/operations/{operationId}");
        queued.StatusCode.Should().Be(HttpStatusCode.OK, "a queued import is visible before any worker reads it");

        var cancel = await fixture.Client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());

        var read = await fixture.Client.GetAsync($"/api/v1/admin/operations/{operationId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await read.Content.ReadAsStringAsync();
        using var status = JsonDocument.Parse(body);
        StatusOf(status.RootElement).GetInt32().Should().Be((int)OperationStatus.Cancelled, body);
    }

    private static JsonElement StatusOf(JsonElement root)
        => root.EnumerateObject()
            .First(property => string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase))
            .Value;
}
