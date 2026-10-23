// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Server.Features.AgentWorkspace;
using Honua.ServiceDefaults;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Server.Tests.Features.AgentWorkspace;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Import)]
public sealed class DatasetImportProductionHttpTests(RedisFixture redis)
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("POST /mcp")]
    public async Task NativeTask_ExactDestinationKeyImportsHumanAttachmentThroughActualWorker(bool previouslyApplied136)
    {
        if (!OperatingSystem.IsLinux()) return;
        var submissionActivities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HonuaTelemetry.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName is "agent.dataset-import.submit" or "agent.dataset-import.observe" || activity.Events.Any(item => item.Name == "exception")) submissionActivities.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        string? database = null;
        var root = Path.Join(Path.GetTempPath(), "honua-native-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connection = await postgres.CreateIsolatedDatabaseAsync(nameof(DatasetImportProductionHttpTests));
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var bridgeKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var settings = new Dictionary<string, string?>
            {
                ["HONUA_SKIP_MIGRATIONS"] = "false",
                ["HONUA_DEV_AUTH"] = "false",
                ["HONUA_DEV_AUTH_ALLOW_BYPASS"] = "false",
                ["HONUA_ADMIN_PASSWORD"] = password,
                ["ConnectionStrings:DefaultConnection"] = connection,
                ["ConnectionStrings:honua"] = connection,
                ["ConnectionStrings:redis"] = redis.ConnectionString,
                ["DataSource:Provider"] = "PostGIS",
                ["Licensing:DevGrantEdition"] = "Pro",
                ["Database:Schema"] = "honua",
                ["AgentWorkspace:DeploymentId"] = "bounded-import-" + Guid.NewGuid().ToString("N"),
                ["AgentWorkspace:ReleaseId"] = "native-import-test",
                ["Security:ConnectionEncryption:MasterKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["Security:ConnectionEncryption:Salt"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Observability:OpsHealthRollup:Enabled"] = "false",
                ["Authentication:OperatorBearer:Enabled"] = "true",
                ["Authentication:OperatorBearer:SigningKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["AgentWorkspace:LocalHumanBridge:Enabled"] = "true",
                ["AgentWorkspace:LocalHumanBridge:Key"] = bridgeKey,
                ["AgentWorkspace:LocalHumanBridge:TenantId"] = "public",
                ["FileStorage:Provider"] = "Local",
                ["FileStorage:LocalStorage:BasePath"] = Path.Join(root, "general"),
                ["AgentWorkspace:DatasetImport:LocalStorage:Enabled"] = "true",
                ["AgentWorkspace:DatasetImport:LocalStorage:RootDirectory"] = Path.Join(root, "imports"),
                ["AgentWorkspace:DatasetImport:LocalStorage:DomainId"] = Guid.NewGuid().ToString("N"),
                ["AgentWorkspace:DatasetImport:LocalStorage:GlobalBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:TenantBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:OwnerBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumObjects"] = "32",
                ["AgentWorkspace:DatasetImport:LocalStorage:JournalCapacity"] = "32",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumScratchBytes"] = "16777216",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumFileBytes"] = "16777216"
            };
            WebApplicationFactory<Program> CreateFactory() => ConfiguredWebApplicationFactory.Create(builder =>
            {
                foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
                builder.ConfigureLogging(logging => logging.AddProvider(new ImportFailureDiagnosticLoggerProvider()));
            }, "Development");
            if (previouslyApplied136)
            {
                // Provision through the real default host, then stage the exact old
                // embedded receipt definition in this empty isolated database. No
                // requested import/job exists during migration input preparation.
                await using var preparation = CreateFactory();
                preparation.UseKestrel(0);
                using var preparationClient = preparation.CreateClient();
                var assembly = typeof(Program).Assembly;
                var correction = assembly.GetManifestResourceNames().Single(name =>
                    name.EndsWith("139_FixImportCommitReceiptFingerprintConstraint.sql", StringComparison.Ordinal));
                await using var originalResource = assembly.GetManifestResourceStream(
                    assembly.GetManifestResourceNames().Single(name => name.EndsWith("136_CreateImportCommitReceipts.sql", StringComparison.Ordinal)))!;
                using var originalReader = new StreamReader(originalResource);
                var originalSql = (await originalReader.ReadToEndAsync()).Replace("$HonuaSchema$", "\"honua\"", StringComparison.Ordinal);
                await using (var previous = new NpgsqlConnection(connection))
                {
                    await previous.OpenAsync();
                    await using var restore = new NpgsqlCommand("DROP TABLE honua.import_commit_receipt; " + originalSql +
                        " DELETE FROM public.schema_versions WHERE scriptname = @script; " +
                        "DELETE FROM public.schema_version_reader_compatibility WHERE script_name = @script", previous);
                    restore.Parameters.AddWithValue("script", correction);
                    await restore.ExecuteNonQueryAsync();
                }
                var runner = preparation.Services.GetRequiredService<IDatabaseMigrationRunner>();
                var plan = await runner.PlanMigrationsAsync(connection, assembly);
                plan.Successful.Should().BeTrue(plan.Error?.ToString());
                plan.PendingScripts.Should().Equal(correction);
                var blocked = await runner.RunMigrationsAsync(connection, assembly);
                blocked.Successful.Should().BeFalse("an existing database still requires its exact reviewed contract approval");
                settings[MigrationSafetyOptions.ApproveContractMigrationsKey] = plan.ContractApprovalNonce
                    ?? throw new InvalidOperationException("The actual migration plan did not return its required contract nonce.");
            }
            await using var factory = CreateFactory();
            factory.UseKestrel(0);
            using var setup = factory.CreateClient();
            setup.DefaultRequestHeaders.Add("X-API-Key", password);
            using var human = factory.CreateClient();
            human.DefaultRequestHeaders.Add("X-Honua-Local-Bridge", bridgeKey);
            var session = await Read(await human.PostAsJsonAsync("/api/v1/admin/auth/local-console-session", new { subject = "import-owner" }), HttpStatusCode.OK);
            human.DefaultRequestHeaders.Remove("X-Honua-Local-Bridge");
            human.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
            var target = await Read(await human.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
            var targetId = target.GetProperty("targetId").GetString()!;
            const string table = "requested_points";
            var issued = await Read(await setup.PostAsJsonAsync("/api/v1/admin/api-keys", new
            {
                name = "bounded-import-worker",
                permissions = new[] { "admin:read", "agent:workspace", $"agent:dataset-import:{targetId}/public/{table}" }
            }), HttpStatusCode.Created);
            var permissions = issued.GetProperty("data").GetProperty("apiKey").GetProperty("permissions").EnumerateArray().Select(value => value.GetString()).ToArray();
            permissions.Should().NotContain("admin:write").And.NotContain("*");
            using var agent = factory.CreateClient();
            agent.DefaultRequestHeaders.Add("X-API-Key", issued.GetProperty("data").GetProperty("key").GetString());
            var identity = await Read(await agent.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
            factory.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = identity.GetProperty("principal").GetProperty("actorId").GetString();
            var workspace = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "import-view", targetId }), HttpStatusCode.OK);
            var workspaceId = workspace.GetProperty("workspaceId").GetString()!;
            var bytes = Encoding.UTF8.GetBytes("""{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"alpha","value":5},"geometry":{"type":"Point","coordinates":[-157.86,21.31]}},{"type":"Feature","properties":{"name":"beta","value":9},"geometry":{"type":"Point","coordinates":[-158,22]}}]}""");
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            const string selectionId = "original-file";
            var workspacePath = "/api/v1/agent/workspaces/" + workspaceId;
            await Read(await human.PostAsJsonAsync(workspacePath + "/turns", new
            {
                turnId = "import-turn",
                viewId = "import-view",
                contextRevision = workspace.GetProperty("revision").GetInt64(),
                prompt = "Import the selected original file only into public.requested_points.",
                browserFileSelection = new { selectionId, fileName = "original.geojson", totalBytes = bytes.Length, expectedSha256 = sha256 }
            }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "import-turn");
            using var initialize = await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = "initialize",
                method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "native-import", version = "1" }, _meta = new Dictionary<string, string> { ["honua.io/workflow-view"] = "workspace" } }
            });
            initialize.EnsureSuccessStatusCode();
            agent.DefaultRequestHeaders.Add("Mcp-Session-Id", initialize.Headers.GetValues("Mcp-Session-Id").Single());
            var discovery = await Read(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = "discover",
                method = "tools/list",
                @params = new { }
            }), HttpStatusCode.OK);
            var published = discovery.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            foreach (var toolName in new[] { "honua_workspace_create_dataset_import_task", "honua_workspace_get_dataset_import_authority", "honua_workspace_submit_dataset_import", "honua_workspace_get_dataset_import_submission" })
            {
                var descriptor = published.Single(tool => tool.GetProperty("name").GetString() == toolName);
                descriptor.GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
            }
            object Request(string intentId, string destinationTable) => new
            {
                workspaceId,
                request = new
                {
                    intentId,
                    viewId = "import-view",
                    contextRevision = 0,
                    destination = new { schema = "public", table = destinationTable },
                    source = new { kind = "browser-file", fileName = "original.geojson", totalBytes = bytes.Length, expectedSha256 = sha256, browserSelectionId = selectionId },
                    options = new { loadMode = 0, targetSrid = 4326, sourceSrid = 4326 }
                }
            };
            (await Rpc(agent, "honua_workspace_create_dataset_import_task", Request("foreign", "other_points")))
                .GetProperty("isError").GetBoolean().Should().BeTrue();
            var task = Success(await Rpc(agent, "honua_workspace_create_dataset_import_task", Request("original", table)));
            var taskId = task.GetProperty("taskId").GetString()!;
            var hash = task.GetProperty("contentHash").GetString()!;
            var revision = task.GetProperty("revision").GetInt64();
            var taskPath = $"{workspacePath}/dataset-import-tasks/{taskId}/versions/{revision}";
            var uploadPath = taskPath + "/upload";
            // The separate human contributes the selected bytes. Only the agent submits the import.
            var upload = await Read(await human.PostAsJsonAsync(uploadPath, new { taskContentHash = hash }), HttpStatusCode.OK);
            using (var body = new ByteArrayContent(bytes))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                upload = await Read(await human.PutAsync(uploadPath + $"/chunks?contentHash={hash}&expectedVersion={upload.GetProperty("version").GetInt64()}&offset=0&sha256={sha256}", body), HttpStatusCode.OK);
            }
            upload = await Read(await human.PostAsJsonAsync(uploadPath + "/seal", new { taskContentHash = hash, expectedVersion = upload.GetProperty("version").GetInt64() }), HttpStatusCode.OK);
            upload = await Read(await human.PostAsJsonAsync(uploadPath + "/source", new { taskContentHash = hash, expectedVersion = upload.GetProperty("version").GetInt64() }), HttpStatusCode.OK);
            upload.GetProperty("sourceRetention").GetProperty("sha256").GetString().Should().Be(sha256);
            using var readinessDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!(await Read(await agent.GetAsync(uploadPath + "/capabilities?contentHash=" + hash), HttpStatusCode.OK))
                .GetProperty("importAcceptanceSupported").GetBoolean())
                await Task.Delay(100, readinessDeadline.Token);
            var authority = Success(await Rpc(agent, "honua_workspace_get_dataset_import_authority", new { workspaceId, taskId, revision, contentHash = hash }));
            HonuaTelemetry.ConfigureExceptionRecording(exportDetails: true, includeStackTraces: true, maxDetailLength: 16384);
            var submitted = await Rpc(agent, "honua_workspace_submit_dataset_import", new
            {
                workspaceId,
                taskId,
                revision,
                request = new
                {
                    taskContentHash = hash,
                    authorityRevision = authority.GetProperty("authorityRevision").GetInt64(),
                    authorityFingerprint = authority.GetProperty("authorityFingerprint").GetString(),
                    policyContextFingerprint = authority.GetProperty("policyContextFingerprint").GetString()
                }
            });
            if (submitted.TryGetProperty("isError", out var refused) && refused.GetBoolean())
            {
                var retained = await factory.Services.GetRequiredService<IAgentWorkspaceStore>().GetAsync(workspaceId);
                var operation = retained?.DatasetImportPreparedAdmissions.GetValueOrDefault(taskId)?.OperationId;
                var job = operation is null ? null : await factory.Services.GetRequiredService<IExecutionJobStore>().GetAsync(operation);
                var diagnostics = string.Join(Environment.NewLine, submissionActivities.Where(activity => activity.Events.Any(item => item.Name == "exception")).Concat(submissionActivities.TakeLast(4)).Distinct().Select(activity =>
                    string.Join(", ", activity.TagObjects.Select(tag => tag.Key + "=" + tag.Value)) + Environment.NewLine +
                    string.Join(Environment.NewLine, activity.Events.SelectMany(item => item.Tags).Select(tag => tag.Key + "=" + tag.Value))));
                refused.GetBoolean().Should().BeFalse($"retained job status is {job?.Status.ToString() ?? "absent"}; {diagnostics}");
            }
            var accepted = Success(submitted);
            accepted.GetProperty("state").GetString().Should().Be("accepted");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            JsonElement receipt;
            do
            {
                await Task.Delay(100, deadline.Token);
                var observed = await Rpc(agent, "honua_workspace_get_dataset_import_submission", new { workspaceId, taskId, revision, contentHash = hash });
                if (observed.TryGetProperty("isError", out var observationRefused) && observationRefused.GetBoolean())
                {
                    var operation = accepted.GetProperty("operationId").GetString()!;
                    var retainedJob = await factory.Services.GetRequiredService<IExecutionJobStore>().GetAsync(operation);
                    var diagnostics = string.Join(Environment.NewLine, submissionActivities.Where(activity => activity.Events.Any(item => item.Name == "exception")).Concat(submissionActivities.TakeLast(4)).Distinct().Select(activity =>
                        activity.OperationName + ": " + string.Join(", ", activity.TagObjects.Select(tag => tag.Key + "=" + tag.Value)) + Environment.NewLine +
                        string.Join(Environment.NewLine, activity.Events.SelectMany(item => item.Tags).Select(tag => tag.Key + "=" + tag.Value))));
                    observationRefused.GetBoolean().Should().BeFalse($"retained actual job status={retainedJob?.Status}; error={retainedJob?.ErrorMessage}; {diagnostics}");
                }
                receipt = Success(observed);
            } while (receipt.GetProperty("job").GetProperty("status").GetString() is "queued" or "provisioning" or "running"
                || receipt.GetProperty("job").GetProperty("outcomePending").GetBoolean());
            var finalJob = await factory.Services.GetRequiredService<IExecutionJobStore>()
                .GetAsync(accepted.GetProperty("operationId").GetString()!);
            var workerDiagnostics = string.Join(Environment.NewLine, submissionActivities.Where(activity =>
                    activity.Events.Any(item => item.Name == "exception")).Select(activity => activity.OperationName + ": " +
                    string.Join(Environment.NewLine, activity.Events.SelectMany(item => item.Tags).Select(tag => tag.Key + "=" + tag.Value))));
            receipt.GetProperty("job").GetProperty("status").GetString().Should().Be("succeeded",
                $"{receipt.GetRawText()}; actual job error={finalJob?.ErrorMessage}; {workerDiagnostics}");
            var output = receipt.GetProperty("job").GetProperty("output");
            output.GetProperty("featureCount").GetInt64().Should().Be(2);
            output.GetProperty("schema").GetString().Should().Be("public");
            var physical = output.GetProperty("physicalTableName").GetString()!;
            physical.Should().Be(ImportPhysicalTableName.Canonical(table));
            using var quote = new NpgsqlCommandBuilder();
            await using var sql = new NpgsqlConnection(connection);
            await sql.OpenAsync();
            await using var verify = sql.CreateCommand();
            verify.CommandText = $"SELECT count(*), string_agg(properties->>'name', ',' ORDER BY properties->>'name'), sum((properties->>'value')::int), bool_and(ST_SRID(geometry) = 4326), string_agg(ST_AsGeoJSON(geometry), ';' ORDER BY properties->>'name') FROM public.{quote.QuoteIdentifier(physical)}";
            await using var reader = await verify.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(2);
            reader.GetString(1).Should().Be("alpha,beta");
            reader.GetInt64(2).Should().Be(14);
            reader.GetBoolean(3).Should().BeTrue();
            reader.GetString(4).Should().Be("""{"type":"Point","coordinates":[-157.86,21.31]};{"type":"Point","coordinates":[-158,22]}""");
        }
        finally
        {
            HonuaTelemetry.ConfigureExceptionRecording(exportDetails: false, includeStackTraces: false);
            if (database is not null) await postgres.DropDatabaseAsync(database);
            await postgres.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Observe existing provider log exceptions without replacing production services.
    // The normal telemetry sanitizer and opt-in detail policy still own their export.
    private sealed class ImportFailureDiagnosticLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new DiagnosticLogger();
        public void Dispose() { }

        private sealed class DiagnosticLogger : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (exception is null) return;
                using var activity = HonuaTelemetry.StartActivity("test.import.logged-failure");
                HonuaTelemetry.RecordException(activity, exception);
            }
        }
    }

    private static async Task<JsonElement> Rpc(HttpClient client, string name, object arguments)
    {
        var envelope = await Read(await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method = "tools/call",
            @params = new { name, arguments }
        }), HttpStatusCode.OK);
        envelope.TryGetProperty("error", out _).Should().BeFalse(envelope.GetRawText());
        return envelope.GetProperty("result");
    }

    private static JsonElement Success(JsonElement result)
    {
        if (result.TryGetProperty("isError", out var error)) error.GetBoolean().Should().BeFalse(result.GetRawText());
        return result.GetProperty("structuredContent");
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(status, "the native HTTP operation must succeed");
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.Clone();
        }
    }
}
