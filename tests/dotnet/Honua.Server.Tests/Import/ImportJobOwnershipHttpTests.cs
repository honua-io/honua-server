// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>Real authentication and routes over a recording job service; no worker execution claim.</summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Admin)]
public sealed class ImportJobOwnershipHttpTests
{
    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("GET /api/v1/admin/import/jobs")]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    [Endpoint("POST /api/v1/admin/import/jobs/{jobId}/cancel")]
    public async Task JobRoutes_FilterForeignAndLegacyRecordsAndRefuseCancellationBeforeEffects()
    {
        var jobs = Substitute.For<IImportJobService>();
        var fixture = new WebAppFixture().ReplaceService(jobs).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "import-ownership-tests-only-signing-key-at-least-32-bytes");
            builder.UseSetting("OperatorApproval:PublishRequiresApproval", "false");
            builder.UseSetting("OperatorApproval:AdminExemptFromApproval", "true");
        });
        await fixture.InitializeAsync();
        try
        {
            var keys = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
            var owner = await keys.CreateAsync("import-owner", ["admin:write", "agent:workspace"],
                DateTimeOffset.UtcNow.AddMinutes(15), "ownership-test", default);
            var reader = await keys.CreateAsync("import-reader", ["admin:read", "agent:workspace"],
                DateTimeOffset.UtcNow.AddMinutes(15), "ownership-test", default);
            var actor = $"apikey:api-key:{owner.Record.Id:D}";
            var records = new[]
            {
                Progress("owned", actor, "public"),
                Progress("foreign-owner", "foreign-actor", "public"),
                Progress("foreign-tenant", actor, "other-tenant"),
                ImportProgress.CreateInitial("legacy", "private-legacy-table", SupportedFileFormat.GeoJson),
            };
            jobs.GetActiveJobsAsync(Arg.Any<CancellationToken>()).Returns(records);
            jobs.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => records.SingleOrDefault(job => job.JobId == call.ArgAt<string>(0)));
            jobs.CancelJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", owner.Key));

            using (var list = await client.GetAsync("/api/v1/admin/import/jobs"))
            {
                list.StatusCode.Should().Be(HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                body.RootElement.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("jobId").GetString())
                    .Should().Equal("owned");
            }

            foreach (var id in new[] { "foreign-owner", "foreign-tenant", "legacy", "missing" })
            {
                using var read = await client.GetAsync($"/api/v1/admin/import/jobs/{id}");
                read.StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await read.Content.ReadAsStringAsync()).Should().NotContain("private-");
                using var cancel = await client.PostAsync($"/api/v1/admin/import/jobs/{id}/cancel", null);
                cancel.StatusCode.Should().Be(HttpStatusCode.NotFound);
                await jobs.DidNotReceive().CancelJobAsync(id, Arg.Any<CancellationToken>());
            }

            using (var read = await client.GetAsync("/api/v1/admin/import/jobs/owned"))
                read.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var cancel = await client.PostAsync("/api/v1/admin/import/jobs/owned/cancel", null))
                cancel.StatusCode.Should().Be(HttpStatusCode.OK);
            await jobs.Received(1).CancelJobAsync("owned", Arg.Any<CancellationToken>());

            using var readOnly = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", reader.Key));
            using (var denied = await readOnly.PostAsync("/api/v1/admin/import/jobs/owned/cancel", null))
                denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            await jobs.Received(1).CancelJobAsync("owned", Arg.Any<CancellationToken>());

            var tokens = fixture.Services.GetRequiredService<OperatorBearerTokenService>();
            foreach (var (tenant, visibleIds) in new[]
            {
                ("public", new[] { "owned", "foreign-owner" }),
                ("other-tenant", new[] { "foreign-tenant" }),
            })
            {
                var token = tokens.Issue([
                    new() { Type = ClaimTypes.NameIdentifier, Value = "human-operator" },
                    new() { Type = ClaimTypes.Role, Value = "admin" },
                    new() { Type = "tenant_id", Value = tenant },
                ], DateTimeOffset.UtcNow.AddMinutes(5));
                token.Should().NotBeNull();
                using var human = fixture.CreateClient(c => c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token));
                using var list = await human.GetAsync("/api/v1/admin/import/jobs");
                list.StatusCode.Should().Be(HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                body.RootElement.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("jobId").GetString())
                    .Should().Equal(visibleIds);
            }

            await keys.RevokeAsync(owner.Record.Id, default);
            using var revoked = await client.GetAsync("/api/v1/admin/import/jobs/owned");
            revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static ImportProgress Progress(string id, string actor, string tenant) =>
        ImportProgress.CreateInitial(id, $"private-{id}-table", SupportedFileFormat.GeoJson,
            ownership: new ImportOperationOwnership(actor, tenant));
}
