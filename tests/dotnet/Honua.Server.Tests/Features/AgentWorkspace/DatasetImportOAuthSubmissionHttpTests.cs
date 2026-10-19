// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.FileStorage;
using Honua.Import.FileImport.Resumable;
using Honua.Server.Features.Import;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Actual Kestrel, RSA JWT validation, canonical delegated capture and Redis CAS.
// Membership/grant discovery and queue delivery are controlled ports. Actual bytes, Redis acceptance,
// production task scope and lease admission are exercised; this test does not execute the GIS worker.
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
public sealed class DatasetImportOAuthSubmissionHttpTests(RedisFixture redis)
{
    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/submission")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/submission")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/authority-renewals")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/authority")]
    [Endpoint("POST /mcp tools/call honua_workspace_renew_dataset_import_authority")]
    public async Task RefreshedSignedExecutorAcceptsHumanPreparedOriginalUploadThroughCanonicalDurableQueue()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-157.8,21.3]},"properties":{"name":"Oahu"}}]}""");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var storageRoot = Path.Join(Path.GetTempPath(), "honua-oauth-submission-" + Guid.NewGuid().ToString("N"));
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var storage = new LocalFileStorage(Options.Create(new LocalStorageOptions { BasePath = storageRoot, CreateDirectoryIfNotExists = true }),
            NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>());
        var budget = new RedisImportStorageBudget(connection, storageRoot, new ImportStorageBudgetLimits(32L * 1024 * 1024, 32L * 1024 * 1024, 32L * 1024 * 1024, 20));
        var scratch = new ImportScratchStorage(Path.Join(storageRoot, "scratch"), new RedisImportStorageBudget(connection, storageRoot + ":scratch",
            new ImportStorageBudgetLimits(32L * 1024 * 1024, 32L * 1024 * 1024, 32L * 1024 * 1024, 20)));
        var delivery = Substitute.For<IJobQueue>();
        const string issuer = "https://import-workload.example";
        const string audience = "honua-workloads";
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "import-renewal-signing-key" };
        var membership = Substitute.For<IPrincipalMembershipSource>();
        membership.ResolveMembershipAsync("uploader", issuer, Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["worker"]));
        var roles = Substitute.For<IRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions
            {
                UserId = "uploader", Roles = ["worker"], Permissions =
                [new() { Service = "agent", Layer = "*", Operation = "workspace" },
                 new() { Service = "admin", Layer = "*", Operation = "read" },
                 new() { Service = "admin", Layer = "*", Operation = "write" }],
            });
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("DataSource:Provider", "PostGIS");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "oauth-import-human-selection-test-key-at-least-32-bytes");
            builder.UseSetting("AgentWorkspace:DeploymentId", $"oauth-renewal-{Guid.NewGuid():N}");
            builder.UseSetting("AgentWorkspace:ReleaseId", "isolated-oauth-renewal-test");
            builder.UseSetting("Oidc:Enabled", "true");
            builder.UseSetting("Oidc:Generic:Enabled", "true");
            builder.UseSetting("Oidc:Generic:Authority", issuer);
            builder.UseSetting("Oidc:Generic:ClientId", audience);
            builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
        }).ConfigureServices(services =>
        {
            services.RemoveAll<IPrincipalMembershipSource>();
            services.AddSingleton(membership);
            services.RemoveAll<IRoleStore>();
            services.AddSingleton(roles);
            services.AddScoped<IDatasetImportDestinationAuthorizer, DatasetImportDestinationAuthorizer>();
            services.AddSingleton<ICloudFileStorage>(storage);
            services.AddSingleton<IImportStorageBudget>(budget);
            services.AddSingleton(scratch);
            services.AddSingleton(new DatasetImportUploadTransportOptions(Path.Join(storageRoot, "scratch"), 16L * 1024 * 1024, 24L * 1024 * 1024));
            services.AddScoped(sp => new ImportUploadCoordinator(new ImportUploadTaskAuthority(sp.GetRequiredService<IDatasetImportTaskProvider>(),
                sp.GetRequiredService<IDatasetImportContributorAuthorizer>()), new RedisImportUploadStore(connection), storage, budget, scratch));
            services.AddScoped<IDatasetImportAdmissionProvider, DatasetImportAdmissionProvider>();
            services.AddScoped<DatasetImportSubmissionService>();
            services.AddScoped<IJobExecutionLeasePolicy, ExecutionJobLeasePolicy>();
            services.AddScoped<IJobExecutionLeaseObservationPolicy>(sp => (ExecutionJobLeasePolicy)sp.GetRequiredService<IJobExecutionLeasePolicy>());
            services.AddScoped<IJobExecutionLeaseScopeProvider, FileImportExecutionLeaseScopeProvider>();
            services.AddScoped<JobExecutionLeaseAuthority>();
            services.AddSingleton<IJobExecutionLeaseAuthority, JobExecutionLeaseAuthorityFacade>();
            services.AddSingleton<IExecutionJobStore>(new RedisExecutionJobStore(connection, NullLogger<RedisExecutionJobStore>.Instance));
            services.AddSingleton(BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false));
            services.AddSingleton<IImportJobService>(sp => new DurableImportJobService(Substitute.For<IImportJobService>(),
                sp.GetRequiredService<BackgroundImportRunnerDecision>(), sp.GetRequiredService<IUniversalProgressStore>(),
                sp.GetRequiredService<IServiceScopeFactory>(), [], NullLogger<DurableImportJobService>.Instance,
                sp.GetRequiredService<IExecutionJobStore>(), delivery));
            services.AddScoped<IDatasetImportContributorAuthorizer, DatasetImportContributorAuthorizer>();
            services.AddScoped<IDatasetImportAuthorityDescriptorReader, DatasetImportAuthorityDescriptorReader>();
            services.Configure<OidcAuthenticationOptions>(options =>
            {
                options.DelegatedAgentAudiences = [new() { Issuer = issuer, Audience = audience }];
                options.DelegatedAgents = [new()
                {
                    Id = "upload-agent", Generation = 1, Enabled = true, Issuer = issuer, Audience = audience,
                    Subject = "uploader", Tenant = "public", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
                    Permissions = ["agent:workspace", "admin:read", "admin:write"],
                }];
            });
            services.PostConfigure<JwtBearerOptions>(OidcAuthenticationExtensions.JwtBearerScheme, options =>
            {
                var discovery = new OpenIdConnectConfiguration { Issuer = issuer };
                discovery.SigningKeys.Add(signingKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
            });
        });
        await fixture.InitializeAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var firstExpiry = WholeSecond(now.AddMinutes(5));
            var secondExpiry = WholeSecond(now.AddMinutes(20));
            var thirdExpiry = WholeSecond(now.AddMinutes(40));
            using var client = fixture.CreateClient();
            var tokenSequence = 0;
            Authenticate(firstExpiry);
            var discovery = await Read(await client.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "discovery-tab" }));
            var discoveryDocument = (await fixture.Services.GetRequiredService<IAgentWorkspaceStore>()
                .GetAsync(discovery.GetProperty("workspaceId").GetString()!))!;
            fixture.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = discoveryDocument.OwnerId;
            var humanBearer = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = "oauth-import-human" }, new() { Type = ClaimTypes.Role, Value = "admin" },
                 new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(5));
            humanBearer.Should().NotBeNull();
            using var human = fixture.CreateClient(c => c.DefaultRequestHeaders.Authorization = new("Bearer", humanBearer!.Token));
            var created = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "oauth-tab" }));
            var workspaceId = created.GetProperty("workspaceId").GetString()!;
            var root = $"/api/v1/agent/workspaces/{workspaceId}";
            await Read(await human.PostAsJsonAsync(root + "/turns", new
                { turnId = "oauth-origin", viewId = "oauth-tab", contextRevision = 0, prompt = "Prepare the selected import",
                  browserFileSelection = new BrowserImportFileSelection("oauth-selected-file", "roads.geojson", bytes.Length, digest) }));
            client.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
            client.DefaultRequestHeaders.Add("X-Honua-Turn", "oauth-origin");
            var taskJson = await Read(await client.PostAsJsonAsync(root + "/dataset-import-tasks", new CreateDatasetImportTask(
                "oauth-import-intent", "oauth-tab", 0, new("public", "oauth_new_" + Guid.NewGuid().ToString("N")),
                new("browser-file", "roads.geojson", bytes.Length, digest, BrowserSelectionId: "oauth-selected-file"), new())));
            var taskId = taskJson.GetProperty("taskId").GetString()!;
            var store = fixture.Services.GetRequiredService<IAgentWorkspaceStore>();
            var originalDocument = (await store.GetAsync(workspaceId))!;
            var originalTask = originalDocument.DatasetImportTasks[taskId].Single();
            var originalBinding = originalDocument.DatasetImportExecutionBindings[taskId];
            originalBinding.ExecutorAuthority.Kind.Should().Be("oidc-delegated-agent");
            originalBinding.ExecutorAuthority.TokenExpiresAt.Should().Be(firstExpiry);
            var authorityPath = root + $"/dataset-import-tasks/{taskId}/versions/1/authority";
            var renewalPath = authorityPath + "-renewals";
            var firstRenewal = new RenewDatasetImportAuthority("oauth-renewal-1", 1, originalTask.ContentHash);
            Authenticate(secondExpiry);
            // Discard the response; recover by the original immutable renewal identity.
            (await client.PostAsJsonAsync(renewalPath, firstRenewal)).StatusCode.Should().Be(HttpStatusCode.OK);
            var afterFirst = (await store.GetAsync(workspaceId))!;
            afterFirst.DatasetImportExecutionBindings[taskId].AuthorityRevision.Should().Be(2);
            afterFirst.DatasetImportExecutionBindings[taskId].ExecutorAuthority.TokenExpiresAt.Should().Be(secondExpiry);
            AssertOriginalIntent(afterFirst);
            Authenticate(thirdExpiry);
            var recovered = await Read(await client.PostAsJsonAsync(renewalPath, firstRenewal));
            recovered.GetProperty("authorityRevision").GetInt64().Should().Be(2);
            (await store.GetAsync(workspaceId))!.Version.Should().Be(afterFirst.Version);
            (await store.GetAsync(workspaceId))!.DatasetImportExecutionBindings[taskId].ExecutorAuthority.TokenExpiresAt.Should().Be(secondExpiry);
            var descriptor = await Read(await client.GetAsync(authorityPath + "?contentHash=" + originalTask.ContentHash));
            descriptor.GetProperty("authorityRevision").GetInt64().Should().Be(2);
            var initialized = await client.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "oauth-renewal-test", version = "1" } },
            });
            initialized.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Add("Mcp-Session-Id", initialized.Headers.GetValues("Mcp-Session-Id").Single());
            var secondRenewal = new RenewDatasetImportAuthority("oauth-renewal-2", 2, originalTask.ContentHash);
            var mcp = await RenewThroughMcp(secondRenewal, 2);
            mcp.GetProperty("authorityRevision").GetInt64().Should().Be(3);
            var afterSecond = (await store.GetAsync(workspaceId))!;
            afterSecond.DatasetImportExecutionBindings[taskId].ExecutorAuthority.TokenExpiresAt.Should().Be(thirdExpiry);
            afterSecond.DatasetImportAuthorityRenewals.Should().HaveCount(2);
            AssertOriginalIntent(afterSecond);
            (await RenewThroughMcp(secondRenewal, 3)).GetProperty("authorityRevision").GetInt64().Should().Be(3);
            (await store.GetAsync(workspaceId))!.Version.Should().Be(afterSecond.Version);
            var uploadPath = root + $"/dataset-import-tasks/{taskId}/versions/1/upload";
            var upload = await Read(await human.PostAsJsonAsync(uploadPath, new CreateDatasetImportUpload(originalTask.ContentHash)));
            using (var body = new ByteArrayContent(bytes))
            {
                body.Headers.ContentType = new("application/octet-stream");
                upload = await Read(await human.PutAsync(uploadPath + $"/chunks?contentHash={originalTask.ContentHash}&expectedVersion={upload.GetProperty("version").GetInt64()}&offset=0&sha256={digest}", body));
            }
            upload = await Read(await human.PostAsJsonAsync(uploadPath + "/seal", new MutateDatasetImportUpload(originalTask.ContentHash, upload.GetProperty("version").GetInt64())));
            upload = await Read(await human.PostAsJsonAsync(uploadPath + "/source", new MutateDatasetImportUpload(originalTask.ContentHash, upload.GetProperty("version").GetInt64())));
            upload.GetProperty("sourceRetention").GetProperty("sha256").GetString().Should().Be(digest);
            var currentAuthority = await Read(await client.GetAsync(authorityPath + "?contentHash=" + originalTask.ContentHash));
            var submit = new SubmitDatasetImport(originalTask.ContentHash, currentAuthority.GetProperty("authorityRevision").GetInt64(),
                currentAuthority.GetProperty("authorityFingerprint").GetString()!, currentAuthority.GetProperty("policyContextFingerprint").GetString()!);
            var submissionPath = root + $"/dataset-import-tasks/{taskId}/versions/1/submission";
            var prepared = await Read(await human.PostAsJsonAsync(submissionPath, submit));
            prepared.GetProperty("state").GetString().Should().Be("prepared");
            prepared.TryGetProperty("job", out _).Should().BeFalse();
            var operationId = prepared.GetProperty("operationId").GetString()!;
            (await fixture.Services.GetRequiredService<IExecutionJobStore>().GetAsync(operationId)).Should().BeNull();
            // Actual refreshed RSA-authenticated executor, not a principal reconstructed from the human.
            var accepted = await Read(await client.PostAsJsonAsync(submissionPath, submit));
            accepted.GetProperty("state").GetString().Should().Be("accepted");
            accepted.GetProperty("job").GetProperty("jobId").GetString().Should().Be(operationId);
            var storedJob = (await fixture.Services.GetRequiredService<IExecutionJobStore>().GetAsync(operationId))!;
            storedJob.Audit.SubmitterSecurityContext!.AgentCredential!.TokenExpiresAt.Should().Be(thirdExpiry);
            storedJob.Audit.SubmitterSecurityContext.Claims.Should().Equal(originalBinding.ExecutorPolicyContext.Claims);
            storedJob.Audit.SubmitterSecurityContext.ExecutionLease.Should().NotBeNull();
            // Discarded success replies are recovered by GET; reads never dispatch.
            (await Read(await human.GetAsync(submissionPath + "?contentHash=" + originalTask.ContentHash))).GetProperty("operationId").GetString().Should().Be(operationId);
            (await Read(await client.PostAsJsonAsync(submissionPath, submit))).GetProperty("operationId").GetString().Should().Be(operationId);
            await delivery.Received(1).EnqueueAsync(operationId, Arg.Any<Honua.Core.Features.ControlPlane.Domain.OperationPriority>(), Arg.Any<CancellationToken>());
            var acceptedDocumentVersion = (await store.GetAsync(workspaceId))!.Version;
            Authenticate(WholeSecond(now.AddMinutes(50)), "south");
            (await client.PostAsJsonAsync(renewalPath, new RenewDatasetImportAuthority("changed-policy", 3, originalTask.ContentHash)))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await store.GetAsync(workspaceId))!.Version.Should().Be(acceptedDocumentVersion);
            membership.ResolveMembershipAsync("uploader", issuer, Arg.Any<CancellationToken>()).Returns(new PrincipalMembership(false, []));
            (await client.GetAsync(authorityPath + "?contentHash=" + originalTask.ContentHash)).StatusCode
                .Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);

            void Authenticate(DateTimeOffset expiry, string department = "north")
            {
                var token = new JwtSecurityToken(issuer, audience,
                    [new("sub", "uploader"), new("tenant_id", "public"), new("scope", "agent:workspace honua.mcp.read honua.mcp.create honua.mcp.update"), new("department", department),
                     new("jti", Guid.NewGuid().ToString("N")), new("iat", now.AddMinutes(-1).AddSeconds(++tokenSequence).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
                    now.AddMinutes(-2).UtcDateTime, expiry.UtcDateTime, new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            }

            void AssertOriginalIntent(AgentWorkspaceDocument document)
            {
                document.DatasetImportTasks[taskId].Single().Should().Be(originalTask);
                var binding = document.DatasetImportExecutionBindings[taskId];
                binding.PolicyContextFingerprint.Should().Be(originalBinding.PolicyContextFingerprint);
                binding.ExecutorPolicyContext.Claims.Should().Equal(originalBinding.ExecutorPolicyContext.Claims);
                binding.ExecutorPolicyContext.AgentCredential!.TokenExpiresAt.Should().Be(firstExpiry);
                binding.OriginalTurnHash.Should().Be(originalBinding.OriginalTurnHash);
                binding.ExecutorActorId.Should().Be(originalBinding.ExecutorActorId);
            }

            async Task<JsonElement> RenewThroughMcp(RenewDatasetImportAuthority request, int id)
            {
                var envelope = await Read(await client.PostAsJsonAsync("/mcp", new
                {
                    jsonrpc = "2.0", id, method = "tools/call",
                    @params = new { name = "honua_workspace_renew_dataset_import_authority", arguments = new
                        { workspaceId, taskId, revision = 1, request } },
                }));
                envelope.TryGetProperty("error", out _).Should().BeFalse();
                return envelope.GetProperty("result").GetProperty("structuredContent");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    private static DateTimeOffset WholeSecond(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
    }

}
