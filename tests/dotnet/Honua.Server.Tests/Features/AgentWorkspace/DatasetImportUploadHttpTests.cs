// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Configuration;
using Honua.Import.FileImport.Resumable;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Tests.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Actual Kestrel/authentication/workspace provider/Redis/storage and byte verification.
// Destination authorization is a controlled port; this does not accept or execute a GIS import.
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class DatasetImportUploadHttpTests(RedisFixture redis)
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/upload")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/upload")]
    [Endpoint("PUT /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/upload/chunks")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/upload/seal")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/upload/source")]
    public async Task ActualBytesRecoverOriginalTransferAndRetainedSourceWithoutAcceptingJob()
    {
        await using var fixture = await Fixture.Create(redis.ConnectionString);
        using var client = fixture.App.CreateAdminClient();
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\",\"features\":[]}" + new string(' ', 4 * 1024 * 1024));
        var task = await fixture.CreateTask(client, bytes);
        var path = UploadPath(task);
        var hash = task.GetProperty("contentHash").GetString()!;
        var capabilities = await Read(await client.GetAsync(path + "/capabilities?contentHash=" + hash));
        capabilities.GetProperty("chunkBytes").GetInt32().Should().Be(RedisImportUploadStore.MaximumChunkBytes);
        capabilities.GetProperty("maximumFileBytes").GetInt64().Should().Be(32L * 1024 * 1024);
        capabilities.GetProperty("importAcceptanceSupported").GetBoolean().Should().BeFalse();
        (await client.GetAsync(path + "?contentHash=" + hash)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Discard the create reply. The task is the sole recovery identity.
        (await client.PostAsJsonAsync(path, new CreateDatasetImportUpload(hash))).EnsureSuccessStatusCode();
        var original = await Read(await client.GetAsync(path + "?contentHash=" + hash));
        var replay = await Read(await client.PostAsJsonAsync(path, new CreateDatasetImportUpload(hash)));
        replay.GetRawText().Should().Be(original.GetRawText());
        var deadline = original.GetProperty("expiresAt").GetDateTimeOffset();
        var uploadId = original.GetProperty("uploadId").GetString();
        for (var offset = 0; offset < bytes.Length; offset += RedisImportUploadStore.MaximumChunkBytes)
        {
            var count = Math.Min(RedisImportUploadStore.MaximumChunkBytes, bytes.Length - offset);
            var chunk = bytes.AsSpan(offset, count).ToArray();
            var version = replay.GetProperty("version").GetInt64();
            using var body = new UnknownLengthContent(chunk, "application/octet-stream");
            // The actual chunk reply is deliberately discarded; GET supplies the next offset/version.
            var appended = await client.PutAsync(path + $"/chunks?contentHash={hash}&expectedVersion={version}&offset={offset}&sha256={Digest(chunk)}", body);
            appended.EnsureSuccessStatusCode();
            replay = await Read(await client.GetAsync(path + "?contentHash=" + hash));
            replay.GetProperty("acceptedBytes").GetInt64().Should().Be(offset + count);
            replay.GetProperty("uploadId").GetString().Should().Be(uploadId);
            replay.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(deadline);
            using var stale = new ByteArrayContent(chunk);
            stale.Headers.ContentType = new("application/octet-stream");
            (await client.PutAsync(path + $"/chunks?contentHash={hash}&expectedVersion={version}&offset={offset}&sha256={Digest(chunk)}", stale))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        var sealVersion = replay.GetProperty("version").GetInt64();
        var sealedReceipt = await Read(await client.PostAsJsonAsync(path + "/seal", new MutateDatasetImportUpload(hash, sealVersion)));
        sealedReceipt.GetProperty("state").GetString().Should().Be("sealed");
        sealedReceipt.GetProperty("contentSha256").GetString().Should().Be(Digest(bytes));
        // Original seal version is a read-only terminal replay, never another seal.
        (await Read(await client.PostAsJsonAsync(path + "/seal", new MutateDatasetImportUpload(hash, sealVersion)))).GetRawText()
            .Should().Be(sealedReceipt.GetRawText());
        var sourceVersion = sealedReceipt.GetProperty("version").GetInt64();
        (await client.PostAsJsonAsync(path + "/source", new MutateDatasetImportUpload(hash, sourceVersion))).EnsureSuccessStatusCode();
        var retained = await Read(await client.GetAsync(path + "?contentHash=" + hash));
        var retention = retained.GetProperty("sourceRetention");
        retention.GetProperty("bytes").GetInt64().Should().Be(bytes.Length);
        retention.GetProperty("sha256").GetString().Should().Be(Digest(bytes));
        (await Read(await client.PostAsJsonAsync(path + "/source", new MutateDatasetImportUpload(hash, sourceVersion)))).GetRawText()
            .Should().Be(retained.GetRawText());
        var json = retained.GetRawText();
        json.Should().NotContain("fileId").And.NotContain("eTag").And.NotContain("source-").And.NotContain(fixture.Root)
            .And.NotContain("executorAuthority").And.NotContain("claims");
        // Server remains the only holder of provider location/generation.
        var workspace = (await fixture.App.Services.GetRequiredService<IAgentWorkspaceStore>()
            .GetAsync(task.GetProperty("workspaceId").GetString()!))!;
        var internalTask = workspace.DatasetImportTasks[task.GetProperty("taskId").GetString()!].Single();
        var stored = (await new RedisImportUploadStore(fixture.Connection).GetAsync(uploadId!,
            new(internalTask.OwnerActorId, internalTask.TenantId, internalTask.TargetId, internalTask.WorkspaceId,
                internalTask.TaskId, internalTask.Revision)))!;
        stored.StagedSource.Should().NotBeNull();
        await using var retainedBytes = await fixture.Storage.DownloadAsync(stored.StagedSource!.FileId);
        retainedBytes.Should().NotBeNull();
        (await SHA256.HashDataAsync(retainedBytes!)).Should().Equal(SHA256.HashData(bytes));
        workspace.DatasetImportPreparedAdmissions.Should().BeEmpty();

    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/upload")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/upload/capabilities")]
    public async Task UnknownLengthBodiesDenialsAndCancellationRemainBoundToOriginalTask()
    {
        await using var fixture = await Fixture.Create(redis.ConnectionString);
        using var client = fixture.App.CreateAdminClient();
        var bytes = new byte[RedisImportUploadStore.MaximumChunkBytes + 1];
        var task = await fixture.CreateTask(client, bytes);
        var path = UploadPath(task);
        var hash = task.GetProperty("contentHash").GetString()!;
        var original = await Read(await client.PostAsJsonAsync(path, new CreateDatasetImportUpload(hash)));
        var uploadId = original.GetProperty("uploadId").GetString()!;
        using (var overflow = new UnknownLengthContent(bytes, "application/octet-stream"))
        {
            var response = await client.PutAsync(path + $"/chunks?contentHash={hash}&expectedVersion=1&offset=0&sha256={Digest(bytes)}", overflow);
            response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        }
        (await Read(await client.GetAsync(path + "?contentHash=" + hash))).GetProperty("acceptedBytes").GetInt64().Should().Be(0);
        using (var oversizedJson = new UnknownLengthContent(Encoding.UTF8.GetBytes("{\"taskContentHash\":\"" + new string('a', 70000) + "\"}"), "application/json"))
            (await client.PostAsync(path, oversizedJson)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        // Chunk metadata exemption does not grant a 4 MiB body to ordinary JSON routes.
        using (var ordinaryJson = new StringContent(new string('x', 70000), Encoding.UTF8, "application/json"))
            (await client.PostAsync(path + "/seal", ordinaryJson)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await client.PostAsJsonAsync(path, new { taskContentHash = hash, scratchDirectory = "/body-selected", maximumScratchBytes = long.MaxValue }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync(path, new StringContent("null", Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync(path + "/capabilities?contentHash=" + new string('b', 64))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        fixture.Policy.AllowRead = false;
        (await client.GetAsync(path + "/capabilities?contentHash=" + hash)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync(path + "?contentHash=" + hash)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        fixture.Policy.AllowRead = true;
        fixture.Policy.AllowWrite = false;
        using (var chunk = new UnknownLengthContent(bytes[..RedisImportUploadStore.MaximumChunkBytes], "application/octet-stream"))
            (await client.PutAsync(path + $"/chunks?contentHash={hash}&expectedVersion=1&offset=0&sha256={Digest(bytes.AsSpan(0, RedisImportUploadStore.MaximumChunkBytes))}", chunk))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        fixture.Policy.AllowWrite = true;
        var cancelled = await Read(await client.DeleteAsync(path + $"?contentHash={hash}&expectedVersion=1"));
        cancelled.GetProperty("state").GetString().Should().Be("cancelled");
        cancelled.GetProperty("uploadId").GetString().Should().Be(uploadId);
        (await Read(await client.DeleteAsync(path + $"?contentHash={hash}&expectedVersion=1"))).GetRawText().Should().Be(cancelled.GetRawText());
        using var anonymous = fixture.App.CreateClient();
        (await anonymous.GetAsync(path + "?contentHash=" + hash)).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [IntegrationTheory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task MissingRegistrationOrMismatchedStorageCannotAdvertiseTransfer(bool install, bool mismatched, bool mismatchedStorage)
    {
        await using var fixture = await Fixture.Create(redis.ConnectionString, install, mismatched, mismatchedStorage: mismatchedStorage);
        using var client = fixture.App.CreateAdminClient();
        var task = await fixture.CreateTask(client, "{}"u8.ToArray());
        var response = await client.GetAsync(UploadPath(task) + "/capabilities?contentHash=" + task.GetProperty("contentHash").GetString());
        response.StatusCode.Should().Be(install ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision}/upload")]
    public async Task ActualHumanAndExecutorShareOriginalTransferWithSeparateMutationAudit()
    {
        await using var fixture = await Fixture.Create(redis.ConnectionString, handoff: true);
        using var bootstrap = fixture.App.CreateAdminClient();
        var keyJson = await Read(await bootstrap.PostAsJsonAsync("/api/v1/admin/api-keys", new
        { name = "upload-executor", permissions = new[] { "admin:read", "agent:workspace" } }));
        var key = keyJson.GetProperty("data");
        var keyId = key.GetProperty("apiKey").GetProperty("id").GetString()!;
        var executor = $"apikey:api-key:{keyId}";
        fixture.App.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = executor;
        var bearer = fixture.App.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
            [new() { Type = ClaimTypes.NameIdentifier, Value = "upload-human" }, new() { Type = ClaimTypes.Role, Value = "admin" },
             new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(5));
        bearer.Should().NotBeNull();
        using var human = fixture.App.CreateClient(client => client.DefaultRequestHeaders.Authorization = new("Bearer", bearer!.Token));
        using var agent = fixture.App.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key.GetProperty("key").GetString()));
        var workspace = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "handoff-tab" }));
        var workspaceId = workspace.GetProperty("workspaceId").GetString()!;
        var root = $"/api/v1/agent/workspaces/{workspaceId}";
        var bytes = "{\"type\":\"FeatureCollection\",\"features\":[]}"u8.ToArray();
        await Read(await human.PostAsJsonAsync(root + "/turns", new
        {
            turnId = "handoff-origin",
            viewId = "handoff-tab",
            contextRevision = 0,
            prompt = "Import the selected dataset",
            browserFileSelection = new BrowserImportFileSelection("selected-handoff", "handoff.geojson", bytes.Length, Digest(bytes))
        }));
        agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
        agent.DefaultRequestHeaders.Add("X-Honua-Turn", "handoff-origin");
        var task = await Read(await agent.PostAsJsonAsync(root + "/dataset-import-tasks", new CreateDatasetImportTask(
            "executor-import-intent", "handoff-tab", 0, new("public", "new_handoff_table"),
            new("browser-file", "handoff.geojson", bytes.Length, Digest(bytes), BrowserSelectionId: "selected-handoff"), new())));
        task.GetProperty("ownerActorId").GetString().Should().Be(executor);
        var path = UploadPath(task);
        var hash = task.GetProperty("contentHash").GetString()!;
        var original = await Read(await human.PostAsJsonAsync(path, new CreateDatasetImportUpload(hash)));
        using (var chunk = new UnknownLengthContent(bytes, "application/octet-stream"))
            (await agent.PutAsync(path + $"/chunks?contentHash={hash}&expectedVersion=1&offset=0&sha256={Digest(bytes)}", chunk)).EnsureSuccessStatusCode();
        var accepted = await Read(await human.GetAsync(path + "?contentHash=" + hash));
        accepted.GetProperty("uploadId").GetString().Should().Be(original.GetProperty("uploadId").GetString());
        var sealedReceipt = await Read(await human.PostAsJsonAsync(path + "/seal", new MutateDatasetImportUpload(hash, accepted.GetProperty("version").GetInt64())));
        sealedReceipt.GetProperty("state").GetString().Should().Be("sealed");
        var storedWorkspace = (await fixture.App.Services.GetRequiredService<IAgentWorkspaceStore>().GetAsync(workspaceId))!;
        var immutableTask = storedWorkspace.DatasetImportTasks[task.GetProperty("taskId").GetString()!].Single();
        var mutations = await new RedisImportUploadStore(fixture.Connection).ReadMutationsAsync(original.GetProperty("uploadId").GetString()!,
            new(executor, immutableTask.TenantId, immutableTask.TargetId, workspaceId, immutableTask.TaskId, immutableTask.Revision), 1, 3);
        mutations.Select(mutation => mutation.ContributorActorId).Should().Equal(storedWorkspace.OwnerId, executor, storedWorkspace.OwnerId);
        immutableTask.OwnerActorId.Should().Be(executor);
        storedWorkspace.DatasetImportPreparedAdmissions.Should().BeEmpty();
        (await human.GetAsync(root + "/dataset-import-task-intents/executor-import-intent")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Revocation of the actual executor also stops human contributor observations.
        (await bootstrap.PostAsync($"/api/v1/admin/api-keys/{keyId}/revoke", null)).EnsureSuccessStatusCode();
        (await human.GetAsync(path + "?contentHash=" + hash)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [UnitTest]
    public void ScratchBindingAndEffectiveCapsUseCanonicalLimitsWithoutAllocation()
    {
        var root = Path.Join(Path.GetTempPath(), "upload-binding-" + Guid.NewGuid().ToString("N"));
        var allocator = new ImportScratchStorage(root, Substitute.For<IImportStorageBudget>());
        allocator.IsBoundToDirectory(root + Path.DirectorySeparatorChar + ".").Should().BeTrue();
        allocator.IsBoundToDirectory(null).Should().BeFalse();
        allocator.IsBoundToDirectory("relative").Should().BeFalse();
        allocator.IsBoundToDirectory(root + "\0").Should().BeFalse();
        allocator.IsBoundToDirectory(root + "-other").Should().BeFalse();
        Directory.Exists(root).Should().BeFalse();
        new DatasetImportUploadTransportOptions(root, long.MaxValue, long.MaxValue).EffectiveMaximumFileBytes
            .Should().Be((long)RedisImportUploadStore.MaximumChunkBytes * RedisImportUploadStore.MaximumChunks);
        new DatasetImportUploadTransportOptions(null!, 1, 1).IsConfigured.Should().BeFalse();
    }

    private static string UploadPath(JsonElement task)
        => $"/api/v1/agent/workspaces/{task.GetProperty("workspaceId").GetString()}/dataset-import-tasks/{task.GetProperty("taskId").GetString()}/versions/1/upload";
    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed class DestinationPolicy : IDatasetImportDestinationAuthorizer
    {
        internal bool AllowRead { get; set; } = true;
        internal bool AllowWrite { get; set; } = true;
        public Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, string targetId, DatasetImportDestination destination,
            DatasetImportTaskOptions options, bool requireWrite, CancellationToken cancellationToken = default)
            => Task.FromResult(requireWrite ? AllowWrite : AllowRead);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Join(Path.GetTempPath(), "honua-upload-http-" + Guid.NewGuid().ToString("N"));
        internal WebAppFixture App { get; private set; } = null!;
        internal DestinationPolicy Policy { get; } = new();
        internal ConnectionMultiplexer Connection { get; private set; } = null!;
        internal LocalFileStorage Storage { get; private set; } = null!;
        internal static async Task<Fixture> Create(string redisConnection, bool install = true, bool mismatch = false, bool handoff = false, bool mismatchedStorage = false)
        {
            var fixture = new Fixture();
            fixture.Connection = await ConnectionMultiplexer.ConnectAsync(redisConnection);
            fixture.Storage = new LocalFileStorage(Options.Create(new LocalStorageOptions { BasePath = fixture.Root, CreateDirectoryIfNotExists = true }),
                NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>());
            var budget = await ImportStorageBudgetTestFactory.CreateAsync(fixture.Connection, fixture.Root,
                new ImportStorageBudgetLimits(128L * 1024 * 1024, 128L * 1024 * 1024, 128L * 1024 * 1024, 100));
            var scratch = new ImportScratchStorage(Path.Join(fixture.Root, "scratch"), await ImportStorageBudgetTestFactory.CreateAsync(fixture.Connection,
                fixture.Root + ":scratch", new ImportStorageBudgetLimits(128L * 1024 * 1024, 128L * 1024 * 1024, 128L * 1024 * 1024, 100)));
            fixture.App = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
            {
                builder.UseSetting("ConnectionStrings:redis", redisConnection);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                if (handoff)
                {
                    builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
                    builder.UseSetting("Authentication:OperatorBearer:SigningKey", "upload-http-human-handoff-test-key-at-least-32-bytes");
                }
                builder.UseSetting("AgentWorkspace:DeploymentId", "upload-http-" + Guid.NewGuid().ToString("N"));
                builder.UseSetting("AgentWorkspace:ReleaseId", "upload-http-test");
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IDatasetImportDestinationAuthorizer>(fixture.Policy);
                    if (!install) return;
                    services.AddSingleton(new ImportStagedSourceStorage(fixture.Storage));
                    services.AddSingleton<IImportStorageBudget>(budget);
                    if (handoff) services.AddScoped<IDatasetImportContributorAuthorizer, DatasetImportContributorAuthorizer>();
                    services.AddSingleton(scratch);
                    services.AddSingleton(new DatasetImportUploadTransportOptions(Path.Join(fixture.Root, mismatch ? "wrong-scratch" : "scratch"),
                        32L * 1024 * 1024, 48L * 1024 * 1024));
                    services.AddScoped(sp => new ImportUploadCoordinator(new ImportUploadTaskAuthority(sp.GetRequiredService<IDatasetImportTaskProvider>(),
                        sp.GetService<IDatasetImportContributorAuthorizer>()), new RedisImportUploadStore(fixture.Connection),
                        mismatchedStorage ? new ImportStagedSourceStorage(Substitute.For<ICloudFileStorage>()) : sp.GetRequiredService<ImportStagedSourceStorage>(), budget, scratch));
                });
            });
            await fixture.App.InitializeAsync();
            return fixture;
        }
        internal async Task<JsonElement> CreateTask(HttpClient client, byte[] bytes)
        {
            var workspace = await Read(await client.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "upload-tab" }));
            return await Read(await client.PostAsJsonAsync($"/api/v1/agent/workspaces/{workspace.GetProperty("workspaceId").GetString()}/dataset-import-tasks",
                new CreateDatasetImportTask("upload-intent", "upload-tab", 0, new("public", "new_table"),
                    new("browser-file", "input.geojson", bytes.Length, Digest(bytes)), new())));
        }
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;
        internal UnknownLengthContent(byte[] bytes, string contentType)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_bytes).AsTask();
    }
}
