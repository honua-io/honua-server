// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Domain;

namespace Honua.ControlPlane;

internal sealed partial class MetadataReleaseControlService(
    IEnumerable<IWorkflowOperationStore> workflowStores)
{
    private const int MaxPackageIdLength = 128;
    private const int MaxTargetEnvironmentLength = 128;
    private const int MaxResourceSemanticIdLength = 256;
    private const int MaxFieldNameLength = 128;
    private const int MaxFieldTypeLength = 64;
    private const int MaxScriptIdLength = 160;
    private const int MaxWorkloadIdLength = 128;
    private const int MaxReasonLength = 512;
    private const int MaxIdempotencyKeyLength = 128;
    private const int MaxCorrelationIdLength = 128;
    private const int MaxDataPopulateFieldsCount = 64;

    private static readonly Regex UnsafeOperationIdCharacters = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IWorkflowOperationStore? _workflowStore = workflowStores.FirstOrDefault();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldIdentifierPattern();

    public async Task<WorkflowOperationRecord> CreateAsync(
        MetadataReleaseExecutionPlan plan,
        string? requestedBy,
        string? reason,
        string? idempotencyKey,
        string? correlationId,
        string? rootProvisioningOperationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
        ValidateAuditFields(reason, idempotencyKey, correlationId);
        var root = RootProvisioningOperationIds.Normalize(rootProvisioningOperationId);
        EnsureDurableStoreConfigured();

        var now = DateTimeOffset.UtcNow;
        var operationId = CreateOperationId(plan.PackageId, idempotencyKey);
        var requestFingerprint = CreateRequestFingerprint(plan, root);

        var release = new MetadataReleaseContext
        {
            PackageId = plan.PackageId,
            DesiredRevision = plan.PackageId,
            TargetEnvironment = plan.TargetEnvironment,
            CurrentStage = MetadataReleaseStage.Preflight,
            ExecutionPlan = plan,
            RootProvisioningOperationId = root,
        };

        var operation = new WorkflowOperationRecord
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.MetadataRelease,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Submitted additive metadata-release lifecycle; awaiting preflight.",
            RootProvisioningOperationId = root,
            Audit = new OperationAuditInfo
            {
                RequestedBy = requestedBy,
                Reason = reason,
                IdempotencyKey = idempotencyKey,
                CorrelationId = correlationId,
                RequestFingerprint = requestFingerprint
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = $"{plan.TargetEnvironment}:metadata-release:{plan.ResourceSemanticId}",
                RequiresExclusiveLease = true
            },
            MetadataRelease = release
        };

        var created = await _workflowStore!.TryCreateAsync(operation, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            var existing = await _workflowStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                // An idempotency key deterministically maps to one operation id. Guard against a
                // replay that reuses the key for a different package/resource/field or a different
                // provisioning root: returning the prior operation in that case would report success
                // while silently dropping the new request.
                RootProvisioningOperationIds.RejectRelabel(existing.RootProvisioningOperationId, root);
                EnsureMatchingIdempotentRequest(existing, requestFingerprint);
                return existing;
            }

            throw new InvalidOperationException("Failed to durably record the metadata release operation.");
        }

        return operation;
    }

    private static void EnsureMatchingIdempotentRequest(WorkflowOperationRecord existing, string requestFingerprint)
    {
        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint) &&
            string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new ResourceConflictException(
            $"Idempotency key '{existing.Audit.IdempotencyKey}' is already associated with a different metadata-release request.");
    }

    private static string CreateRequestFingerprint(MetadataReleaseExecutionPlan plan, string? rootProvisioningOperationId)
    {
        var builder = new StringBuilder();
        builder.Append("package=").Append(plan.PackageId.Trim()).Append('\n');
        builder.Append("environment=").Append(plan.TargetEnvironment.Trim()).Append('\n');
        builder.Append("resource=").Append(plan.ResourceSemanticId.Trim()).Append('\n');
        builder.Append("field=").Append(plan.NewFieldName.Trim()).Append('\n');
        builder.Append("dataPopulate=").Append(plan.DataPopulateWorkloadId?.Trim() ?? string.Empty).Append('\n');
        builder.Append("scriptId=").Append(plan.Script.ScriptId.Trim()).Append('\n');
        builder.Append("scriptReversible=").Append(plan.Script.Reversible ? "1" : "0").Append('\n');
        if (rootProvisioningOperationId is not null)
        {
            builder.Append("root=").Append(rootProvisioningOperationId).Append('\n');
        }

        foreach (var op in plan.Script.ForwardOperations)
        {
            builder.Append("op=")
                .Append(op.Kind)
                .Append(':')
                .Append(op.ResourceSemanticId.Trim())
                .Append(':')
                .Append(op.FieldName.Trim())
                .Append(':')
                .Append(op.FieldType?.Trim() ?? string.Empty)
                .Append(':')
                .Append(op.Nullable ? "1" : "0")
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public async Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();
        return await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateOperationId(string packageId, string? idempotencyKey)
    {
        var seed = string.IsNullOrWhiteSpace(idempotencyKey) ? packageId : idempotencyKey;
        var slug = UnsafeOperationIdCharacters.Replace(seed.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return $"metadata-release-{Guid.NewGuid():N}";
        }

        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        return string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"metadata-release-{slug}-{Guid.NewGuid():N}"
            : $"metadata-release-{slug}";
    }

    private void EnsureDurableStoreConfigured()
    {
        if (_workflowStore == null)
        {
            throw new InvalidOperationException("Metadata release operations require PostgreSQL-backed durable storage.");
        }
    }

    internal static void ValidatePlan(MetadataReleaseExecutionPlan plan)
    {
        ValidateRequiredBoundedText(plan.PackageId, "packageId", MaxPackageIdLength);
        ValidateRequiredBoundedText(plan.TargetEnvironment, "targetEnvironment", MaxTargetEnvironmentLength);
        ValidateRequiredBoundedText(plan.ResourceSemanticId, "resourceSemanticId", MaxResourceSemanticIdLength);
        ValidateFieldIdentifier(plan.NewFieldName, "newFieldName");
        ValidateOptionalBoundedText(plan.DataPopulateWorkloadId, "dataPopulateWorkloadId", MaxWorkloadIdLength);
        ValidateDataPopulateFields(plan.DataPopulateFields);
        ValidateScript(plan.Script);
    }

    internal static void ValidateAuditFields(string? reason, string? idempotencyKey, string? correlationId)
    {
        ValidateOptionalBoundedText(reason, "reason", MaxReasonLength);
        ValidateOptionalBoundedText(idempotencyKey, "idempotencyKey", MaxIdempotencyKeyLength);
        ValidateOptionalBoundedText(correlationId, "correlationId", MaxCorrelationIdLength);
    }

    private static void ValidateDataPopulateFields(IReadOnlyList<string>? fields)
    {
        if (fields is null)
        {
            return;
        }

        if (fields.Count > MaxDataPopulateFieldsCount)
        {
            throw new ArgumentException($"dataPopulateFields must contain {MaxDataPopulateFieldsCount} entries or fewer.");
        }

        foreach (var field in fields)
        {
            ValidateFieldIdentifier(field, "dataPopulateFields");
        }
    }

    private static void ValidateScript(MetadataReleaseScript? script)
    {
        if (script is null)
        {
            return;
        }

        ValidateRequiredBoundedText(script.ScriptId, "scriptId", MaxScriptIdLength);

        if (script.ForwardOperations is null)
        {
            return;
        }

        foreach (var operation in script.ForwardOperations)
        {
            ValidateRequiredBoundedText(operation.ResourceSemanticId, "resourceSemanticId", MaxResourceSemanticIdLength);
            ValidateFieldIdentifier(operation.FieldName, "newFieldName");
            ValidateOptionalBoundedText(operation.FieldType, "newFieldType", MaxFieldTypeLength);
        }
    }

    private static void ValidateFieldIdentifier(string? value, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldLabel} is required.");
        }

        if (value.Length > MaxFieldNameLength || !FieldIdentifierPattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"{fieldLabel} must be a valid SQL field identifier (ASCII letter or '_' followed by letters, digits, or '_', max {MaxFieldNameLength} characters).");
        }
    }

    private static void ValidateRequiredBoundedText(string? value, string fieldLabel, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldLabel} is required.");
        }

        ValidateBoundedTextCore(value, fieldLabel, maxLength);
    }

    private static void ValidateOptionalBoundedText(string? value, string fieldLabel, int maxLength)
    {
        if (value is null)
        {
            return;
        }

        ValidateBoundedTextCore(value, fieldLabel, maxLength);
    }

    private static void ValidateBoundedTextCore(string value, string fieldLabel, int maxLength)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"{fieldLabel} must be {maxLength} characters or fewer.");
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch) || ch is '\u0085' or '\u2028' or '\u2029')
            {
                throw new ArgumentException($"{fieldLabel} must not contain control characters.");
            }
        }
    }
}
