// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Authentication;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed partial class FolderOperationIntegrationTests(FolderOperationIntegrationTests.FolderHost host)
    : IClassFixture<FolderOperationIntegrationTests.FolderHost>, IAsyncLifetime
{
    private readonly WebAppFixture _fixture = host.Fixture;
    private readonly ReplayServiceAuthorization _authorization = host.Authorization;
    private readonly FolderRequesterMembership _memberships = host.Memberships;
    private readonly FolderRequesterLicense _licensing = host.Licensing;
    private HttpClient _proposer = null!;
    private readonly HumanApprovalHttpClient _approver = host.Approver;
    private readonly IOperationProposalStore _proposals = host.Proposals;

    public async Task InitializeAsync()
    {
        await host.ResetAsync();
        _proposer = await CreateClientAsync("proposer", ["admin:*"]);
    }

    public async Task DisposeAsync()
    {
        await DeletePublishConnectionAsync();
        _proposer.Dispose();
    }

    // One host per class. Each case starts from the metadata graph the host had after its
    // schema install, with the requester membership, license and service denial restored, and
    // issues its own proposer key because cases revoke or restrict it.
    public sealed class FolderHost : IAsyncLifetime
    {
        private readonly AdminAuthEndpointsTests.StubHttpClientFactory _identity = AdminAuthEndpointsTests.CreateStubFactory();
        private ReplayServiceAuthorization? _authorization;
        private string _baselineSchema = null!;

        internal WebAppFixture Fixture { get; } = new WebAppFixture().WithProductionJobSubstrate();
        internal FolderRequesterMembership Memberships { get; } = new();
        internal FolderRequesterLicense Licensing { get; } = new();
        internal ReplayServiceAuthorization Authorization =>
            _authorization ?? throw new InvalidOperationException("The operator authorization evaluator was not resolved.");
        internal HumanApprovalHttpClient Approver { get; private set; } = null!;
        internal IOperationProposalStore Proposals { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            HumanApprovalHttpClient.ConfigureKestrel(Fixture, _identity).ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(ApprovalPolicy()));
            }).ConfigureServices(services =>
            {
                FolderPostgresTestHost.Configure(services, () => Fixture.CurrentSchema);
                services.AddSingleton<IPrincipalMembershipSource>(Memberships);
                services.AddSingleton<ILicenseEntitlementService>(Licensing);
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                    RlsClaimsTestAuthHandler.SchemeName, _ => { });
                services.AddSingleton<IOperatorAuthorizationEvaluator>(provider =>
                    _authorization = new ReplayServiceAuthorization(ActivatorUtilities.CreateInstance<OperatorAuthorizationEvaluator>(provider)));
            });
            await Fixture.InitializeAsync();
            await Fixture.Postgres.DataSource.ClearJobSubstrateAsync();
            await FolderPostgresTestHost.InstallSchemaAsync(Fixture);
            _baselineSchema = $"graph_baseline_{Guid.NewGuid():N}";
            await Fixture.Postgres.ExecuteDdlUnderLockAsync(SaveGraphTables(Fixture.CurrentSchema!, _baselineSchema));
            Approver = await HumanApprovalHttpClient.AuthenticateAsync(Fixture, _identity, tenantId: "public");
            Proposals = Fixture.Services.GetRequiredService<IOperationProposalStore>();
            _ = Fixture.Services.GetRequiredService<IOperatorAuthorizationEvaluator>();
        }

        internal async Task ResetAsync()
        {
            Memberships.Reset();
            Licensing.Reset();
            Authorization.DeniedService = null;
            await Fixture.Postgres.ExecuteDdlUnderLockAsync(RestoreGraphTables(_baselineSchema, Fixture.CurrentSchema!));
            Fixture.Services.GetService<IMetadataV2GraphCacheInvalidator>()?.InvalidateAll();
        }

        public async Task DisposeAsync()
        {
            Approver?.Dispose();
            using var identity = _identity;
            if (_baselineSchema is not null)
            {
                await Fixture.Postgres.DropSchemaAsync(_baselineSchema);
            }

            await Fixture.DisposeAsync();
        }

        // Keeps a copy of every metadata_v2 table of the host schema.
        private static string SaveGraphTables(string source, string baseline) => $"""
            DO $save$
            DECLARE
                graph_table text;
            BEGIN
                EXECUTE format('CREATE SCHEMA %I', '{baseline}');
                FOR graph_table IN SELECT tablename FROM pg_tables
                    WHERE schemaname = '{source}' AND tablename LIKE 'metadata\_v2\_%'
                LOOP
                    EXECUTE format('CREATE TABLE %I.%I AS TABLE %I.%I', '{baseline}', graph_table, '{source}', graph_table);
                END LOOP;
            END
            $save$;
            """;

        // Truncates the graph tables in one statement, then copies the saved rows back as a
        // replication session so neither foreign keys nor triggers depend on the copy order.
        // Generated columns are left to the database.
        private static string RestoreGraphTables(string baseline, string target) => $"""
            DO $restore$
            DECLARE
                graph_table text;
                graph_tables text;
                graph_columns text;
            BEGIN
                SELECT string_agg(format('%I.%I', '{target}', tablename), ', ') INTO graph_tables
                    FROM pg_tables WHERE schemaname = '{baseline}';
                IF graph_tables IS NULL THEN
                    RETURN;
                END IF;
                PERFORM set_config('session_replication_role', 'replica', true);
                EXECUTE 'TRUNCATE ' || graph_tables;
                FOR graph_table IN SELECT tablename FROM pg_tables WHERE schemaname = '{baseline}' LOOP
                    SELECT string_agg(format('%I', column_name), ', ' ORDER BY ordinal_position) INTO graph_columns
                        FROM information_schema.columns
                        WHERE table_schema = '{target}' AND table_name = graph_table AND is_generated = 'NEVER';
                    EXECUTE format('INSERT INTO %I.%I (%s) OVERRIDING SYSTEM VALUE SELECT %s FROM %I.%I',
                        '{target}', graph_table, graph_columns, graph_columns, '{baseline}', graph_table);
                END LOOP;
            END
            $restore$;
            """;
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task FolderOperations_ApprovedLifecycle_PersistsMembershipAliasesAndDeletion()
    {
        var initial = await SnapshotAsync();
        var listed = await SubmitAsync("folder.list", []);
        listed.GetProperty("result").GetProperty("details").GetProperty("etag").GetString().Should().Be(initial.Etag);
        await ApplyAsync("folder.create", new() { ["name"] = "Survey", ["title"] = "Field survey", ["If-Match"] = initial.Etag });
        var created = await SnapshotAsync();
        var folder = created.Graph.Folders.Should().ContainSingle().Subject;
        await ApplyAsync("service.move", new() { ["folderId"] = folder.Metadata.Id, ["If-Match"] = created.Etag });
        var moved = await SnapshotAsync();
        moved.Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().HaveCountGreaterThan(1).And.OnlyContain(service => service.FolderId == folder.Metadata.Id);
        await ApplyAsync("folder.update", new() { ["id"] = folder.Metadata.Id, ["name"] = "Survey2", ["If-Match"] = moved.Etag });
        var renamed = await SnapshotAsync();
        renamed.Graph.Folders.Single().PreviousNames.Should().Contain("Survey");
        renamed.Graph.Folders.Single().Metadata.Title.Should().Be("Field survey");
        await ApplyAsync("service.move", new() { ["folderId"] = null, ["If-Match"] = renamed.Etag });
        var returned = await SnapshotAsync();
        returned.Graph.Services.Where(service => service.Metadata.Name == "test").Should().OnlyContain(service => service.FolderId == null);
        await ApplyAsync("folder.delete", new() { ["id"] = folder.Metadata.Id, ["If-Match"] = returned.Etag });
        var deleted = await SnapshotAsync();
        deleted.Graph.Folders.Single().Deleted.Should().BeTrue();
        deleted.Etag.Should().NotBe(initial.Etag);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData(null, null)]
    [InlineData("workspace", "public")]
    public async Task FolderCreate_ApprovalReplay_PreservesResolvedOwnership(string? requestedNamespace, string? tenant)
    {
        var initial = await SnapshotAsync();
        var parameters = new Dictionary<string, string?>
        {
            ["name"] = "Owned",
            ["If-Match"] = initial.Etag,
            ["tenantId"] = "caller-supplied-tenant"
        };
        if (requestedNamespace is not null) parameters["namespace"] = requestedNamespace;
        await ApplyAsync("folder.create", parameters);
        (await SnapshotAsync()).Graph.Folders.Single().Metadata.Tenant.Should().Be(tenant);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task FolderCreate_ProposalBecomesStale_ApprovalCannotOverwriteNewRevision()
    {
        var initial = await SnapshotAsync();
        var pending = await ProposeAsync("folder.create", new() { ["name"] = "Stale", ["If-Match"] = initial.Etag });
        await ApplyAsync("folder.create", new() { ["name"] = "Winner", ["If-Match"] = initial.Etag });
        var winner = await SnapshotAsync();
        await ApproveAsync(pending);
        (await _proposals.GetAsync(pending, CancellationToken.None))!.Status.Should().Be(OperationProposalStatus.Failed);
        var current = await SnapshotAsync();
        current.Etag.Should().Be(winner.Etag);
        current.Graph.Folders.Should().ContainSingle(folder => folder.Metadata.Name == "Winner");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData(null)]
    [InlineData("\"stale\"")]
    public async Task FolderCreate_InvalidPrecondition_DoesNotMutateSnapshot(string? etag)
    {
        var initial = await SnapshotAsync();
        using var response = await _proposer.PostAsJsonAsync("/api/v1/operations/folder.create/submit",
            new { parameters = new Dictionary<string, string?> { ["name"] = "Rejected", ["If-Match"] = etag } });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (response.IsSuccessStatusCode && body.RootElement.GetProperty("data").TryGetProperty("proposalId", out var id)
            && id.ValueKind == JsonValueKind.String)
        {
            await ApproveAsync(id.GetString()!);
            (await _proposals.GetAsync(id.GetString()!, CancellationToken.None))!.Status.Should().Be(OperationProposalStatus.Failed);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            body.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Failed");
        }
        (await SnapshotAsync()).Etag.Should().Be(initial.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task FolderCreate_ReadOnlyKey_CannotSubmitMutation()
    {
        var initial = await SnapshotAsync();
        using var reader = await CreateClientAsync("reader", ["admin:read"]);
        using var response = await reader.PostAsJsonAsync("/api/v1/operations/folder.create/submit",
            new { parameters = new Dictionary<string, string?> { ["name"] = "Denied", ["If-Match"] = initial.Etag } });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await SnapshotAsync()).Etag.Should().Be(initial.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ServiceMove_AccessDeniedAfterSubmission_ApprovalCannotMutateMembership()
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Destination", ["If-Match"] = initial.Etag });
        var before = await SnapshotAsync();
        var proposal = await ProposeAsync("service.move", new()
        {
            ["folderId"] = before.Graph.Folders.Single().Metadata.Id,
            ["If-Match"] = before.Etag
        });
        _authorization.DeniedService = "test";
        await ApproveAsync(proposal);
        (await _proposals.GetAsync(proposal, CancellationToken.None))!.Status.Should().Be(OperationProposalStatus.Failed);
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
        (await SnapshotAsync()).Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.FolderId == null);
    }

    internal sealed class ReplayServiceAuthorization(IOperatorAuthorizationEvaluator evaluator) : IOperatorAuthorizationEvaluator
    {
        public string? DeniedService { get; set; }

        public Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => request.ResourceType == OperatorResourceType.PublishedService && request.ResourceId == DeniedService
                ? Task.FromResult(AccessDecision.Forbidden("Service access denied."))
                : evaluator.EvaluateAsync(principal, request, cancellationToken);
    }

    private async Task ApplyAsync(string operationId, Dictionary<string, string?> parameters)
    {
        var before = await SnapshotAsync();
        var proposal = await ProposeAsync(operationId, parameters);
        (await SnapshotAsync()).Etag.Should().Be(before.Etag, "submission must wait for separate approval");
        await ApproveAsync(proposal);
        var applied = (await _proposals.GetAsync(proposal, CancellationToken.None))!;
        applied.Status.Should().Be(OperationProposalStatus.Succeeded, string.Join("; ", applied.Plan.BlockingReasons));
        var after = await SnapshotAsync();
        after.Etag.Should().NotBe(before.Etag);
        using var replay = await _approver.Client.PostAsync($"/api/v1/admin/proposals/{proposal}/approve", null);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SnapshotAsync()).Etag.Should().Be(after.Etag, "duplicate approval cannot repeat a mutation");
    }

    private async Task<string> ProposeAsync(string operationId, Dictionary<string, string?> parameters)
    {
        var result = await SubmitAsync(operationId, parameters);
        var id = result.GetProperty("proposalId").GetString()!;
        var proposal = await _proposals.GetAsync(id, CancellationToken.None);
        proposal!.OperationId.Should().Be(operationId);
        proposal.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        return id;
    }

    private async Task<JsonElement> SubmitAsync(string operationId, Dictionary<string, string?> parameters)
    {
        using var response = await _proposer.PostAsJsonAsync($"/api/v1/operations/{operationId}/submit",
            new { serviceName = "test", parameters });
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var body = JsonDocument.Parse(payload);
        return body.RootElement.GetProperty("data").Clone();
    }

    private async Task ApproveAsync(string proposal)
    {
        using var response = await _approver.ApproveAsync(proposal);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> CreateClientAsync(string name, string[] permissions)
    {
        var keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var issued = await keys.CreateAsync($"folder-{name}-{Guid.NewGuid():N}", permissions,
            DateTimeOffset.UtcNow.AddHours(1), "test", CancellationToken.None);
        return _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", issued.Key));
    }

    private async Task<MetadataV2GraphSnapshot> SnapshotAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().GetCurrentAsync();
    }

    private static Dictionary<string, string?> ApprovalPolicy()
    {
        var settings = new Dictionary<string, string?> { ["Operations:Policy:Enabled"] = "true" };
        var operations = new[] { "folder.create", "folder.update", "folder.delete", "service.move", "service.publish" };
        for (var index = 0; index < operations.Length; index++)
        {
            settings[$"Operations:Policy:Rules:{index}:OperationId"] = operations[index];
            settings[$"Operations:Policy:Rules:{index}:Decision"] = "RequireApproval";
            settings[$"Operations:Policy:Rules:{index}:Reason"] = "separate-folder-review";
        }
        return settings;
    }
}
