// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
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

    public List<string> AllowedEnvironmentTags { get; set; } = ["cert", "demo", "staging", "prod", "production"];

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

internal interface IRestoredSchemaProbe
{
    Task<SchemaJournalSnapshot> ProbeAsync(string endpoint, CancellationToken cancellationToken);
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
        var (decision, backup) = await assessment.DecideAsync(payload, cancellationToken).ConfigureAwait(false);
        return await execution.ExecuteAsync(payload, request, starter, decision, backup, cancellationToken).ConfigureAwait(false);
    }

    public Task<RestoreCompletion> ResumeCloudAsync(DeployExecutionPayload payload, OperationGatewayRequest request,
        DurableRestoreWorker.CheckpointStarter starter, string instanceId, string resourceId, int expectedSchema, DateTimeOffset submittedAt, CancellationToken cancellationToken)
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
    public async Task<(RestoreDecision Decision, VerifiedPreMigrationBackup? Backup)> DecideAsync(
        DeployExecutionPayload payload,
        CancellationToken cancellationToken)
    {
        var target = controlPlane.CurrentValue.DeployTargets
            .FirstOrDefault(candidate => string.Equals(candidate.TargetId, payload.TargetId, StringComparison.Ordinal));
        if (target is null)
        {
            return (Refuse(PreMigrationRestoreCodes.TargetUnknown, "The deploy target is not in the server catalog."), null);
        }

        var options = restoreOptions.Value;
        var journalSnapshot = await journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        var priorMax = revisions.MaxSchema(payload.DesiredRevision);
        var (backup, refusal) = await VerifyBackupAsync(payload, options, cancellationToken).ConfigureAwait(false);
        var decision = ReaderIncompatibleRestorePolicy.Decide(new RestoreAssessment
        {
            CompatibilityKnown = journalSnapshot.Known,
            PriorImageMaxSchema = priorMax,
            Applied = journalSnapshot.Applied,
            Backup = backup,
            BackupRefusalCode = refusal,
            RetargetConfigured = secrets.IsConfigured && !string.IsNullOrWhiteSpace(options.ConnectionSecretId),
        });
        if (decision.Kind == RestoreDecisionKind.RestoreBeforeStart
            && target.TargetKind is DeployTargetKind.AwsLambda)
        {
            return (Refuse(PreMigrationRestoreCodes.RetargetUnsupported, "This target has no product restore executor."), null);
        }

        return (decision, backup);
    }

    private async Task<(VerifiedPreMigrationBackup? Backup, string? Refusal)> VerifyBackupAsync(
        DeployExecutionPayload payload,
        RestoreCellOptions options,
        CancellationToken cancellationToken)
    {
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

    private static RestoreDecision Refuse(string code, string message)
        => new() { Kind = RestoreDecisionKind.Refuse, Code = code, Message = message };


}

internal sealed class PreMigrationRestoreExecutionService(
    IRevisionSchemaCatalog revisions,
    IRdsSnapshotRestore rds,
    IConnectionSecretRetarget secrets,
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
            if (starter is DurableRestoreWorker.CheckpointStarter && operationId is not null)
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

        return await ExecuteCloudAsync(payload, request, decision, backup!, starter, cancellationToken).ConfigureAwait(false);
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
        var allowedTags = options.AllowedEnvironmentTags is { Count: > 0 }
            ? options.AllowedEnvironmentTags
            : ["cert", "demo", "staging", "prod", "production"];
        if (!RestoreCellScope.IsBoundedPrefix(options.CellPrefix)
            || string.IsNullOrWhiteSpace(options.EnvironmentTag)
            || !allowedTags.Any(tag => string.Equals(tag, options.EnvironmentTag, StringComparison.OrdinalIgnoreCase))
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
        if (starter is DurableRestoreWorker.CheckpointStarter durableStarter)
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
        DurableRestoreWorker.CheckpointStarter starter, string instanceId, string resourceId, int expectedSchema, DateTimeOffset submittedAt, CancellationToken cancellationToken)
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

        var imageId = starter is DurableRestoreWorker.CheckpointStarter durable
            ? await durable.StartRestoredAsync(request, payload.Serialize(), decision.ExpectedSchema!.Value, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant(), cancellationToken).ConfigureAwait(false)
            : await starter.StartAsync(request, payload.Serialize(), cancellationToken).ConfigureAwait(false);
        if (starter is DurableRestoreWorker.CheckpointStarter && !string.IsNullOrWhiteSpace(imageId))
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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PreMigrationRestoreAuditLine))]
internal sealed partial class PreMigrationRestoreJsonContext : JsonSerializerContext
{
}
