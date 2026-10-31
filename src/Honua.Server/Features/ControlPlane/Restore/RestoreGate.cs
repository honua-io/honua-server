// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Restore;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>Operator settings for restore across a reader-incompatible migration. Bound from configuration, never from a proposal.</summary>
internal sealed class RestoreCellOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "ControlPlane:Restore";

    /// <summary>Name prefix of the cell's databases, snapshots, and connection secret.</summary>
    public string CellPrefix { get; set; } = string.Empty;

    /// <summary>Append-only audit file. Success is refused when this is empty or the write fails.</summary>
    public string AuditPath { get; set; } = string.Empty;

    /// <summary>Secrets Manager id whose connection string host is repointed after a cloud restore.</summary>
    public string ConnectionSecretId { get; set; } = string.Empty;

    /// <summary>DB subnet group for the new instance. Must start with <see cref="CellPrefix"/>.</summary>
    public string DbSubnetGroupName { get; set; } = string.Empty;

    /// <summary>Environment tag written on the new instance. Only <c>cert</c> or <c>demo</c>.</summary>
    public string EnvironmentTag { get; set; } = "cert";

    /// <summary>Unix socket of the Compose sidecar. Empty on cloud cells.</summary>
    public string ComposeSocketPath { get; set; } = string.Empty;

    /// <summary>Directory of Compose backup manifests. Empty when the sidecar is the only reader.</summary>
    public string ComposeBackupDirectory { get; set; } = string.Empty;

    /// <summary>Compose project name. Matches the manifest project field.</summary>
    public string ComposeProject { get; set; } = string.Empty;

    /// <summary>Customer URL that must return HTTP 200 and a non-empty body after the prior image is up.</summary>
    public string CustomerProbeUrl { get; set; } = string.Empty;

    /// <summary>How long to wait for a new RDS instance, in seconds.</summary>
    public int RdsWaitSeconds { get; set; } = 1200;

    /// <summary>How long to wait for the prior image's workflow to succeed, in seconds.</summary>
    public int ImageWaitSeconds { get; set; } = DefaultImageWaitSeconds;

    /// <summary>Default wait for a cloud rollout to reach a successful terminal state.</summary>
    public const int DefaultImageWaitSeconds = 600;

    /// <summary>Prior image schema numbers, keyed by the exact revision string the proposal names.</summary>
    public List<RevisionSchemaOption> Revisions { get; set; } = [];
}

/// <summary>One revision the operator catalog says an image can read.</summary>
internal sealed class RevisionSchemaOption
{
    /// <summary>Exact revision or image reference.</summary>
    public string Revision { get; set; } = string.Empty;

    /// <summary>Highest migration that image ships.</summary>
    public int MaxSchema { get; set; }
}

/// <summary>Journal the product read. Unknown means rollback must not start a prior image.</summary>
internal sealed record SchemaJournalSnapshot(bool Known, IReadOnlyList<AppliedSchemaMigration> Applied)
{
    /// <summary>Journal could not be read.</summary>
    public static SchemaJournalSnapshot Unknown { get; } = new(false, []);
}

/// <summary>Reads applied migrations and their reader-compatibility declarations.</summary>
internal interface ISchemaJournal
{
    /// <summary>Reads the journal. Failures return <see cref="SchemaJournalSnapshot.Unknown"/>.</summary>
    Task<SchemaJournalSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Highest migration a revision ships, from operator configuration.</summary>
internal interface IRevisionSchemaCatalog
{
    /// <summary>Returns the catalogued max schema, or null when this revision is not listed.</summary>
    int? MaxSchema(string revision);
}

/// <summary>Looks up and restores an RDS snapshot to a new instance.</summary>
internal interface IRdsSnapshotRestore
{
    /// <summary>Describes one snapshot. Null when it does not exist.</summary>
    Task<RdsSnapshotEvidence?> DescribeAsync(string snapshotId, string cellPrefix, CancellationToken cancellationToken);

    /// <summary>Restores a new instance. Never reports an endpoint the provider did not mark available.</summary>
    Task<RdsNewInstance> RestoreNewAsync(RdsRestoreRequest request, CancellationToken cancellationToken);
}

/// <summary>Request to restore one cell snapshot into a new instance.</summary>
internal sealed record RdsRestoreRequest(
    string SnapshotId,
    string NewInstanceId,
    string CellPrefix,
    string SubnetGroupName,
    string EnvironmentTag,
    int WaitSeconds);

/// <summary>Provider result. <see cref="Endpoint"/> is set only when the new instance is available.</summary>
internal sealed record RdsNewInstance(bool Available, string? Endpoint, string? Code);

/// <summary>Repoints the serving connection secret at a new database host and reads the host back.</summary>
internal interface IConnectionSecretRetarget
{
    /// <summary>Whether a secret id is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Updates the host and confirms the value that was read back.</summary>
    Task<bool> PointAtAsync(string secretId, string host, CancellationToken cancellationToken);

    /// <summary>Returns the current host without exposing the connection string.</summary>
    Task<string?> ReadHostAsync(string secretId, CancellationToken cancellationToken);
}

/// <summary>Compose sidecar that restores a dump and starts the prior image only after its own readback.</summary>
internal interface IComposeRollbackTransport
{
    /// <summary>Whether the sidecar socket is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Asks the sidecar to roll back. The server does not talk to Docker.</summary>
    Task<ComposeRollbackResult> RollbackAsync(string image, string? backupId, string? requestId, CancellationToken cancellationToken);
}

/// <summary>Sidecar response. Success still requires the server's own schema probe.</summary>
internal sealed record ComposeRollbackResult(
    bool Ok,
    string? Code,
    bool ReadbackComplete,
    int? SchemaVersion,
    bool ImageStarted);

/// <summary>Reads the schema of a database the product just restored.</summary>
internal interface IRestoredSchemaProbe
{
    /// <summary>Probes the app database when <paramref name="endpoint"/> is null, otherwise that host.</summary>
    Task<SchemaJournalSnapshot> ProbeAsync(string? endpoint, CancellationToken cancellationToken);
}

/// <summary>Customer readback after the prior image is serving.</summary>
internal interface IServingCustomerProbe
{
    /// <summary>True only for HTTP 200 and a non-empty body.</summary>
    Task<bool> SucceededAsync(CancellationToken cancellationToken);
}

/// <summary>Append-only audit. False means the line was not durable.</summary>
internal interface IPreMigrationRestoreAudit
{
    /// <summary>Appends one JSON line and flushes it.</summary>
    Task<bool> AppendAsync(PreMigrationRestoreAuditLine line, CancellationToken cancellationToken);

    /// <summary>Reads the latest audit line for an operation id. Null when the file has none.</summary>
    Task<OperationBackendStatus?> FindAsync(string operationId, CancellationToken cancellationToken);
}

/// <summary>One audit line. No secrets, probe URLs, or customer bodies.</summary>
internal sealed record PreMigrationRestoreAuditLine
{
    /// <summary>UTC time.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Operation name.</summary>
    public string? Op { get; init; }

    /// <summary>Caller correlation id.</summary>
    public string? RequestId { get; init; }

    /// <summary>Revision the rollback named.</summary>
    public string? Image { get; init; }

    /// <summary>Backup id, when one was involved.</summary>
    public string? BackupId { get; init; }

    /// <summary>Whether this line records success.</summary>
    public bool Ok { get; init; }

    /// <summary>Stable code when <see cref="Ok"/> is false.</summary>
    public string? Code { get; init; }

    /// <summary>Schema observed, when known.</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>Cell prefix.</summary>
    public string? Cell { get; init; }

    /// <summary>Whether readback completed.</summary>
    public bool ReadbackComplete { get; init; }

    /// <summary>Durable id returned to the proposal when success is reported.</summary>
    public string? OperationId { get; init; }
}

/// <summary>Starts the prior image only after the restore gate allows it.</summary>
internal interface IPriorImageStarter
{
    /// <summary>
    /// Submits the existing deploy workflow and returns its id only after that workflow
    /// reports success. A failed or unfinished rollout returns null so customer readback does not run.
    /// </summary>
    Task<string?> StartAsync(OperationGatewayRequest request, string? executionPayload, CancellationToken cancellationToken);
}

/// <summary>Persists the restore checkpoint before submitting the prior-image workflow.</summary>
internal interface IDurablePriorImageStarter : IPriorImageStarter
{
    /// <summary>Submits the prior image after recording its deterministic workflow id and verified schema.</summary>
    Task<string?> StartRestoredAsync(OperationGatewayRequest request, string payload, int expectedSchema, string restoredHostHash, CancellationToken cancellationToken);
}

/// <summary>Assesses an approved rollback, then delegates its effects and durable readback.</summary>
internal sealed class PreMigrationRestoreCoordinator(
    PreMigrationRestoreAssessmentService assessment,
    PreMigrationRestoreExecutionService execution)
{
    /// <summary>Assesses a rollback without restoring or starting an image.</summary>
    public async Task<RestoreDecision> AssessAsync(DeployExecutionPayload payload, CancellationToken cancellationToken)
        => (await assessment.DecideAsync(payload, cancellationToken).ConfigureAwait(false)).Decision;

    /// <summary>Executes only the backup and target selected by the shared assessment.</summary>
    public async Task<RestoreCompletion> ExecuteAsync(
        DeployExecutionPayload payload,
        OperationGatewayRequest request,
        IPriorImageStarter starter,
        CancellationToken cancellationToken)
    {
        var (decision, target, backup) = await assessment.DecideAsync(payload, cancellationToken).ConfigureAwait(false);
        return await execution.ExecuteAsync(payload, request, starter, decision, target, backup, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads the configured target, journal and backup without applying a restore.</summary>
internal sealed class PreMigrationRestoreAssessmentService(
    ISchemaJournal journal,
    IRevisionSchemaCatalog revisions,
    IRdsSnapshotRestore rds,
    IConnectionSecretRetarget secrets,
    IOptionsMonitor<ControlPlaneOptions> controlPlane,
    IOptions<RestoreCellOptions> restoreOptions)
{
    /// <summary>Returns the policy decision and the exact target and verified backup it used.</summary>
    public async Task<(RestoreDecision Decision, DeployTargetOptions? Target, VerifiedPreMigrationBackup? Backup)> DecideAsync(
        DeployExecutionPayload payload,
        CancellationToken cancellationToken)
    {
        var target = controlPlane.CurrentValue.DeployTargets
            .FirstOrDefault(candidate => string.Equals(candidate.TargetId, payload.TargetId, StringComparison.Ordinal));
        if (target is null)
        {
            return (Refuse(PreMigrationRestoreCodes.TargetUnknown, "The deploy target is not in the server catalog."), null, null);
        }

        var options = restoreOptions.Value;
        var inPlace = target.TargetKind == DeployTargetKind.SelfHostedRolling;
        var journalSnapshot = await journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        var priorMax = revisions.MaxSchema(payload.DesiredRevision);
        var (backup, refusal) = await VerifyBackupAsync(payload, options, inPlace, cancellationToken).ConfigureAwait(false);
        var decision = ReaderIncompatibleRestorePolicy.Decide(new RestoreAssessment
        {
            CompatibilityKnown = journalSnapshot.Known,
            PriorImageMaxSchema = priorMax,
            Applied = journalSnapshot.Applied,
            Backup = backup,
            BackupRefusalCode = refusal,
            RetargetConfigured = secrets.IsConfigured && !string.IsNullOrWhiteSpace(options.ConnectionSecretId),
            InPlaceDatabase = inPlace,
        });
        if (decision.Kind == RestoreDecisionKind.RestoreBeforeStart
            && target.TargetKind is DeployTargetKind.AzureContainerApps or DeployTargetKind.AzureFunctions or DeployTargetKind.AwsLambda)
        {
            return (Refuse(PreMigrationRestoreCodes.RetargetUnsupported, "This target has no product restore executor."), target, null);
        }

        return (decision, target, backup);
    }

    private async Task<(VerifiedPreMigrationBackup? Backup, string? Refusal)> VerifyBackupAsync(
        DeployExecutionPayload payload,
        RestoreCellOptions options,
        bool inPlace,
        CancellationToken cancellationToken)
    {
        if (inPlace)
        {
            return VerifyComposeBackup(payload, options);
        }

        if (!RestoreCellScope.IsBoundedPrefix(options.CellPrefix))
        {
            return (null, PreMigrationRestoreCodes.ScopeViolation);
        }

        if (string.IsNullOrWhiteSpace(payload.BackupId))
        {
            return (null, PreMigrationRestoreCodes.BackupRequired);
        }

        if (!BackupEvidenceVerifier.SnapshotInCell(payload.BackupId, options.CellPrefix))
        {
            return (null, PreMigrationRestoreCodes.ScopeViolation);
        }

        var described = await rds.DescribeAsync(payload.BackupId, options.CellPrefix, cancellationToken).ConfigureAwait(false);
        if (described is null)
        {
            return (null, PreMigrationRestoreCodes.BackupUnverified);
        }

        var verification = BackupEvidenceVerifier.VerifyRdsSnapshot(described);
        return (verification.Backup, verification.Code);
    }

    private static (VerifiedPreMigrationBackup? Backup, string? Refusal) VerifyComposeBackup(
        DeployExecutionPayload payload,
        RestoreCellOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ComposeBackupDirectory) || string.IsNullOrWhiteSpace(options.ComposeProject))
        {
            return (null, null);
        }

        var evidence = ComposeBackupDirectory.Read(options.ComposeBackupDirectory, options.ComposeProject, payload.DesiredRevision, payload.BackupId);
        if (evidence is null)
        {
            return (null, string.IsNullOrWhiteSpace(payload.BackupId) ? PreMigrationRestoreCodes.BackupRequired : PreMigrationRestoreCodes.BackupUnverified);
        }

        var verification = BackupEvidenceVerifier.VerifyCompose(evidence);
        return (verification.Backup, verification.Code);
    }

    private static RestoreDecision Refuse(string code, string message)
        => new() { Kind = RestoreDecisionKind.Refuse, Code = code, Message = message };


}

/// <summary>Applies the assessed restore and verifies schema, serving state and durable audit.</summary>
internal sealed class PreMigrationRestoreExecutionService(
    IRevisionSchemaCatalog revisions,
    IRdsSnapshotRestore rds,
    IConnectionSecretRetarget secrets,
    IComposeRollbackTransport compose,
    IRestoredSchemaProbe probe,
    IServingCustomerProbe customerProbe,
    IPreMigrationRestoreAudit audit,
    IOptions<RestoreCellOptions> restoreOptions)
{
    /// <summary>
    /// Restores when required, then starts the prior image only after schema readback.
    /// Compose starts inside the sidecar; cloud starts through <paramref name="starter"/>.
    /// </summary>
    public async Task<RestoreCompletion> ExecuteAsync(
        DeployExecutionPayload payload,
        OperationGatewayRequest request,
        IPriorImageStarter starter,
        RestoreDecision decision,
        DeployTargetOptions? target,
        VerifiedPreMigrationBackup? backup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(starter);
        if (decision.Kind == RestoreDecisionKind.Refuse)
        {
            return await FailAsync(decision.Code, decision.Message, payload, request, decision.Kind, cancellationToken)
                .ConfigureAwait(false);
        }

        if (decision.Kind == RestoreDecisionKind.NotRequired)
        {
            var started = await starter.StartAsync(request, payload.Serialize(), cancellationToken).ConfigureAwait(false);
            var operationId = string.IsNullOrWhiteSpace(started) ? null : started;
            var persisted = await WriteAsync(payload, request, operationId, ok: operationId != null, code: operationId == null ? PreMigrationRestoreCodes.ProviderFailed : null, schema: null, readback: false, cancellationToken).ConfigureAwait(false);
            return new RestoreCompletion
            {
                Decision = RestoreDecisionKind.NotRequired,
                Code = operationId == null ? PreMigrationRestoreCodes.ProviderFailed : null,
                Message = operationId == null ? "The prior image did not start." : null,
                OperationId = operationId,
                ImageStarted = operationId != null,
                AuditPersisted = persisted,
            };
        }

        if (target!.TargetKind == DeployTargetKind.SelfHostedRolling)
        {
            return await ExecuteComposeAsync(payload, request, decision, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteCloudAsync(payload, request, decision, backup!, starter, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RestoreCompletion> ExecuteComposeAsync(
        DeployExecutionPayload payload,
        OperationGatewayRequest request,
        RestoreDecision decision,
        CancellationToken cancellationToken)
    {
        if (!compose.IsConfigured)
        {
            return await FailAsync(PreMigrationRestoreCodes.TransportUnavailable, "The Compose restore transport is not configured, so the prior image was not started.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var sidecar = await compose.RollbackAsync(payload.DesiredRevision, decision.BackupId, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (!sidecar.Ok || !sidecar.ImageStarted)
        {
            return await FailAsync(sidecar.Code ?? PreMigrationRestoreCodes.ProviderFailed, "Compose restore did not finish, so success was not reported.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var observed = await probe.ProbeAsync(endpoint: null, cancellationToken).ConfigureAwait(false);
        var priorMax = revisions.MaxSchema(payload.DesiredRevision) ?? -1;
        var matched = observed.Known && ReaderIncompatibleRestorePolicy.SchemaMatchesBackup(decision.ExpectedSchema!.Value, Max(observed.Applied), observed.Applied, priorMax);
        if (!matched || !sidecar.ReadbackComplete)
        {
            return await FailAsync(
                matched ? PreMigrationRestoreCodes.ReadbackIncomplete : PreMigrationRestoreCodes.RestoreIncomplete,
                "Restore did not return the database to the backup schema, so success was not reported.",
                payload,
                request,
                decision.Kind,
                cancellationToken,
                restoreInvoked: true,
                schemaMatched: matched,
                servingPointed: true,
                imageStarted: sidecar.ImageStarted).ConfigureAwait(false);
        }

        var operationId = OperationIdFor(payload, decision.BackupId);
        var persisted = await WriteAsync(payload, request, operationId, ok: true, code: null, schema: decision.ExpectedSchema, readback: true, cancellationToken).ConfigureAwait(false);
        return new RestoreCompletion
        {
            Decision = decision.Kind,
            OperationId = persisted ? operationId : null,
            Code = persisted ? null : PreMigrationRestoreCodes.AuditFailed,
            RestoreInvoked = true,
            SchemaMatched = true,
            ServingPointed = true,
            ImageStarted = true,
            ReadbackComplete = true,
            AuditPersisted = persisted,
        };
    }

    private async Task<RestoreCompletion> ExecuteCloudAsync(
        DeployExecutionPayload payload,
        OperationGatewayRequest request,
        RestoreDecision decision,
        VerifiedPreMigrationBackup backup,
        IPriorImageStarter starter,
        CancellationToken cancellationToken)
    {
        var options = restoreOptions.Value;
        if (!RestoreCellScope.IsBoundedPrefix(options.CellPrefix)
            || options.EnvironmentTag is not ("cert" or "demo")
            || !SecretInCell(options.ConnectionSecretId, options.CellPrefix)
            || !BackupEvidenceVerifier.InstanceInCell(options.DbSubnetGroupName, options.CellPrefix))
        {
            return await FailAsync(PreMigrationRestoreCodes.ScopeViolation, "Restore is limited to this cell's own prefix, secret, and subnet group.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var instanceId = NewInstanceId(options.CellPrefix, request.IdempotencyKey ?? decision.BackupId!);
        var restored = await rds.RestoreNewAsync(
            new RdsRestoreRequest(backup.BackupId, instanceId, options.CellPrefix, options.DbSubnetGroupName, options.EnvironmentTag, options.RdsWaitSeconds),
            cancellationToken).ConfigureAwait(false);
        if (!restored.Available || string.IsNullOrWhiteSpace(restored.Endpoint))
        {
            return await FailAsync(restored.Code ?? PreMigrationRestoreCodes.ProviderFailed, "The new database instance did not become available.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var observed = await probe.ProbeAsync(restored.Endpoint, cancellationToken).ConfigureAwait(false);
        var priorMax = revisions.MaxSchema(payload.DesiredRevision) ?? -1;
        var matched = observed.Known && ReaderIncompatibleRestorePolicy.SchemaMatchesBackup(decision.ExpectedSchema!.Value, Max(observed.Applied), observed.Applied, priorMax);
        if (!matched)
        {
            return await FailAsync(PreMigrationRestoreCodes.RestoreIncomplete, "The restored database is not the backup schema. The prior image was not started.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var pointed = await secrets.PointAtAsync(options.ConnectionSecretId, restored.Endpoint, cancellationToken).ConfigureAwait(false);
        if (!pointed)
        {
            return await FailAsync(PreMigrationRestoreCodes.ProviderFailed, "The serving connection was not observed on the restored database. The prior image was not started.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var imageId = starter is IDurablePriorImageStarter durable
            ? await durable.StartRestoredAsync(request, payload.Serialize(), decision.ExpectedSchema!.Value, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(restored.Endpoint))).ToLowerInvariant(), cancellationToken).ConfigureAwait(false)
            : await starter.StartAsync(request, payload.Serialize(), cancellationToken).ConfigureAwait(false);
        if (starter is IDurablePriorImageStarter && !string.IsNullOrWhiteSpace(imageId))
        {
            // The durable reconciler owns protection-window completion and final readback.
            return new RestoreCompletion { Decision = decision.Kind, OperationId = imageId, RestoreInvoked = true, SchemaMatched = true, ServingPointed = true };
        }
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return await FailAsync(PreMigrationRestoreCodes.ProviderFailed, "The prior image did not start after the database was restored.", payload, request, decision.Kind, cancellationToken, restoreInvoked: true, schemaMatched: true, servingPointed: true).ConfigureAwait(false);
        }

        var readback = await customerProbe.SucceededAsync(cancellationToken).ConfigureAwait(false);
        if (!readback)
        {
            return await FailAsync(PreMigrationRestoreCodes.ReadbackIncomplete, "Customer readback did not complete, so success was not reported.", payload, request, decision.Kind, cancellationToken, restoreInvoked: true, schemaMatched: true, servingPointed: true, imageStarted: true).ConfigureAwait(false);
        }

        var persisted = await WriteAsync(payload, request, imageId, ok: true, code: null, schema: decision.ExpectedSchema, readback: true, cancellationToken).ConfigureAwait(false);
        return new RestoreCompletion
        {
            Decision = decision.Kind,
            OperationId = persisted ? imageId : null,
            Code = persisted ? null : PreMigrationRestoreCodes.AuditFailed,
            RestoreInvoked = true,
            SchemaMatched = true,
            ServingPointed = true,
            ImageStarted = true,
            ReadbackComplete = true,
            AuditPersisted = persisted,
        };
    }

    private async Task<RestoreCompletion> FailAsync(
        string? code,
        string? message,
        DeployExecutionPayload payload,
        OperationGatewayRequest request,
        RestoreDecisionKind kind,
        CancellationToken cancellationToken,
        bool restoreInvoked = false,
        bool schemaMatched = false,
        bool servingPointed = false,
        bool imageStarted = false)
    {
        var persisted = await WriteAsync(payload, request, operationId: null, ok: false, code: code, schema: null, readback: false, cancellationToken).ConfigureAwait(false);
        return new RestoreCompletion
        {
            Decision = kind,
            Code = persisted ? code : PreMigrationRestoreCodes.AuditFailed,
            Message = message,
            RestoreInvoked = restoreInvoked,
            SchemaMatched = schemaMatched,
            ServingPointed = servingPointed,
            ImageStarted = imageStarted,
            ImageStartedBeforeSchemaMatch = imageStarted && !schemaMatched,
            ReadbackComplete = false,
            AuditPersisted = persisted,
        };
    }

    private Task<bool> WriteAsync(
        DeployExecutionPayload payload,
        OperationGatewayRequest request,
        string? operationId,
        bool ok,
        string? code,
        int? schema,
        bool readback,
        CancellationToken cancellationToken)
        => audit.AppendAsync(new PreMigrationRestoreAuditLine
        {
            Time = DateTimeOffset.UtcNow,
            Op = "rollback",
            RequestId = request.CorrelationId,
            Image = payload.DesiredRevision,
            BackupId = payload.BackupId,
            Ok = ok,
            Code = code,
            SchemaVersion = schema,
            Cell = restoreOptions.Value.CellPrefix,
            ReadbackComplete = readback,
            OperationId = operationId,
        }, cancellationToken);

    private static RestoreDecision Refuse(string code, string message)
        => new() { Kind = RestoreDecisionKind.Refuse, Code = code, Message = message };

    private static int? Max(IReadOnlyList<AppliedSchemaMigration> applied)
        => applied.Count == 0 ? null : applied.Max(migration => migration.Number);

    private static string NewInstanceId(string cellPrefix, string material)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..8].ToLowerInvariant();
        var identifier = string.Concat(cellPrefix.TrimEnd('-'), "-restore-", hash);
        return identifier.Length <= 63 ? identifier : identifier[..63].TrimEnd('-');
    }

    private static bool SecretInCell(string secretId, string cellPrefix)
    {
        if (!RestoreCellScope.IsBoundedPrefix(cellPrefix) || string.IsNullOrWhiteSpace(secretId))
        {
            return false;
        }

        var name = secretId;
        const string marker = ":secret:";
        var index = secretId.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            name = secretId[(index + marker.Length)..];
        }

        return name.StartsWith(cellPrefix, StringComparison.Ordinal)
            && !name.Contains('/', StringComparison.Ordinal)
            && !name.Contains('\\', StringComparison.Ordinal);
    }

    private static string OperationIdFor(DeployExecutionPayload payload, string? backupId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.TargetId + "\n" + payload.DesiredRevision + "\n" + backupId)))[..32].ToLowerInvariant();
        return "restore-" + hash;
    }
}

/// <summary>Boundary shared by restore assessment and application.</summary>
internal static class RestoreCellScope
{
    /// <summary>A trailing hyphen keeps the prefix from matching a sibling name such as <c>honua-certrun</c>.</summary>
    public static bool IsBoundedPrefix(string cellPrefix)
        => !string.IsNullOrWhiteSpace(cellPrefix)
           && cellPrefix.EndsWith('-')
           && !cellPrefix.Contains('/', StringComparison.Ordinal)
           && !cellPrefix.Contains('\\', StringComparison.Ordinal);

}

/// <summary>Reads one Compose manifest directory. The model cannot choose the directory or the dump name.</summary>
internal static class ComposeBackupDirectory
{
    /// <summary>Loads one named backup, or the newest manifest that verifies for the image.</summary>
    public static ComposeBackupManifestEvidence? Read(string root, string project, string image, string? backupId)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(backupId))
        {
            return Load(root, project, image, backupId, out _);
        }

        ComposeBackupManifestEvidence? newest = null;
        DateTimeOffset newestStamp = DateTimeOffset.MinValue;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(directory);
            var loaded = Load(root, project, image, id, out var createdAt);
            if (loaded is null || BackupEvidenceVerifier.VerifyCompose(loaded).Backup is null)
            {
                continue;
            }

            var stamp = createdAt > DateTimeOffset.MinValue
                ? createdAt
                : new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero);
            if (newest is null || stamp > newestStamp)
            {
                newest = loaded;
                newestStamp = stamp;
            }
        }

        return newest;
    }

    /// <summary>A later absolute segment must not discard <paramref name="root"/>.</summary>
    private static bool TryChild(string root, string name, string file, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(['/', '\\']) >= 0
            || Path.IsPathRooted(name)
            || file.IndexOfAny(['/', '\\']) >= 0
            || Path.IsPathRooted(file))
        {
            return false;
        }

        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(rootFull, name, file));
        if (!combined.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        path = combined;
        return true;
    }

    private static ComposeBackupManifestEvidence? Load(string root, string project, string image, string backupId, out DateTimeOffset createdAt)
    {
        createdAt = DateTimeOffset.MinValue;
        if (!TryChild(root, backupId, "manifest.json", out var manifestPath)
            || !TryChild(root, backupId, BackupEvidenceVerifier.DumpFileName, out var dumpPath))
        {
            return null;
        }
        if (!File.Exists(manifestPath) || !File.Exists(dumpPath))
        {
            return null;
        }

        ComposeManifestFile? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), PreMigrationRestoreJsonContext.Default.ComposeManifestFile);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null || !string.Equals(manifest.BackupId, backupId, StringComparison.Ordinal))
        {
            return null;
        }

        createdAt = manifest.CreatedAt;

        using var dump = File.OpenRead(dumpPath);
        var hash = Convert.ToHexString(SHA256.HashData(dump)).ToLowerInvariant();
        return new ComposeBackupManifestEvidence
        {
            BackupId = backupId,
            Project = manifest.Project ?? string.Empty,
            ExpectedProject = project,
            Image = manifest.Image ?? string.Empty,
            ExpectedImage = image,
            SchemaVersion = manifest.SchemaVersion,
            Sha256 = manifest.Sha256 ?? string.Empty,
            ComputedSha256 = hash,
            DumpFileName = manifest.DumpFile ?? string.Empty,
        };
    }
}

/// <summary>Manifest JSON the Compose sidecar writes beside <c>database.dump</c>.</summary>
internal sealed record ComposeManifestFile
{
    /// <summary>Backup id.</summary>
    public string? BackupId { get; init; }

    /// <summary>Compose project.</summary>
    public string? Project { get; init; }

    /// <summary>Image id.</summary>
    public string? Image { get; init; }

    /// <summary>Compose service name. Recorded by the sidecar and not used to choose a project.</summary>
    public string? Service { get; init; }

    /// <summary>When the sidecar wrote the backup. Used to choose the newest manifest.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Schema at backup time.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Lowercase SHA-256 of the dump.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Dump file name.</summary>
    public string? DumpFile { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ComposeManifestFile))]
[JsonSerializable(typeof(PreMigrationRestoreAuditLine))]
[JsonSerializable(typeof(ComposeRollbackWireRequest))]
[JsonSerializable(typeof(ComposeRollbackWireResponse))]
internal sealed partial class PreMigrationRestoreJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Response reader that ignores members the sidecar adds, including a customer body.
/// The audit line never receives those members.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(ComposeRollbackWireResponse))]
internal sealed partial class ComposeRollbackResponseJsonContext : JsonSerializerContext
{
}

/// <summary>One rollback line sent to the Compose sidecar. There is no project or command field.</summary>
internal sealed record ComposeRollbackWireRequest
{
    /// <summary>Always <c>rollback</c>.</summary>
    public string? Op { get; init; }

    /// <summary>Prior image reference.</summary>
    public string? Image { get; init; }

    /// <summary>Optional backup id.</summary>
    public string? BackupId { get; init; }

    /// <summary>Correlation id.</summary>
    public string? RequestId { get; init; }
}

/// <summary>One sidecar response line.</summary>
internal sealed record ComposeRollbackWireResponse
{
    /// <summary>Caller correlation id, when the sidecar echoes it.</summary>
    public string? RequestId { get; init; }

    /// <summary>Whether the sidecar finished its own checks.</summary>
    public bool Ok { get; init; }

    /// <summary>Stable code when <see cref="Ok"/> is false.</summary>
    public string? Code { get; init; }

    /// <summary>Operator text. Never written to the audit line.</summary>
    public string? Message { get; init; }

    /// <summary>Image the sidecar observed.</summary>
    public string? RunningImage { get; init; }

    /// <summary>Whether the sidecar's readback completed.</summary>
    public bool ReadbackComplete { get; init; }

    /// <summary>Schema the sidecar observed.</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>Backup the sidecar consumed.</summary>
    public string? BackupId { get; init; }
}
