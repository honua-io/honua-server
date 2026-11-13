// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.DependencyInjection;
using Honua.Server.Startup;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;

namespace Honua.Server.Tests.Admin;

internal static class McpPublicationFixtureSetup
{
    internal static async Task UseManagedPublicationKeyAsync(IServiceProvider services, HttpClient client)
    {
        // Approval revalidates the proposer against the managed credential store.
        // Bootstrap credentials cannot supply that durable authority.
        var issued = await services.GetRequiredService<IAdminApiKeyStore>().CreateAsync(
            "publication-fixture", ["admin:read", "admin:write", "agent"],
            DateTimeOffset.UtcNow.AddMinutes(10), "publication-fixture", CancellationToken.None);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", issued.Key);
    }

    internal static async Task<OperationStatus> ApprovePublicationAsync(
        HumanApprovalHttpClient human, HttpClient admin, JsonElement pending)
    {
        pending.GetProperty("status").GetString().Should().Be("RequiresApproval", pending.GetRawText());
        var proposalId = pending.GetProperty("proposalId").GetString();
        proposalId.Should().NotBeNullOrWhiteSpace();
        using var approval = await human.ApproveAsync(proposalId!);
        approval.StatusCode.Should().Be(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync());
        using var response = await admin.GetAsync($"/api/v1/operations/handles/{pending.GetProperty("handleId").GetString()}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync(OperationsJsonContext.Default.ApiResponseOperationStatus);
        var completed = envelope!.Data!;
        completed.OperationInstanceId.Should().Be(pending.GetProperty("operationInstanceId").GetString());
        completed.Status.Should().Be(OperationHandleStatus.Completed);
        return completed;
    }

    internal static async Task InstallPromotionStoresAsync(WebAppFixture fixture)
    {
        // Test hosts skip DbUp. Readback uses the production promotion stores
        // before falling back to ordinary catalog publications, so install the
        // canonical schema without inserting synthetic promotion records.
        const string migrationName = "Honua.Server.Migrations.082_CreatePromotionStores.sql";
        await using var stream = typeof(ServerCoreSchemaMigrations).Assembly.GetManifestResourceStream(migrationName)
            ?? throw new InvalidOperationException($"Embedded migration '{migrationName}' was not found.");
        using var reader = new StreamReader(stream);
        await fixture.Postgres.ExecuteDdlUnderLockAsync(await reader.ReadToEndAsync());
    }
}
