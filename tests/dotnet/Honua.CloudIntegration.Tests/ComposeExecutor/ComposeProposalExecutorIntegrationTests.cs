// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Hosting.ComposeExecutor;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests.ComposeExecutor;

/// <summary>
/// Proves an approved proposal can converge an installed Compose project and roll it back across
/// migration 142. The honua service is a local Python stand-in, not the published server image:
/// it applies the real 140 and 142 scripts and serves a customer readback. PostGIS is the real
/// <c>postgis/postgis:16-3.4</c> image. The test fails when Docker is missing.
/// </summary>
public sealed class ComposeProposalExecutorIntegrationTests
{
    private const string PostgisImage = "postgis/postgis:16-3.4";
    private readonly ITestOutputHelper _output;

    public ComposeProposalExecutorIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [IntegrationTest(Timeout = 900000)]
    public async Task ApprovedProposal_ConvergesAcrossMigration142_AndRollbackRestoresTheBackup()
    {
        var docker = await RunAsync("docker", ["info"], TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        if (docker.Code != 0)
        {
            Assert.Fail("Docker is required for the Compose proposal executor integration test. " + Tail(docker.Stderr));
        }

        var repo = FindRepoRoot();
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var root = Path.Combine(Path.GetTempPath(), "honua-g44", suffix);
        var projectName = "honua-g44-" + suffix[..8];
        var otherName = "honua-g44-other-" + suffix[..8];
        var projectDir = Path.Combine(root, "project");
        var otherDir = Path.Combine(root, "other");
        var backupDir = Path.Combine(root, "backups");
        var socketPath = Path.Combine(root, "executor.sock");
        var rc1Tag = "honua-g44-rc1-" + suffix[..8] + ":test";
        var candidateTag = "honua-g44-candidate-" + suffix[..8] + ":test";
        var port = FreePort();
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "init"));
        Directory.CreateDirectory(otherDir);
        Directory.CreateDirectory(backupDir);
        var admissionSql = await File.ReadAllTextAsync(Path.Combine(repo, "src", "Honua.Server", "Migrations", "140_CreateAiExecutionAdmission.sql")).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "init", "01-init.sql"),
            InitSql + "\n" + admissionSql.Replace("$HonuaSchema$", "public", StringComparison.Ordinal)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "compose.yaml"), ComposeYaml(projectName, rc1Tag, port)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(otherDir, "compose.yaml"), OtherYaml(otherName)).ConfigureAwait(false);

        using var cancel = new CancellationTokenSource();
        Task<int>? serve = null;
        try
        {
            await BuildFixtureAsync(repo, rc1Tag, "rc1", "140").ConfigureAwait(false);
            await BuildFixtureAsync(repo, candidateTag, "candidate", "142").ConfigureAwait(false);
            var rc1Id = (await DockerAsync(["image", "inspect", "--format", "{{.Id}}", rc1Tag]).ConfigureAwait(false)).Trim();
            var candidateId = (await DockerAsync(["image", "inspect", "--format", "{{.Id}}", candidateTag]).ConfigureAwait(false)).Trim();
            Assert.Matches("^sha256:[a-f0-9]{64}$", rc1Id);
            Assert.Matches("^sha256:[a-f0-9]{64}$", candidateId);

            await ComposeAsync(projectDir, projectName, ["up", "-d", "--pull", "never", "--wait", "--wait-timeout", "180"]).ConfigureAwait(false);
            await ComposeAsync(otherDir, otherName, ["up", "-d", "--pull", "never"]).ConfigureAwait(false);
            await WaitForCustomerAsync(port, "schema=140").ConfigureAwait(false);

            var options = new ComposeExecutorOptions
            {
                SocketPath = socketPath,
                ProjectDirectory = projectDir,
                ProjectName = projectName,
                ComposeFile = "compose.yaml",
                ServiceName = "honua",
                PostgresService = "postgres",
                DatabaseName = "honua",
                DatabaseUser = "honua",
                BackupDirectory = backupDir,
                CustomerProbeUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/customer",
                HealthProbeUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/healthz/ready",
                ReleaseImage = candidateId,
                Revisions = [new ComposeImageSchemaOption { Image = rc1Id, MaxSchema = 140 }],
                ProbeTimeoutSeconds = 10,
                ComposeWaitTimeoutSeconds = 180
            };
            serve = ComposeExecutorCommand.ServeAsync(options, cancel.Token);
            await WaitForSocketAsync(socketPath).ConfigureAwait(false);

            var honuaId = await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false);
            var otherId = await ServiceContainerIdAsync(otherDir, otherName, "sleeper").ConfigureAwait(false);
            var running = (await DockerAsync(["inspect", "--format", "{{.Image}}", honuaId]).ConfigureAwait(false)).Trim();
            Assert.Equal(rc1Id, running);

            var scope = await SendRawAsync(socketPath, "{\"op\":\"status\",\"project\":\"other\"}").ConfigureAwait(false);
            Assert.Equal(ComposeExecutorCodes.ScopeViolation, scope.Code);
            Assert.False(scope.Ok);
            Assert.Equal(honuaId, await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false));
            Assert.Equal(otherId, await ServiceContainerIdAsync(otherDir, otherName, "sleeper").ConfigureAwait(false));

            var client = new ComposeSocketClient(Options.Create(options), NullLogger<ComposeSocketClient>.Instance);
            var ready = await client.ExecuteAsync(new ComposeExecutorRequest { Op = "status", RequestId = "ready" }).ConfigureAwait(false);
            if (!ready.Ok || !ready.ReadbackComplete)
            {
                await DumpAsync(projectDir, projectName).ConfigureAwait(false);
                Assert.Fail("Compose readback was incomplete before approval: " + ready.Code + " " + ready.Message);
            }

            var backend = new ComposeDeployBackend(client, Options.Create(options), NullLogger<ComposeDeployBackend>.Instance);
            var store = new MemoryWorkflows();
            var service = new DeployWorkflowService(
                new MemoryTargets(Target(candidateId)),
                [store],
                [backend],
                new NotRequiredApproval(),
                NullLogger<DeployWorkflowService>.Instance);

            var created = await service.CreateAsync(
                "compose-local",
                candidateId,
                rc1Id,
                "agent",
                "converge",
                "compose-it-converge",
                "corr",
                OperationPriority.Normal,
                submitImmediately: true,
                parameterOverrides: null).ConfigureAwait(false);
            Assert.NotNull(created);
            Assert.Equal(WorkflowOperationStatus.AwaitingApproval, created.Status);
            Assert.Empty(created.BlockingReasons);
            Assert.Equal(honuaId, await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false));

            var same = await Assert.ThrowsAsync<ResourceConflictException>(() =>
                service.SubmitAsync(created.OperationId, "agent", "self")).ConfigureAwait(false);
            Assert.Contains("Separation of duties", same.Message, StringComparison.Ordinal);
            Assert.Equal(rc1Id, (await DockerAsync(["inspect", "--format", "{{.Image}}", honuaId]).ConfigureAwait(false)).Trim());

            var converged = await service.SubmitAsync(created.OperationId, "approver", "approved").ConfigureAwait(false);
            Assert.NotNull(converged);
            if (converged.Status != WorkflowOperationStatus.Reconciling)
            {
                await DumpAsync(projectDir, projectName).ConfigureAwait(false);
                Assert.Fail("Convergence did not reconcile: " + converged.Status + " " + converged.CurrentPhase);
            }

            Assert.Equal(rc1Id, converged.Deploy!.CurrentRevision);
            Assert.Equal(candidateId, converged.Deploy.DesiredRevision);
            await WaitForCustomerAsync(port, "schema=142").ConfigureAwait(false);
            var convergedId = await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false);
            Assert.Equal(candidateId, (await DockerAsync(["inspect", "--format", "{{.Image}}", convergedId]).ConfigureAwait(false)).Trim());
            Assert.Equal("142", (await PsqlAsync(projectDir, projectName, SchemaSql).ConfigureAwait(false)).Trim());
            Assert.Contains("ai_financial_invocations", await PsqlAsync(projectDir, projectName, "SELECT to_regclass('public.ai_financial_invocations')").ConfigureAwait(false), StringComparison.Ordinal);
            Assert.Contains("kept", await CustomerAsync(port).ConfigureAwait(false), StringComparison.Ordinal);

            var restartServices = new ServiceCollection();
            restartServices.AddLogging();
            restartServices.AddSingleton<IComposeExecutorClient>(client);
            using var provider = restartServices.BuildServiceProvider();
            var applier = new OpsActionAdminConfigChangeApplier(
                provider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                NullLogger<OpsActionAdminConfigChangeApplier>.Instance);
            var configRefusal = await Assert.ThrowsAsync<OpsActionException>(() =>
                applier.ApplyAsync("{\"action\":\"compose.apply_config\"}")).ConfigureAwait(false);
            Assert.Equal(ComposeExecutorCodes.ConfigUnsupported, configRefusal.Message);
            Assert.Equal(convergedId, await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false));

            var restartChange = await applier.ApplyAsync("{\"action\":\"compose.restart_service\"}").ConfigureAwait(false);
            Assert.False(string.IsNullOrWhiteSpace(restartChange));
            var restartedId = await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false);
            Assert.Equal(candidateId, (await DockerAsync(["inspect", "--format", "{{.Image}}", restartedId]).ConfigureAwait(false)).Trim());

            var manifestPath = NewestManifest(backupDir);
            var manifestText = await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(manifestText, ComposeExecutorJsonContext.Default.ComposeBackupManifest);
            Assert.NotNull(manifest);
            Assert.Equal(rc1Id, manifest.Image);
            Assert.Equal(140, manifest.SchemaVersion);
            var auditBeforePreview = await File.ReadAllTextAsync(Path.Combine(backupDir, "audit.jsonl")).ConfigureAwait(false);
            var preview = await client.ExecuteAsync(new ComposeExecutorRequest
            {
                Op = "plan-rollback",
                Image = rc1Id,
                RequestId = "preview"
            }).ConfigureAwait(false);
            Assert.True(preview.Ok && preview.ReadbackComplete, "Rollback preview must verify the selected backup.");
            Assert.Equal(manifest.BackupId, preview.BackupId);
            Assert.Equal(restartedId, await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false));
            Assert.Equal("142", (await PsqlAsync(projectDir, projectName, SchemaSql).ConfigureAwait(false)).Trim());
            Assert.Equal(auditBeforePreview, await File.ReadAllTextAsync(Path.Combine(backupDir, "audit.jsonl")).ConfigureAwait(false));
            var marker = "\"sha256\":\"" + manifest.Sha256 + "\"";
            var tampered = manifestText.Replace(marker, "\"sha256\":\"" + new string('0', 64) + "\"", StringComparison.Ordinal);
            Assert.NotEqual(manifestText, tampered);
            await File.WriteAllTextAsync(manifestPath, tampered).ConfigureAwait(false);

            var badPreview = await client.ExecuteAsync(new ComposeExecutorRequest
            {
                Op = "plan-rollback",
                Image = rc1Id,
                RequestId = "preview-tampered"
            }).ConfigureAwait(false);
            Assert.False(badPreview.Ok);
            Assert.Equal(PreMigrationRestoreCodes.BackupChecksumMismatch, badPreview.Code);
            var bad = await SubmitRollbackAsync(service, rc1Id, candidateId, "compose-it-bad-backup").ConfigureAwait(false);
            Assert.Equal(WorkflowOperationStatus.Failed, bad.Status);
            Assert.Contains(PreMigrationRestoreCodes.BackupChecksumMismatch, bad.CurrentPhase, StringComparison.Ordinal);
            Assert.Equal(restartedId, await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false));
            Assert.Equal("142", (await PsqlAsync(projectDir, projectName, SchemaSql).ConfigureAwait(false)).Trim());

            await File.WriteAllTextAsync(manifestPath, manifestText).ConfigureAwait(false);
            var rolled = await SubmitRollbackAsync(service, rc1Id, candidateId, "compose-it-rollback").ConfigureAwait(false);
            if (rolled.Status != WorkflowOperationStatus.Reconciling)
            {
                await DumpAsync(projectDir, projectName).ConfigureAwait(false);
                Assert.Fail("Rollback did not reconcile: " + rolled.Status + " " + rolled.CurrentPhase);
            }

            await WaitForCustomerAsync(port, "schema=140").ConfigureAwait(false);
            var restoredId = await ServiceContainerIdAsync(projectDir, projectName, "honua").ConfigureAwait(false);
            Assert.Equal(rc1Id, (await DockerAsync(["inspect", "--format", "{{.Image}}", restoredId]).ConfigureAwait(false)).Trim());
            Assert.Equal("140", (await PsqlAsync(projectDir, projectName, SchemaSql).ConfigureAwait(false)).Trim());
            Assert.True(string.IsNullOrWhiteSpace(await PsqlAsync(projectDir, projectName, "SELECT to_regclass('public.ai_financial_invocations')").ConfigureAwait(false)));
            Assert.Contains("kept", await CustomerAsync(port).ConfigureAwait(false), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(await PsqlAsync(projectDir, projectName, "SELECT PostGIS_Version()").ConfigureAwait(false)));
            Assert.Equal(otherId, await ServiceContainerIdAsync(otherDir, otherName, "sleeper").ConfigureAwait(false));
            var audit = await File.ReadAllTextAsync(Path.Combine(backupDir, "audit.jsonl")).ConfigureAwait(false);
            Assert.Contains("\"op\":\"converge\"", audit, StringComparison.Ordinal);
            Assert.Contains("\"op\":\"rollback\"", audit, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _output.WriteLine(ex.ToString());
            try
            {
                await DumpAsync(projectDir, projectName).ConfigureAwait(false);
            }
            catch (Exception dumpEx) when (dumpEx is IOException or InvalidOperationException)
            {
                _output.WriteLine(dumpEx.Message);
            }

            throw;
        }
        finally
        {
            await cancel.CancelAsync().ConfigureAwait(false);
            if (serve != null)
            {
                try
                {
                    await serve.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _output.WriteLine(ex.GetType().Name + ": " + ex.Message);
                }
            }

            await BestEffortDownAsync(projectDir, projectName).ConfigureAwait(false);
            await BestEffortDownAsync(otherDir, otherName).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException ex)
            {
                _output.WriteLine(ex.Message);
            }
        }
    }

    private async Task<WorkflowOperationRecord> SubmitRollbackAsync(
        DeployWorkflowService service,
        string priorImage,
        string candidateImage,
        string idempotencyKey)
    {
        var created = await service.CreateAsync(
            "compose-local",
            priorImage,
            candidateImage,
            "agent",
            "rollback",
            idempotencyKey,
            "corr",
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null).ConfigureAwait(false);
        Assert.NotNull(created);
        Assert.Equal(WorkflowOperationStatus.AwaitingApproval, created.Status);
        var submitted = await service.SubmitAsync(created.OperationId, "approver", "approved").ConfigureAwait(false);
        Assert.NotNull(submitted);
        return submitted;
    }

    private async Task BuildFixtureAsync(string repo, string tag, string role, string maxSchema)
    {
        var context = Path.Combine(Path.GetTempPath(), "honua-g44-build-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(context, "migrations"));
        var source = Path.Combine(repo, "tests", "dotnet", "Honua.CloudIntegration.Tests", "ComposeExecutor");
        File.Copy(Path.Combine(source, "Dockerfile"), Path.Combine(context, "Dockerfile"));
        File.Copy(Path.Combine(source, "entrypoint.py"), Path.Combine(context, "entrypoint.py"));
        foreach (var name in new[] { "140_CreateAiExecutionAdmission.sql", "142_CreateAiFinancialAccounting.sql" })
        {
            File.Copy(
                Path.Combine(repo, "src", "Honua.Server", "Migrations", name),
                Path.Combine(context, "migrations", name));
        }

        try
        {
            await DockerAsync(
            [
                "build",
                "--build-arg", "HONUA_FIXTURE_ROLE=" + role,
                "--build-arg", "HONUA_MAX_SCHEMA=" + maxSchema,
                "--label", "org.honua.max-schema=" + (role == "rc1" ? string.Empty : maxSchema),
                "-t", tag,
                context
            ], TimeSpan.FromMinutes(8)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(context, recursive: true);
        }
    }

    private async Task WaitForCustomerAsync(int port, string expected)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddMinutes(2);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var body = await http.GetStringAsync(new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/customer")).ConfigureAwait(false);
                last = body;
                if (body.Contains("kept", StringComparison.Ordinal) && body.Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex.GetType().Name;
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        Assert.Fail("Customer readback did not reach " + expected + ". Last observation: " + last);
    }

    private static async Task<string> CustomerAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await http.GetStringAsync(new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/customer")).ConfigureAwait(false);
    }

    private static async Task WaitForSocketAsync(string socketPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(socketPath))
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.Fail("The Compose executor socket was not created.");
    }

    private static async Task<ComposeExecutorResponse> SendRawAsync(string socketPath, string json)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(payload).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(one).ConfigureAwait(false);
            if (read == 0 || one[0] == (byte)'\n')
            {
                break;
            }

            buffer.WriteByte(one[0]);
        }

        var line = Encoding.UTF8.GetString(buffer.ToArray());
        return JsonSerializer.Deserialize(line, ComposeExecutorJsonContext.Default.ComposeExecutorResponse)
            ?? throw new InvalidOperationException("The executor returned an empty response.");
    }

    private async Task<string> PsqlAsync(string projectDir, string projectName, string sql)
    {
        var args = ComposePrefix(projectDir, projectName).ToList();
        args.AddRange(["exec", "-T", "--", "postgres", "psql", "-U", "honua", "-d", "honua", "-tA", "-v", "ON_ERROR_STOP=1", "-c", sql]);
        return await DockerAsync(args).ConfigureAwait(false);
    }

    private async Task<string> ServiceContainerIdAsync(string projectDir, string projectName, string service)
    {
        var args = ComposePrefix(projectDir, projectName).ToList();
        args.AddRange(["ps", "-q", service]);
        var id = (await DockerAsync(args).ConfigureAwait(false)).Trim();
        Assert.False(string.IsNullOrWhiteSpace(id), "Compose service " + service + " has no container in " + projectName + ".");
        return id;
    }

    private Task<string> ComposeAsync(string projectDir, string projectName, IReadOnlyList<string> tail)
    {
        var args = ComposePrefix(projectDir, projectName).ToList();
        args.AddRange(tail);
        return DockerAsync(args, TimeSpan.FromMinutes(5));
    }

    private static IEnumerable<string> ComposePrefix(string projectDir, string projectName)
    {
        yield return "compose";
        yield return "--project-directory";
        yield return projectDir;
        yield return "--project-name";
        yield return projectName;
        yield return "-f";
        yield return Path.Combine(projectDir, "compose.yaml");
        var over = Path.Combine(projectDir, "compose.executor.image.yaml");
        if (File.Exists(over))
        {
            yield return "-f";
            yield return over;
        }
    }

    private async Task DumpAsync(string projectDir, string projectName)
    {
        if (!Directory.Exists(projectDir))
        {
            return;
        }

        var args = ComposePrefix(projectDir, projectName).ToList();
        args.AddRange(["logs", "--no-color", "--tail", "80"]);
        var logs = await RunAsync("docker", args, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        _output.WriteLine(logs.Stdout);
        _output.WriteLine(logs.Stderr);
    }

    private async Task BestEffortDownAsync(string projectDir, string projectName)
    {
        if (!File.Exists(Path.Combine(projectDir, "compose.yaml")))
        {
            return;
        }

        try
        {
            var args = ComposePrefix(projectDir, projectName).ToList();
            args.AddRange(["down", "-v", "--remove-orphans"]);
            var result = await RunAsync("docker", args, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            if (result.Code != 0)
            {
                _output.WriteLine(result.Stderr);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _output.WriteLine(ex.Message);
        }
    }

    private async Task<string> DockerAsync(IReadOnlyList<string> args, TimeSpan? timeout = null)
    {
        var result = await RunAsync("docker", args, timeout ?? TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        if (result.Code != 0)
        {
            _output.WriteLine(result.Stdout);
            _output.WriteLine(result.Stderr);
            Assert.Fail("docker " + string.Join(' ', args.Take(4)) + " failed: " + Tail(result.Stderr));
        }

        return result.Stdout;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string file,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start " + file + ".");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string NewestManifest(string backupDir)
    {
        string? newestPath = null;
        DateTimeOffset newest = DateTimeOffset.MinValue;
        foreach (var directory in Directory.EnumerateDirectories(backupDir))
        {
            var name = Path.GetFileName(directory);
            if (name == null || !name.StartsWith("backup-", StringComparison.Ordinal) || name.Length != "backup-".Length + 32)
            {
                continue;
            }

            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path))
            {
                continue;
            }

            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                ComposeExecutorJsonContext.Default.ComposeBackupManifest);
            if (manifest != null && (newestPath == null || manifest.CreatedAt >= newest))
            {
                newest = manifest.CreatedAt;
                newestPath = path;
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(newestPath), "Convergence did not write a backup manifest.");
        return newestPath!;
    }

    private static DeployTargetDefinition Target(string releaseImage)
        => new()
        {
            TargetId = "compose-local",
            TargetKind = DeployTargetKind.LocalCompose,
            Backend = ComposeDeployBackend.AdapterBackendName,
            Environment = "local",
            TargetName = "honua",
            ArtifactReference = releaseImage,
            RequiresApproval = true
        };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var marker = Path.Combine(dir.FullName, "src", "Honua.Server", "Migrations", "142_CreateAiFinancialAccounting.sql");
            if (File.Exists(marker))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find src/Honua.Server/Migrations from the test output directory.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 1200 ? trimmed : trimmed[^1200..];
    }

    private static string ComposeYaml(string project, string image, int port)
        => $"""
            name: {project}
            services:
              postgres:
                image: {PostgisImage}
                environment:
                  POSTGRES_USER: honua
                  POSTGRES_PASSWORD: honua
                  POSTGRES_DB: honua
                volumes:
                  - pgdata:/var/lib/postgresql/data
                  - ./init:/docker-entrypoint-initdb.d:ro
                healthcheck:
                  test: ["CMD-SHELL", "grep -qx postgres /proc/1/comm && pg_isready -U honua -d honua && psql -U honua -d honua -tAc \"SELECT marker FROM public.customer_marker WHERE id = 1\" | grep -qx kept"]
                  interval: 2s
                  timeout: 5s
                  retries: 40
                  start_period: 10s
              honua:
                image: {image}
                environment:
                  HONUA_PGHOST: postgres
                  HONUA_PGUSER: honua
                  HONUA_PGDATABASE: honua
                  PGPASSWORD: honua
                ports:
                  - "127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}:8080"
                depends_on:
                  postgres:
                    condition: service_healthy
                healthcheck:
                  test:
                    - CMD
                    - python
                    - -c
                    - "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/healthz/ready', timeout=2)"
                  interval: 2s
                  timeout: 5s
                  retries: 40
                  start_period: 5s
            volumes:
              pgdata:
            """;

    private static string OtherYaml(string project)
        => $"""
            name: {project}
            services:
              sleeper:
                image: python:3.12-alpine
                command: ["sleep", "3600"]
            """;

    private const string SchemaSql =
        "SELECT COALESCE(MAX((regexp_match(scriptname, '(^|[./])0*([0-9]+)_'))[2]::int), 0) FROM public.schema_versions";

    private const string InitSql =
        """
        CREATE EXTENSION IF NOT EXISTS postgis;
        CREATE TABLE public.customer_marker (
            id integer PRIMARY KEY,
            marker text NOT NULL
        );
        INSERT INTO public.customer_marker (id, marker) VALUES (1, 'kept');
        CREATE TABLE public.schema_versions (
            schemaversionsid serial PRIMARY KEY,
            scriptname varchar(255) NOT NULL,
            applied timestamp NOT NULL
        );
        INSERT INTO public.schema_versions (scriptname, applied) VALUES ('Honua.Server.Migrations.140_CreateAiExecutionAdmission.sql', now());
        CREATE TABLE public.schema_version_reader_compatibility (
            script_name varchar(255) PRIMARY KEY,
            declaration_format integer NOT NULL,
            classification text NOT NULL,
            script_sha256 text NOT NULL,
            min_reader_schema integer NOT NULL,
            script_text text NOT NULL,
            recorded_at timestamptz NOT NULL DEFAULT now()
        );
        INSERT INTO public.schema_version_reader_compatibility
            (script_name, declaration_format, classification, script_sha256, min_reader_schema, script_text)
        VALUES ('Honua.Server.Migrations.140_CreateAiExecutionAdmission.sql', 1, 'expand', repeat('cd', 32), 140, 'fixture');
        """;

    private sealed class NotRequiredApproval : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
            => ApprovalRequirement.NotRequired();
    }

    private sealed class MemoryTargets(DeployTargetDefinition target) : IDeployTargetRegistry
    {
        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTargetDefinition?>(target);
    }

    private sealed class MemoryWorkflows : IWorkflowOperationStore
    {
        private readonly Dictionary<string, WorkflowOperationRecord> _items = new(StringComparer.Ordinal);

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.TryAdd(operation.OperationId, operation));

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.TryGetValue(operationId, out var item) ? item : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _items[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (!_items.TryGetValue(operation.OperationId, out var current) || current.Version != operation.Version)
            {
                return Task.FromResult(false);
            }

            _items[operation.OperationId] = operation with { Version = operation.Version + 1 };
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(_items.Values.ToArray());

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
