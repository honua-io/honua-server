// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Core.Features.ControlPlane.Restore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.ControlPlane;

/// <summary>
/// Allowlisted Compose controller. Every mutation uses a fixed <c>docker</c> argument list for the
/// single project in <see cref="ComposeExecutorOptions"/>. A failed checksum, a scope violation, or
/// an incomplete readback never reports success and does not start a different image.
/// </summary>
internal sealed partial class ComposeProjectController : IDisposable
{
    private const string MaxSchemaLabel = "org.honua.max-schema";
    private const string CompatibilityTableSql = "SELECT to_regclass('public.schema_version_reader_compatibility')";
    private const string JournalSql = "SELECT scriptname FROM public.schema_versions ORDER BY scriptname";
    private const string CompatibilityRowsSql =
        "SELECT script_name || E'\\x1f' || declaration_format::text || E'\\x1f' || classification || E'\\x1f' || min_reader_schema::text FROM public.schema_version_reader_compatibility ORDER BY script_name";
    private const string MaintenanceDatabase = "postgres";
    private const int CustomerBodyCapBytes = 4096;

    private static readonly TimeSpan DumpTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    private readonly ComposeExecutorOptions _options;
    private readonly IDockerComposeProcess _docker;
    private readonly ILogger<ComposeProjectController> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a controller. The HTTP client probes only the operator-configured URLs.</summary>
    public ComposeProjectController(
        ComposeExecutorOptions options,
        IDockerComposeProcess docker,
        ILogger<ComposeProjectController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(docker);
        _options = options;
        _docker = docker;
        _logger = logger ?? NullLogger<ComposeProjectController>.Instance;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.ProbeTimeoutSeconds, 1, 120))
        };
    }

    /// <summary>Runs one allowlisted operation. Docker mutations are serialized.</summary>
    public async Task<ComposeExecutorResponse> ExecuteAsync(
        ComposeExecutorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var problem = _options.ValidateForProcess();
            if (problem is { } invalid)
            {
                return ComposeExecutorProtocol.Refuse(request.RequestId, invalid.Code, invalid.Message);
            }

            var response = request.Op switch
            {
                "observe" => await ObserveServicesAsync(request, cancellationToken).ConfigureAwait(false),
                "status" => await StatusAsync(request, cancellationToken).ConfigureAwait(false),
                "converge" => await ConvergeAsync(request, cancellationToken).ConfigureAwait(false),
                "rollback" => await RollbackCoreAsync(request, cancellationToken).ConfigureAwait(false),
                "restart" => await RestartAsync(request, cancellationToken).ConfigureAwait(false),
                _ => ComposeExecutorProtocol.Refuse(
                    request.RequestId,
                    ComposeExecutorCodes.ConfigUnsupported,
                    "The executor op is not allowlisted.")
            };

            if (request.Op is not ("status" or "observe"))
            {
                response = Finish(request, response);
            }

            if (!response.Ok)
            {
                Log.Refused(_logger, request.Op ?? string.Empty, response.Code ?? string.Empty);
            }

            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private async Task<ComposeExecutorResponse> StatusAsync(
        ComposeExecutorRequest request,
        CancellationToken cancellationToken)
    {
        var live = await ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        if (live.ScopeViolation)
        {
            return Refuse(request, ComposeExecutorCodes.ScopeViolation, "A service row belongs to a different Compose project.");
        }

        var running = live.ImageId;
        if (ComposeExecutorRules.IsImmutableImage(request.Image))
        {
            var inspected = await InspectImageAsync(request.Image!, cancellationToken).ConfigureAwait(false);
            if (inspected != null && string.Equals(live.ImageId, inspected.Id, StringComparison.Ordinal))
            {
                running = request.Image;
            }
        }

        var complete = live.IsComplete;
        if (complete && request.RequireAudit && string.Equals(running, request.Image, StringComparison.Ordinal) && !HasSuccessfulAudit(request))
            return Refuse(request, ComposeExecutorCodes.AuditUnrecorded, "The operation has no matching successful audit record.");
        return new ComposeExecutorResponse
        {
            RequestId = request.RequestId,
            Ok = complete,
            Code = complete ? null : ComposeExecutorCodes.ReadbackIncomplete,
            Message = complete ? "Readback completed." : "Readback is incomplete.",
            RunningImage = running,
            ReadbackComplete = complete,
            SchemaVersion = live.SchemaVersion,
            CustomerBody = complete ? live.CustomerBody : null
        };
    }

    private async Task<ComposeExecutorResponse> ConvergeAsync(
        ComposeExecutorRequest request,
        CancellationToken cancellationToken)
    {
        if (!ComposeExecutorRules.IsImmutableImage(request.Image))
        {
            return Refuse(request, ComposeExecutorCodes.ImageNotImmutable, "Converge requires an immutable image digest.");
        }

        if (!string.Equals(request.Image, _options.ReleaseImage, StringComparison.Ordinal))
        {
            return Refuse(request, ComposeExecutorCodes.ScopeViolation, "Converge only accepts the configured release image.");
        }

        var inspected = await InspectImageAsync(request.Image!, cancellationToken).ConfigureAwait(false);
        if (inspected == null)
        {
            return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "The release image is not present on this host.");
        }

        var live = await ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        if (live.ScopeViolation)
        {
            return Refuse(request, ComposeExecutorCodes.ScopeViolation, "A service row belongs to a different Compose project.");
        }

        if (live.IsComplete && string.Equals(live.ImageId, inspected.Id, StringComparison.Ordinal))
        {
            return Success(request, request.Image!, live, backupId: null);
        }

        if (!live.IsComplete || string.IsNullOrWhiteSpace(live.ImageId) || live.SchemaVersion == null)
        {
            return Refuse(
                request,
                ComposeExecutorCodes.ReadbackIncomplete,
                "The service is not ready to converge, so it was not stopped.");
        }

        var stopped = await ComposeAsync(CommandTimeout, ["stop", "--", _options.ServiceName], cancellationToken)
            .ConfigureAwait(false);
        if (stopped.ExitCode != 0)
        {
            return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "The service could not be stopped, so no backup was taken.");
        }

        (string? BackupId, ComposeExecutorResponse? Failure) backup;
        try
        {
            backup = await TakeBackupAsync(live.ImageId, live.SchemaVersion.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await RestartAfterBackupFailureAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException) throw;
            return Refuse(request, ComposeExecutorCodes.BackupFailed, "The backup failed; restarting the prior service was attempted.");
        }
        if (backup.Failure != null)
        {
            await RestartAfterBackupFailureAsync().ConfigureAwait(false);
            return backup.Failure with { RequestId = request.RequestId };
        }

        var started = await StartImageAsync(request.Image!, cancellationToken).ConfigureAwait(false);
        if (started != null)
        {
            return started with { RequestId = request.RequestId, BackupId = backup.BackupId };
        }

        var confirmed = await ConfirmAsync(request, request.Image!, inspected.Id, cancellationToken).ConfigureAwait(false);
        return confirmed with { BackupId = backup.BackupId };
    }

    private async Task RestartAfterBackupFailureAsync()
    {
        try
        {
            await ComposeAsync(CommandTimeout, ["start", "--", _options.ServiceName], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The response remains a failure; a restart exception cannot turn it into success.
            Log.Refused(_logger, "restart", ComposeExecutorCodes.BackupFailed);
        }
    }

    private async Task<ComposeExecutorResponse> RollbackCoreAsync(
        ComposeExecutorRequest request,
        CancellationToken cancellationToken)
    {
        if (!ComposeExecutorRules.IsImmutableImage(request.Image))
        {
            return Refuse(request, ComposeExecutorCodes.ImageNotImmutable, "Rollback requires an immutable image digest.");
        }

        if (string.Equals(request.Image, _options.ReleaseImage, StringComparison.Ordinal))
        {
            return Refuse(request, ComposeExecutorCodes.ConfigUnsupported, "Rollback of the configured release image is not supported.");
        }

        // Registry manifest digests differ from local image configuration IDs stored in backups.
        // Resolve a registry reference before comparing, while retaining the no-Docker fast path
        // for a directly specified local image ID.
        InspectedImage? inspected = null;
        var backupImage = request.Image!;
        if (!backupImage.StartsWith("sha256:", StringComparison.Ordinal))
        {
            inspected = await InspectImageAsync(backupImage, cancellationToken).ConfigureAwait(false);
            if (inspected is null)
                return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "The prior image is not present on this host.");
            backupImage = inspected.Id;
        }
        var located = LocateBackup(backupImage, request.BackupId);
        if (located.Failure != null)
        {
            return located.Failure with { RequestId = request.RequestId };
        }

        var manifest = located.Manifest!;
        inspected ??= await InspectImageAsync(request.Image!, cancellationToken).ConfigureAwait(false);
        if (inspected == null)
        {
            return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "The prior image is not present on this host.");
        }

        var live = await ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        if (live.ScopeViolation)
        {
            return Refuse(request, ComposeExecutorCodes.ScopeViolation, "A service row belongs to a different Compose project.");
        }

        var database = live.CompatibilityKnown
            ? live.Database
            : await ReadDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (!database.CompatibilityKnown)
        {
            return Refuse(
                request,
                ComposeExecutorCodes.ReadbackIncomplete,
                "Reader compatibility is unknown, so the prior image was not started.");
        }

        var priorMax = inspected.MaxSchema ?? -1;
        var applied = ComposeReaderGate.Applied(database.Journal, database.Rows);
        var assessment = new RestoreAssessment
        {
            CompatibilityKnown = database.CompatibilityKnown,
            PriorImageMaxSchema = inspected.MaxSchema,
            Applied = applied,
            Backup = new VerifiedPreMigrationBackup(manifest.BackupId!, manifest.SchemaVersion, ReaderIncompatibleRestorePolicy.ComposeDumpEngine),
            InPlaceDatabase = true
        };
        var decision = ReaderIncompatibleRestorePolicy.Decide(assessment);
        if (decision.Kind == RestoreDecisionKind.Refuse)
        {
            return Refuse(request, decision.Code!, decision.Message!);
        }

        if (decision.Kind == RestoreDecisionKind.NotRequired)
        {
            var expand = ComposeReaderGate.Assess(database.Journal, database.Rows, priorMax);
            var rejected = await ExpandScriptsRejectedAsync(expand.ExpandScriptsToVerify, priorMax, cancellationToken).ConfigureAwait(false);
            if (rejected is null)
            {
                return Refuse(request, PreMigrationRestoreCodes.ReadbackIncomplete, "Reader compatibility could not be verified.");
            }
            if (rejected.Value)
            {
                decision = ReaderIncompatibleRestorePolicy.Decide(assessment with
                {
                    Applied = applied.Select(migration => migration.Number > priorMax ? migration with { Classification = null } : migration).ToArray()
                });
            }
        }
        if (decision.Kind == RestoreDecisionKind.Refuse)
            return Refuse(request, decision.Code!, decision.Message!);
        var restoreRequired = decision.Kind == RestoreDecisionKind.RestoreBeforeStart;
        if (live.IsComplete && string.Equals(live.ImageId, inspected.Id, StringComparison.Ordinal) && !restoreRequired)
        {
            return Success(request, request.Image!, live, manifest.BackupId);
        }

        var stopped = await ComposeAsync(CommandTimeout, ["stop", "--", _options.ServiceName], cancellationToken)
            .ConfigureAwait(false);
        if (stopped.ExitCode != 0)
        {
            return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "The service could not be stopped.");
        }

        if (restoreRequired)
        {
            var restored = await ApplyDumpAsync(manifest, cancellationToken).ConfigureAwait(false);
            if (restored != null)
            {
                return restored with { RequestId = request.RequestId, BackupId = manifest.BackupId };
            }

            var after = await ReadDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var afterAssessment = after.CompatibilityKnown
                ? ComposeReaderGate.Assess(after.Journal, after.Rows, priorMax)
                : new ComposeRestoreAssessment(true, []);
            if (!after.CompatibilityKnown
                || after.SchemaVersion != manifest.SchemaVersion
                || afterAssessment.Required
                || afterAssessment.ExpandScriptsToVerify.Count > 0)
            {
                return Refuse(
                    request,
                    PreMigrationRestoreCodes.RestoreIncomplete,
                    "Restore did not return the database to the backup schema. The service was left stopped.")
                    with
                { BackupId = manifest.BackupId, SchemaVersion = after.SchemaVersion };
            }
        }

        var started = await StartImageAsync(request.Image!, cancellationToken).ConfigureAwait(false);
        if (started != null)
        {
            return started with { RequestId = request.RequestId, BackupId = manifest.BackupId };
        }

        var confirmed = await ConfirmAsync(request, request.Image!, inspected.Id, cancellationToken).ConfigureAwait(false);
        return confirmed with { BackupId = manifest.BackupId };
    }

    private async Task<ComposeExecutorResponse> RestartAsync(
        ComposeExecutorRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Image) || !string.IsNullOrWhiteSpace(request.BackupId))
        {
            return Refuse(request, ComposeExecutorCodes.ConfigUnsupported, "Restart does not accept an image or a backup.");
        }

        var service = request.Service ?? _options.ServiceName;
        if (!ManagedServices().Contains(service, StringComparer.Ordinal))
        {
            return Refuse(request, ComposeExecutorCodes.ScopeViolation, "The service is outside the configured managed stack.");
        }

        var restarted = await ComposeAsync(CommandTimeout, ["restart", "--", service], cancellationToken)
            .ConfigureAwait(false);
        if (restarted.ExitCode != 0)
        {
            return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "The service restart did not complete.");
        }

        // compose restart returns before the healthcheck passes. Poll until the wait budget ends
        // and only then report incomplete readback. A partial restart never returns success.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_options.ComposeWaitTimeoutSeconds);
        while (true)
        {
            if (request.Service is not null)
            {
                var observation = await ObserveServicesAsync(request, cancellationToken).ConfigureAwait(false);
                if (observation.Ok && observation.Services.SingleOrDefault(row => row.Service == service)?.Status == "Healthy")
                    return observation with { ReadbackComplete = true, Message = "The restarted service is healthy." };
                if (DateTimeOffset.UtcNow >= deadline)
                    return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "Readback after restart is incomplete.");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var live = await ReadLiveAsync(cancellationToken).ConfigureAwait(false);
            if (live.ScopeViolation)
            {
                return Refuse(request, ComposeExecutorCodes.ScopeViolation, "A service row belongs to a different Compose project.");
            }

            if (live.IsComplete && !string.IsNullOrWhiteSpace(live.ImageId))
            {
                return Success(request, live.ImageId, live, backupId: null);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "Readback after restart is incomplete.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ComposeExecutorResponse> ConfirmAsync(
        ComposeExecutorRequest request,
        string requestedImage,
        string expectedImageId,
        CancellationToken cancellationToken)
    {
        var live = await ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        if (live.ScopeViolation)
        {
            return Refuse(request, ComposeExecutorCodes.ScopeViolation, "A service row belongs to a different Compose project.");
        }

        if (!live.IsComplete || !string.Equals(live.ImageId, expectedImageId, StringComparison.Ordinal))
        {
            return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "Readback did not confirm the requested image.");
        }

        return Success(request, requestedImage, live, backupId: null);
    }

    private async Task<(string? BackupId, ComposeExecutorResponse? Failure)> TakeBackupAsync(
        string priorImageId,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        var backupId = "backup-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var directory = Path.Combine(Path.GetFullPath(_options.BackupDirectory), backupId);
        Directory.CreateDirectory(directory);
        const string remote = "/tmp/honua-backup.dump";
        var dumped = await ComposeAsync(DumpTimeout, ["exec", "-T", "--", _options.PostgresService, "pg_dump", "-U", _options.DatabaseUser, "-d", _options.DatabaseName, "-Fc", "-f", remote], cancellationToken)
            .ConfigureAwait(false);
        if (dumped.ExitCode != 0)
        {
            return (null, Refuse(null, ComposeExecutorCodes.BackupFailed, "pg_dump did not complete."));
        }

        var hostDump = Path.Combine(directory, ComposeExecutorProtocol.DumpFileName);
        var copied = await ComposeAsync(DumpTimeout, ["cp", _options.PostgresService + ":" + remote, hostDump], cancellationToken)
            .ConfigureAwait(false);
        if (copied.ExitCode != 0 || !File.Exists(hostDump))
        {
            return (null, Refuse(null, ComposeExecutorCodes.BackupFailed, "The backup could not be copied out of Postgres."));
        }

        var hash = await HashFileAsync(hostDump, cancellationToken).ConfigureAwait(false);
        var manifest = new ComposeBackupManifest
        {
            BackupId = backupId,
            Project = _options.ProjectName,
            Service = _options.ServiceName,
            Image = priorImageId,
            SchemaVersion = schemaVersion,
            Sha256 = hash,
            CreatedAt = DateTimeOffset.UtcNow,
            DumpFile = ComposeExecutorProtocol.DumpFileName
        };
        var json = JsonSerializer.Serialize(manifest, ComposeExecutorJsonContext.Default.ComposeBackupManifest);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var temporary = manifestPath + ".tmp";
        await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, manifestPath, overwrite: true);
        return (backupId, null);
    }

    private async Task<ComposeExecutorResponse?> ApplyDumpAsync(
        ComposeBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var dump = DumpPath(Path.GetFullPath(_options.BackupDirectory), manifest.BackupId!);
        var copied = await ComposeAsync(DumpTimeout, ["cp", dump, _options.PostgresService + ":/tmp/honua-backup.dump"], cancellationToken)
            .ConfigureAwait(false);
        if (copied.ExitCode != 0)
        {
            return Refuse(null, PreMigrationRestoreCodes.RestoreIncomplete, "The backup could not be copied into Postgres.");
        }

        var dropped = await ComposeAsync(CommandTimeout, ["exec", "-T", "--", _options.PostgresService, "dropdb", "-U", _options.DatabaseUser, "--force", "--if-exists", _options.DatabaseName], cancellationToken)
            .ConfigureAwait(false);
        if (dropped.ExitCode != 0)
        {
            return Refuse(null, PreMigrationRestoreCodes.RestoreIncomplete, "The database could not be dropped before restore.");
        }

        var restored = await ComposeAsync(DumpTimeout, ["exec", "-T", "--", _options.PostgresService, "pg_restore", "-U", _options.DatabaseUser, "-d", MaintenanceDatabase, "--create", "--exit-on-error", "/tmp/honua-backup.dump"], cancellationToken)
            .ConfigureAwait(false);
        if (restored.ExitCode != 0)
        {
            return Refuse(null, PreMigrationRestoreCodes.RestoreIncomplete, "pg_restore did not complete.");
        }

        return null;
    }

    internal async Task<ComposeExecutorResponse?> StartImageAsync(string image, CancellationToken cancellationToken)
    {
        var overridePath = Path.Combine(Path.GetFullPath(_options.ProjectDirectory), ComposeExecutorProtocol.OverrideFileName);
        var yaml = "services:\n  " + _options.ServiceName + ":\n    image: \"" + image + "\"\n";
        var temporary = overridePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, yaml, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, overridePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller attaches the verified backup so the workflow can recover.
            return Refuse(null, ComposeExecutorCodes.ReadbackIncomplete, "The image override could not be written.");
        }
        var wait = _options.ComposeWaitTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        var up = await ComposeAsync(TimeSpan.FromSeconds(_options.ComposeWaitTimeoutSeconds + 60), ["up", "-d", "--no-deps", "--pull", "never", "--wait", "--wait-timeout", wait, "--", _options.ServiceName], cancellationToken)
            .ConfigureAwait(false);
        if (up.ExitCode != 0)
        {
            return Refuse(null, ComposeExecutorCodes.ReadbackIncomplete, "docker compose up did not become healthy.");
        }

        return null;
    }

    private BackupHit LocateBackup(string image, string? backupId)
    {
        var root = Path.GetFullPath(_options.BackupDirectory);
        if (!string.IsNullOrWhiteSpace(backupId))
        {
            if (!ComposeExecutorRules.BackupId().IsMatch(backupId))
            {
                return BackupHit.Fail(Refuse(null, ComposeExecutorCodes.ScopeViolation, "The backup id is not allowlisted."));
            }

            return LoadBackup(root, backupId, image, named: true);
        }

        ComposeBackupManifest? newest = null;
        string? newestChecksumFailure = null;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!ComposeExecutorRules.BackupId().IsMatch(name ?? string.Empty))
            {
                continue;
            }

            var loaded = LoadBackup(root, name!, image, named: false);
            if (loaded.Manifest == null)
            {
                continue;
            }

            if (newest == null || loaded.Manifest.CreatedAt >= newest.CreatedAt)
            {
                newest = loaded.Manifest;
                newestChecksumFailure = loaded.ChecksumFailure;
            }
        }

        if (newest == null)
        {
            return BackupHit.Fail(Refuse(null, PreMigrationRestoreCodes.BackupImageMismatch, "No backup manifest matches the requested image."));
        }

        if (newestChecksumFailure != null)
        {
            return BackupHit.Fail(Refuse(null, newestChecksumFailure, "The newest matching backup failed verification."));
        }

        return BackupHit.Ok(newest);
    }

    private BackupHit LoadBackup(string root, string backupId, string image, bool named)
    {
        var manifestPath = Path.Combine(root, backupId, "manifest.json");
        if (!IsUnder(root, manifestPath) || !File.Exists(manifestPath))
        {
            return named
                ? BackupHit.Fail(Refuse(null, PreMigrationRestoreCodes.BackupImageMismatch, "The backup manifest is missing."))
                : default;
        }

        ComposeBackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath),
                ComposeExecutorJsonContext.Default.ComposeBackupManifest);
        }
        catch (JsonException)
        {
            return named
                ? BackupHit.Fail(Refuse(null, PreMigrationRestoreCodes.BackupChecksumMismatch, "The backup manifest is not valid."))
                : default;
        }

        if (manifest == null || !string.Equals(manifest.BackupId, backupId, StringComparison.Ordinal))
        {
            return named
                ? BackupHit.Fail(Refuse(null, ComposeExecutorCodes.ScopeViolation, "The backup manifest id does not match its directory."))
                : default;
        }

        if (!string.Equals(manifest.Service, _options.ServiceName, StringComparison.Ordinal))
        {
            return named ? BackupHit.Fail(Refuse(null, PreMigrationRestoreCodes.ScopeViolation, "The backup belongs to another service.")) : default;
        }
        if (!string.Equals(manifest.Project, _options.ProjectName, StringComparison.Ordinal))
            return named ? BackupHit.Fail(Refuse(null, PreMigrationRestoreCodes.ScopeViolation, "The backup belongs to another project.")) : default;
        if (!string.Equals(manifest.Image, image, StringComparison.Ordinal))
            return named ? BackupHit.Fail(Refuse(null, PreMigrationRestoreCodes.BackupImageMismatch, "The backup belongs to another image.")) : default;
        var dump = DumpPath(root, backupId);
        if (!IsUnder(root, dump) || !File.Exists(dump))
        {
            return named
                ? BackupHit.Fail(Refuse(null, PreMigrationRestoreCodes.BackupUnverified, "The backup dump is missing."))
                : BackupHit.Checksum(manifest, PreMigrationRestoreCodes.BackupUnverified);
        }
        var verification = BackupEvidenceVerifier.VerifyCompose(new ComposeBackupManifestEvidence
        {
            BackupId = backupId,
            Project = manifest.Project ?? string.Empty,
            ExpectedProject = _options.ProjectName,
            Image = manifest.Image ?? string.Empty,
            ExpectedImage = image,
            SchemaVersion = manifest.SchemaVersion,
            Sha256 = manifest.Sha256 ?? string.Empty,
            ComputedSha256 = HashFile(dump),
            DumpFileName = manifest.DumpFile ?? string.Empty
        });
        if (verification.Backup is null)
        {
            return named
                ? BackupHit.Fail(Refuse(null, verification.Code!, "The backup evidence did not verify."))
                : verification.Code == PreMigrationRestoreCodes.BackupChecksumMismatch
                    ? BackupHit.Checksum(manifest, verification.Code!)
                    : default;
        }

        return BackupHit.Ok(manifest);
    }

    private async Task<LiveView> ReadLiveAsync(CancellationToken cancellationToken)
    {
        var ps = await ComposeAsync(CommandTimeout, ["ps", "--all", "--format", "json"], cancellationToken)
            .ConfigureAwait(false);
        if (ps.ExitCode != 0)
        {
            return LiveView.Incomplete();
        }

        List<ComposePsRow> rows;
        try
        {
            rows = ParseComposePs(ps.Stdout).ToList();
        }
        catch (JsonException)
        {
            return LiveView.Incomplete();
        }

        string? containerId = null;
        var found = false;
        var running = false;
        foreach (var row in rows)
        {
            if (!string.Equals(row.Service, _options.ServiceName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(row.Project, _options.ProjectName, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(row.Project))
                {
                    return LiveView.Scope();
                }

                continue;
            }

            found = true;
            containerId = row.Id;
            running = string.Equals(row.State, "running", StringComparison.OrdinalIgnoreCase);
        }

        if (!found || string.IsNullOrWhiteSpace(containerId))
        {
            return LiveView.Incomplete();
        }

        var image = await DockerAsync(CommandTimeout, ["inspect", "--format", "{{.Image}}", containerId], cancellationToken)
            .ConfigureAwait(false);
        var imageId = image.ExitCode == 0 ? image.Stdout.Trim() : null;
        var database = running ? await ReadDatabaseAsync(cancellationToken).ConfigureAwait(false) : DatabaseView.Unknown();
        var health = false;
        var customerOk = false;
        string? customerBody = null;
        if (running)
        {
            health = await ProbeOkAsync(_options.HealthProbeUrl, captureBody: false, cancellationToken).ConfigureAwait(false) is { Ok: true };
            var customer = await ProbeOkAsync(_options.CustomerProbeUrl, captureBody: true, cancellationToken).ConfigureAwait(false);
            customerOk = customer.Ok;
            customerBody = customer.Body;
        }

        return new LiveView(
            ScopeViolation: false,
            Found: true,
            ContainerId: containerId,
            ImageId: imageId,
            Running: running,
            Database: database,
            HealthOk: health,
            CustomerOk: customerOk,
            CustomerBody: customerOk ? customerBody : null);
    }

    private async Task<DatabaseView> ReadDatabaseAsync(CancellationToken cancellationToken)
    {
        var table = await PsqlAsync(_options.DatabaseName, CompatibilityTableSql, cancellationToken).ConfigureAwait(false);
        if (table == null)
        {
            return DatabaseView.Unknown();
        }

        var journalText = await PsqlAsync(_options.DatabaseName, JournalSql, cancellationToken).ConfigureAwait(false);
        if (journalText == null)
        {
            return DatabaseView.Unknown();
        }

        var journal = journalText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var schema = ComposeReaderGate.SchemaVersion(journal);
        if (schema is null)
        {
            return DatabaseView.Unknown();
        }

        if (string.IsNullOrWhiteSpace(table))
        {
            return new DatabaseView(false, schema, journal, []);
        }

        var scripts = await PsqlAsync(_options.DatabaseName, CompatibilityRowsSql, cancellationToken).ConfigureAwait(false);
        if (scripts == null || !TryParseCompatibilityRows(scripts, out var rows))
        {
            return DatabaseView.Unknown();
        }

        return new DatabaseView(true, schema, journal, rows);
    }

    private async Task<bool?> ExpandScriptsRejectedAsync(
        IReadOnlyList<string> scripts,
        int priorMax,
        CancellationToken cancellationToken)
    {
        foreach (var script in scripts)
        {
            if (!ComposeReaderGate.IsSafeScriptName(script))
            {
                return null;
            }

            var sql = "SELECT declaration_format::text || E'\\x1f' || classification || E'\\x1f' || script_sha256 || E'\\x1f' || min_reader_schema::text || E'\\x1f' || replace(encode(convert_to(script_text, 'UTF8'), 'base64'), E'\\n', '') FROM public.schema_version_reader_compatibility WHERE script_name = '"
                + script.Replace("'", "''", StringComparison.Ordinal)
                + "'";
            var text = await PsqlAsync(_options.DatabaseName, sql, cancellationToken).ConfigureAwait(false);
            if (text == null || !TryParseDeclaration(script, text.Trim(), out var declaration))
            {
                return null;
            }

            if (!MigrationReaderCompatibility.Verify(declaration, script, priorMax).Accepted)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseCompatibilityRows(string scripts, out List<ComposeCompatibilityRow> rows)
    {
        rows = [];
        foreach (var line in scripts.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\u001f');
            if (parts.Length != 4
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var format)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minReader))
            {
                return false;
            }

            rows.Add(new ComposeCompatibilityRow(parts[0], format, parts[2], minReader));
        }

        return true;
    }

    private static bool TryParseDeclaration(string scriptName, string line, out MigrationReaderCompatibilityDeclaration declaration)
    {
        declaration = null!;
        var parts = line.Split('\u001f');
        if (parts.Length != 5
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var format)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minReader))
        {
            return false;
        }

        byte[] text;
        try
        {
            text = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        declaration = new MigrationReaderCompatibilityDeclaration
        {
            ScriptName = scriptName,
            DeclarationFormat = format,
            Classification = parts[1],
            ScriptSha256 = parts[2],
            MinReaderSchema = minReader,
            ScriptText = Encoding.UTF8.GetString(text)
        };
        return true;
    }

    private async Task<InspectedImage?> InspectImageAsync(string image, CancellationToken cancellationToken)
    {
        var id = await DockerAsync(CommandTimeout, ["image", "inspect", "--format", "{{.Id}}", image], cancellationToken)
            .ConfigureAwait(false);
        if (id.ExitCode != 0)
        {
            return null;
        }

        var trimmed = id.Stdout.Trim();
        if (!ComposeExecutorRules.IsImmutableImage(trimmed))
        {
            return null;
        }

        var label = await DockerAsync(CommandTimeout, ["image", "inspect", "--format", "{{index .Config.Labels \"" + MaxSchemaLabel + "\"}}", image], cancellationToken)
            .ConfigureAwait(false);
        int? maxSchema = null;
        if (label.ExitCode == 0
            && int.TryParse(label.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0)
        {
            maxSchema = parsed;
        }

        maxSchema ??= _options.Revisions.FirstOrDefault(revision =>
            string.Equals(revision.Image, image, StringComparison.Ordinal)
            || string.Equals(revision.Image, trimmed, StringComparison.Ordinal))?.MaxSchema;
        if (maxSchema is null)
        {
            foreach (var revision in _options.Revisions)
            {
                if (!ComposeExecutorRules.IsImmutableImage(revision.Image)) continue;
                var catalogImage = await DockerAsync(CommandTimeout,
                    ["image", "inspect", "--format", "{{.Id}}", revision.Image], cancellationToken).ConfigureAwait(false);
                if (catalogImage.ExitCode == 0 && string.Equals(catalogImage.Stdout.Trim(), trimmed, StringComparison.Ordinal))
                {
                    maxSchema = revision.MaxSchema;
                    break;
                }
            }
        }
        return new InspectedImage(trimmed, maxSchema);
    }

    private async Task<(bool Ok, string? Body)> ProbeOkAsync(
        string url,
        bool captureBody,
        CancellationToken cancellationToken,
        int? maximumTimeoutSeconds = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = Math.Min(_options.ProbeTimeoutSeconds, maximumTimeoutSeconds ?? _options.ProbeTimeoutSeconds);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (false, null);
            }

            if (!captureBody)
            {
                return (true, null);
            }

            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (false, null);
            }

            return (true, Cap(text));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (false, null);
        }
    }

    private async Task<string?> PsqlAsync(string database, string sql, CancellationToken cancellationToken)
    {
        var result = await ComposeAsync(CommandTimeout, ["exec", "-T", "--", _options.PostgresService, "psql", "-U", _options.DatabaseUser, "-d", database, "-tA", "-v", "ON_ERROR_STOP=1", "-c", sql], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Stdout : null;
    }

    private Task<DockerProcessResult> ComposeAsync(
        TimeSpan timeout,
        string[] tail,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "compose",
            "--project-directory",
            Path.GetFullPath(_options.ProjectDirectory),
            "--project-name",
            _options.ProjectName
        };
        foreach (var composeFile in _options.ComposeFilePaths())
        {
            args.Add("-f");
            args.Add(composeFile);
        }
        var over = Path.Combine(Path.GetFullPath(_options.ProjectDirectory), ComposeExecutorProtocol.OverrideFileName);
        if (File.Exists(over))
        {
            args.Add("-f");
            args.Add(over);
        }

        args.AddRange(tail);
        return _docker.RunAsync(args, timeout, cancellationToken);
    }

    private Task<DockerProcessResult> DockerAsync(
        TimeSpan timeout,
        string[] arguments,
        CancellationToken cancellationToken)
        => _docker.RunAsync(arguments, timeout, cancellationToken);

    private ComposeExecutorResponse Finish(ComposeExecutorRequest request, ComposeExecutorResponse response)
    {
        var recorded = TryAudit(new ComposeAuditRecord
        {
            Time = DateTimeOffset.UtcNow,
            Op = request.Op,
            RequestId = request.RequestId,
            Image = request.Image,
            BackupId = response.BackupId,
            Ok = response.Ok,
            Code = response.Code,
            SchemaVersion = response.SchemaVersion,
            Project = _options.ProjectName
        });
        if (response.Ok && !recorded)
        {
            return response with
            {
                Ok = false,
                ReadbackComplete = false,
                Code = request.Op == "rollback" ? PreMigrationRestoreCodes.AuditFailed : ComposeExecutorCodes.AuditUnrecorded,
                Message = "The operation was not recorded in the audit log.",
                CustomerBody = null
            };
        }

        return response;
    }

    private bool HasSuccessfulAudit(ComposeExecutorRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Image)) return false;
        try
        {
            var path = Path.Combine(Path.GetFullPath(_options.BackupDirectory), "audit.jsonl");
            foreach (var line in File.ReadLines(path))
            {
                var entry = JsonSerializer.Deserialize(line, ComposeExecutorJsonContext.Default.ComposeAuditRecord);
                if (entry is { Ok: true } && entry.Op is "converge" or "rollback" &&
                    string.Equals(entry.RequestId, request.RequestId, StringComparison.Ordinal) &&
                    string.Equals(entry.Image, request.Image, StringComparison.Ordinal) &&
                    string.Equals(entry.Project, _options.ProjectName, StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        return false;
    }

    private bool TryAudit(ComposeAuditRecord record)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(_options.BackupDirectory), "audit.jsonl");
            var line = JsonSerializer.Serialize(record, ComposeExecutorJsonContext.Default.ComposeAuditRecord) + "\n";
            File.AppendAllText(path, line);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ComposeExecutorResponse Success(
        ComposeExecutorRequest request,
        string runningImage,
        LiveView live,
        string? backupId)
        => new()
        {
            RequestId = request.RequestId,
            Ok = true,
            ReadbackComplete = true,
            RunningImage = runningImage,
            SchemaVersion = live.SchemaVersion,
            CustomerBody = live.CustomerBody,
            BackupId = backupId,
            Message = "Readback completed for the requested image."
        };

    private static ComposeExecutorResponse Refuse(ComposeExecutorRequest? request, string code, string message)
        => ComposeExecutorProtocol.Refuse(request?.RequestId, code, message);

    private static string DumpPath(string root, string backupId)
        => Path.Combine(root, backupId, ComposeExecutorProtocol.DumpFileName);

    private static bool IsUnder(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.Ordinal);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Cap(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return bytes.Length <= CustomerBodyCapBytes
            ? body
            : Encoding.UTF8.GetString(bytes, 0, CustomerBodyCapBytes);
    }

    private static IEnumerable<ComposePsRow> ParseComposePs(string stdout)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        if (trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(trimmed);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                yield return ReadRow(element);
            }

            yield break;
        }

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            yield return ReadRow(document.RootElement);
        }
    }

    private static ComposePsRow ReadRow(JsonElement element)
        => new(
            Service: ReadString(element, "Service"),
            Project: ReadString(element, "Project"),
            Id: ReadString(element, "ID").Length > 0 ? ReadString(element, "ID") : ReadString(element, "Id"),
            State: ReadString(element, "State"));

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private sealed record InspectedImage(string Id, int? MaxSchema);

    private readonly record struct DatabaseView(
        bool CompatibilityKnown,
        int? SchemaVersion,
        IReadOnlyList<string> Journal,
        IReadOnlyList<ComposeCompatibilityRow> Rows)
    {
        public static DatabaseView Unknown() => new(false, null, [], []);
    }

    private readonly record struct LiveView(
        bool ScopeViolation,
        bool Found,
        string? ContainerId,
        string? ImageId,
        bool Running,
        DatabaseView Database,
        bool HealthOk,
        bool CustomerOk,
        string? CustomerBody)
    {
        public int? SchemaVersion => Database.SchemaVersion;

        public bool CompatibilityKnown => Database.CompatibilityKnown;

        public bool IsComplete
            => Found
               && Running
               && !ScopeViolation
               && !string.IsNullOrWhiteSpace(ImageId)
               && Database.CompatibilityKnown
               && Database.SchemaVersion != null
               && HealthOk
               && CustomerOk;

        public static LiveView Incomplete() => new(false, false, null, null, false, DatabaseView.Unknown(), false, false, null);

        public static LiveView Scope() => new(true, false, null, null, false, DatabaseView.Unknown(), false, false, null);
    }

    private readonly record struct ComposePsRow(string Service, string Project, string Id, string State);

    private readonly record struct BackupHit(ComposeBackupManifest? Manifest, ComposeExecutorResponse? Failure, string? ChecksumFailure)
    {
        public static BackupHit Ok(ComposeBackupManifest manifest) => new(manifest, null, null);

        public static BackupHit Fail(ComposeExecutorResponse failure) => new(null, failure, null);

        public static BackupHit Checksum(ComposeBackupManifest manifest, string code) => new(manifest, null, code);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9531, Level = LogLevel.Warning, Message = "Compose executor op {Op} refused with {Code}.")]
        public static partial void Refused(ILogger logger, string op, string code);
    }
}
