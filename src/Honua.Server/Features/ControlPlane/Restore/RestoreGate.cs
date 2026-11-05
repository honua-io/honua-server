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

// Restore settings are bound from configuration, never from a proposal.
internal sealed class RestoreCellOptions
{
    public const string SectionName = "ControlPlane:Restore";

    public string CellPrefix { get; set; } = string.Empty;

    public string AuditPath { get; set; } = string.Empty;

    public string ConnectionSecretId { get; set; } = string.Empty;

    public string DbSubnetGroupName { get; set; } = string.Empty;

    public List<string> RdsSecurityGroupIds { get; set; } = [];

    public string EnvironmentTag { get; set; } = "cert";

    public string ComposeSocketPath { get; set; } = string.Empty;

    public string ComposeBackupDirectory { get; set; } = string.Empty;

    public string ComposeProject { get; set; } = string.Empty;

    // This URL must return HTTP 200 and a non-empty body after the prior image is up.
    public string CustomerProbeUrl { get; set; } = string.Empty;

    public int RdsWaitSeconds { get; set; } = 1200;

    public int ImageWaitSeconds { get; set; } = DefaultImageWaitSeconds;

    public const int DefaultImageWaitSeconds = 600;

    public List<RevisionSchemaOption> Revisions { get; set; } = [];
}

internal sealed class RevisionSchemaOption
{
    public string Revision { get; set; } = string.Empty;

    public int MaxSchema { get; set; }
}

// An unknown journal means rollback must not start a prior image.
internal sealed record SchemaJournalSnapshot(bool Known, IReadOnlyList<AppliedSchemaMigration> Applied)
{
    public static SchemaJournalSnapshot Unknown { get; } = new(false, []);
}

internal interface ISchemaJournal
{
    Task<SchemaJournalSnapshot> ReadAsync(CancellationToken cancellationToken);
}

internal interface IRevisionSchemaCatalog
{
    int? MaxSchema(string revision);
}

internal interface IRdsSnapshotRestore
{
    Task<RdsSnapshotEvidence?> DescribeAsync(string snapshotId, string cellPrefix, CancellationToken cancellationToken);

    // Restore never reports an endpoint the provider did not mark available.
    Task<RdsNewInstance> RestoreNewAsync(RdsRestoreRequest request, CancellationToken cancellationToken);

    Task<RdsRestoreSubmission> BeginAsync(RdsRestoreRequest request, CancellationToken cancellationToken);

    Task<RdsNewInstance> ObserveAsync(string instanceId, string resourceId, CancellationToken cancellationToken);
}

internal sealed record RdsRestoreRequest(
    string SnapshotId,
    string NewInstanceId,
    string CellPrefix,
    string SubnetGroupName,
    string EnvironmentTag,
    int WaitSeconds,
    IReadOnlyList<string> SecurityGroupIds);

internal sealed record RdsRestoreSubmission(bool Accepted, string? ResourceId, string? Code);

internal sealed record RdsNewInstance(bool Available, string? Endpoint, string? Code);

internal interface IConnectionSecretRetarget
{
    bool IsConfigured { get; }

    Task<bool> PointAtAsync(string secretId, string host, CancellationToken cancellationToken);

    Task<string?> ReadHostAsync(string secretId, CancellationToken cancellationToken);
}

internal interface IComposeRollbackTransport
{
    bool IsConfigured { get; }

    Task<ComposeRollbackResult> RollbackAsync(string image, string? backupId, string? requestId, CancellationToken cancellationToken);
}

internal sealed record ComposeRollbackResult(
    bool Ok,
    string? Code,
    bool ReadbackComplete,
    int? SchemaVersion,
    bool ImageStarted);

internal interface IRestoredSchemaProbe
{
    Task<SchemaJournalSnapshot> ProbeAsync(string? endpoint, CancellationToken cancellationToken);
}

internal interface IServingCustomerProbe
{
    Task<bool> SucceededAsync(CancellationToken cancellationToken);
}

internal interface IPreMigrationRestoreAudit
{
    Task<bool> AppendAsync(PreMigrationRestoreAuditLine line, CancellationToken cancellationToken);

    Task<OperationBackendStatus?> FindAsync(string operationId, CancellationToken cancellationToken);
}

internal sealed record PreMigrationRestoreAuditLine
{
    public DateTimeOffset Time { get; init; }

    public string? Op { get; init; }

    public string? RequestId { get; init; }

    public string? Image { get; init; }

    public string? BackupId { get; init; }

    public bool Ok { get; init; }

    public string? Code { get; init; }

    public int? SchemaVersion { get; init; }

    public string? Cell { get; init; }

    public bool ReadbackComplete { get; init; }

    public string? OperationId { get; init; }
}

internal interface IPriorImageStarter
{
    Task<string?> StartAsync(OperationGatewayRequest request, string? executionPayload, CancellationToken cancellationToken);
}

internal interface IDurablePriorImageStarter : IPriorImageStarter
{
    Task<string?> StartRestoredAsync(OperationGatewayRequest request, string payload, int expectedSchema, string restoredHostHash, CancellationToken cancellationToken);

    Task<bool> CheckpointRdsAsync(string phase, string instanceId, string? resourceId, int expectedSchema, CancellationToken cancellationToken);
}

internal sealed class PreMigrationRestoreCoordinator(
    PreMigrationRestoreAssessmentService assessment,
    PreMigrationRestoreExecutionService execution)
{
    public async Task<RestoreDecision> AssessAsync(DeployExecutionPayload payload, CancellationToken cancellationToken)
        => (await assessment.DecideAsync(payload, cancellationToken).ConfigureAwait(false)).Decision;

    public async Task<RestoreCompletion> ExecuteAsync(
        DeployExecutionPayload payload,
        OperationGatewayRequest request,
        IPriorImageStarter starter,
        CancellationToken cancellationToken)
    {
        var (decision, target, backup) = await assessment.DecideAsync(payload, cancellationToken).ConfigureAwait(false);
        return await execution.ExecuteAsync(payload, request, starter, decision, target, backup, cancellationToken).ConfigureAwait(false);
    }

    public Task<RestoreCompletion> ResumeCloudAsync(DeployExecutionPayload payload, OperationGatewayRequest request,
        IDurablePriorImageStarter starter, string instanceId, string resourceId, int expectedSchema, DateTimeOffset submittedAt, CancellationToken cancellationToken)
        => execution.ResumeCloudAsync(payload, request, starter, instanceId, resourceId, expectedSchema, submittedAt, cancellationToken);
}

internal sealed class PreMigrationRestoreAssessmentService(
    ISchemaJournal journal,
    IRevisionSchemaCatalog revisions,
    IRdsSnapshotRestore rds,
    IConnectionSecretRetarget secrets,
    IOptionsMonitor<ControlPlaneOptions> controlPlane,
    IOptions<RestoreCellOptions> restoreOptions)
{
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
            if (starter is IDurablePriorImageStarter && operationId is not null)
                return new RestoreCompletion { Decision = RestoreDecisionKind.NotRequired, OperationId = operationId };
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
        var observedMax = Max(observed.Applied);
        var matched = observed.Known && observedMax >= decision.ExpectedSchema!.Value && observedMax <= priorMax
            && observed.Applied.All(migration => migration.Number <= priorMax);
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
            || !BackupEvidenceVerifier.InstanceInCell(options.DbSubnetGroupName, options.CellPrefix)
            || options.CellPrefix.TrimEnd('-').Length > 38
            || options.RdsSecurityGroupIds.Count == 0
            || options.RdsSecurityGroupIds.Any(id => !IsSecurityGroupId(id)))
        {
            return await FailAsync(PreMigrationRestoreCodes.ScopeViolation, "Restore is limited to this cell's own prefix, secret, and subnet group.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var instanceId = NewInstanceId(options.CellPrefix, request.IdempotencyKey ?? decision.BackupId!);
        var restoreRequest = new RdsRestoreRequest(backup.BackupId, instanceId, options.CellPrefix, options.DbSubnetGroupName, options.EnvironmentTag, options.RdsWaitSeconds, options.RdsSecurityGroupIds);
        if (starter is IDurablePriorImageStarter durableStarter)
        {
            if (!await durableStarter.CheckpointRdsAsync("submitting-rds", instanceId, null, decision.ExpectedSchema!.Value, cancellationToken).ConfigureAwait(false))
                return new RestoreCompletion { Decision = decision.Kind, Code = PreMigrationRestoreCodes.RestoreIncomplete };
            var submitted = await rds.BeginAsync(restoreRequest, cancellationToken).ConfigureAwait(false);
            if (!submitted.Accepted || string.IsNullOrWhiteSpace(submitted.ResourceId))
                return await FailAsync(submitted.Code ?? PreMigrationRestoreCodes.ProviderFailed, "The restore submission was not confirmed.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
            if (!await durableStarter.CheckpointRdsAsync("awaiting-rds", instanceId, submitted.ResourceId, decision.ExpectedSchema!.Value, cancellationToken).ConfigureAwait(false))
                return new RestoreCompletion { Decision = decision.Kind, Code = PreMigrationRestoreCodes.RestoreIncomplete };
            return new RestoreCompletion { Decision = decision.Kind, Code = "restore-pending", RestoreInvoked = true };
        }
        var restored = await rds.RestoreNewAsync(restoreRequest, cancellationToken).ConfigureAwait(false);
        if (!restored.Available || string.IsNullOrWhiteSpace(restored.Endpoint))
        {
            return await FailAsync(restored.Code ?? PreMigrationRestoreCodes.ProviderFailed, "The new database instance did not become available.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        return await FinishCloudAsync(payload, request, starter, decision, restored.Endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RestoreCompletion> ResumeCloudAsync(DeployExecutionPayload payload, OperationGatewayRequest request,
        IDurablePriorImageStarter starter, string instanceId, string resourceId, int expectedSchema, DateTimeOffset submittedAt, CancellationToken cancellationToken)
    {
        var options = restoreOptions.Value;
        var decision = new RestoreDecision { Kind = RestoreDecisionKind.RestoreBeforeStart, ExpectedSchema = expectedSchema, BackupId = payload.BackupId };
        if (!RestoreCellScope.IsBoundedPrefix(options.CellPrefix) || !BackupEvidenceVerifier.InstanceInCell(instanceId, options.CellPrefix) ||
            string.IsNullOrWhiteSpace(resourceId) || !SecretInCell(options.ConnectionSecretId, options.CellPrefix))
            return await FailAsync(PreMigrationRestoreCodes.ScopeViolation, "The restored instance is outside the configured cell.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        var restored = await rds.ObserveAsync(instanceId, resourceId, cancellationToken).ConfigureAwait(false);
        if (!restored.Available || string.IsNullOrWhiteSpace(restored.Endpoint))
        {
            if (restored.Code == "restore-pending" && DateTimeOffset.UtcNow - submittedAt < TimeSpan.FromSeconds(Math.Clamp(options.RdsWaitSeconds, 1, 3600)))
                return new RestoreCompletion { Decision = decision.Kind, Code = "restore-pending", RestoreInvoked = true };
            return await FailAsync(restored.Code == "restore-pending" ? PreMigrationRestoreCodes.ProviderFailed : restored.Code,
                "The restored instance could not be verified as available.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }
        return await FinishCloudAsync(payload, request, starter, decision, restored.Endpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RestoreCompletion> FinishCloudAsync(DeployExecutionPayload payload, OperationGatewayRequest request,
        IPriorImageStarter starter, RestoreDecision decision, string endpoint, CancellationToken cancellationToken)
    {
        var options = restoreOptions.Value;
        var observed = await probe.ProbeAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var priorMax = revisions.MaxSchema(payload.DesiredRevision) ?? -1;
        var matched = observed.Known && ReaderIncompatibleRestorePolicy.SchemaMatchesBackup(decision.ExpectedSchema!.Value, Max(observed.Applied), observed.Applied, priorMax);
        if (!matched)
        {
            return await FailAsync(PreMigrationRestoreCodes.RestoreIncomplete, "The restored database is not the backup schema. The prior image was not started.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var pointed = await secrets.PointAtAsync(options.ConnectionSecretId, endpoint, cancellationToken).ConfigureAwait(false);
        if (!pointed)
        {
            return await FailAsync(PreMigrationRestoreCodes.ProviderFailed, "The serving connection was not observed on the restored database. The prior image was not started.", payload, request, decision.Kind, cancellationToken).ConfigureAwait(false);
        }

        var imageId = starter is IDurablePriorImageStarter durable
            ? await durable.StartRestoredAsync(request, payload.Serialize(), decision.ExpectedSchema!.Value, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant(), cancellationToken).ConfigureAwait(false)
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
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
        return string.Concat(cellPrefix.TrimEnd('-'), "-restore-", hash);
    }

    private static bool IsSecurityGroupId(string id)
        => id.StartsWith("sg-", StringComparison.Ordinal) && id.Length is 11 or 20
            && id.AsSpan(3).ToString().All(character => char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character));

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

internal static class RestoreCellScope
{
    // A trailing hyphen keeps the prefix from matching a sibling name.
    public static bool IsBoundedPrefix(string cellPrefix)
        => !string.IsNullOrWhiteSpace(cellPrefix)
           && cellPrefix.EndsWith('-')
           && !cellPrefix.Contains('/', StringComparison.Ordinal)
           && !cellPrefix.Contains('\\', StringComparison.Ordinal);

}

// The model cannot choose the Compose directory or the dump name.
internal static class ComposeBackupDirectory
{
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

    // A later absolute path segment must not discard the base directory.
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
        if (!BackupEvidenceVerifier.IsValidBackupId(backupId)) return null;
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

internal sealed record ComposeManifestFile
{
    public string? BackupId { get; init; }

    public string? Project { get; init; }

    public string? Image { get; init; }

    public string? Service { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int SchemaVersion { get; init; }

    public string? Sha256 { get; init; }

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

// The audit line never receives members the sidecar adds, including a customer body.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(ComposeRollbackWireResponse))]
internal sealed partial class ComposeRollbackResponseJsonContext : JsonSerializerContext
{
}

internal sealed record ComposeRollbackWireRequest
{
    public string? Op { get; init; }

    public string? Image { get; init; }

    public string? BackupId { get; init; }

    public string? RequestId { get; init; }
}

internal sealed record ComposeRollbackWireResponse
{
    public string? RequestId { get; init; }

    public bool Ok { get; init; }

    public string? Code { get; init; }

    // Operator text is never written to the audit line.
    public string? Message { get; init; }

    public string? RunningImage { get; init; }

    public bool ReadbackComplete { get; init; }

    public int? SchemaVersion { get; init; }

    public string? BackupId { get; init; }
}
