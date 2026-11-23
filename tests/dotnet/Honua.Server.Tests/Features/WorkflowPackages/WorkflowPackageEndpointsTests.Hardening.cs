// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed partial class WorkflowPackageEndpointsTests
{
    public static TheoryData<string> UnsafeIdentifiers => new()
    {
        "a/b",
        "a:v1:b",
        "a\r\nb",
        "-leading-dash",
        "a b",
        new string('p', 129),
    };

    [IntegrationTheory]
    [MemberData(nameof(UnsafeIdentifiers))]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    public async Task SavePackage_WithUnsafePackageId_IsRefusedAndNotStored(string packageId)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            CreatePackageRequest("unsafe-id", CreateAreaGraph(), packageId),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("packageId");
        (await GetStoredPackageAsync(packageId.Trim())).Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    public async Task SavePackage_WithDottedPackageId_IsCreatedAtItsOwnRoute()
    {
        var packageId = $"team.flood_{Guid.NewGuid():N}-v2";
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            CreatePackageRequest("dotted-id", CreateAreaGraph(), packageId),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.Should().Be($"/api/v1/console/workflow-packages/{packageId}");
        (await _client.GetAsync(response.Headers.Location)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task PublishVersion_WithColonPublicationId_DoesNotWriteAPublicationOrSchedule()
    {
        var packageId = await CreatePackageAsync("colon-publication", CreateAreaGraph());
        var version = await CreateVersionAsync(packageId);
        var publicationId = $"p{Guid.NewGuid():N}:v1:q";
        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/publish",
            new
            {
                publicationId,
                target = "Schedule",
                schedule = new { cronExpression = "0 0 * * *", timeZone = "UTC", enabled = true }
            },
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetStoredPublicationAsync(publicationId)).Should().BeNull();
        (await _definitionStore.GetAsync($"workflow-package:{packageId}:v{version.Version}:{publicationId}"))
            .Should().BeNull();
    }

    private async Task<WorkflowPackage?> GetStoredPackageAsync(string packageId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>().GetPackageAsync(packageId);
    }

    private async Task<WorkflowPublication?> GetStoredPublicationAsync(string publicationId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>().GetPublicationAsync(publicationId);
    }
}
