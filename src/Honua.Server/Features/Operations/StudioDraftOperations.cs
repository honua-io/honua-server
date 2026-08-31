// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.WorkflowPackages.Domain;
using OperationGatewayRequest = Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest;

namespace Honua.Server.Features.Operations;

internal static class StudioDraftOperations
{
    public const string Create = "studio.draft.create";
    public const string Update = "studio.draft.update";
    public const string Delete = "studio.draft.delete";
    public const string Validate = "studio.draft.validate";
    public const string PreviewPlan = "studio.draft.preview-plan";
    public const string PayloadParameter = "payload";
    public const string ResultParameter = "payload";

    public static IReadOnlyList<IOperationDescriptor> BuildDescriptors() =>
    [
        Build(Create, "Create Studio draft", OperationSideEffectClass.CreatesMetadata),
        Build(Update, "Update Studio draft", OperationSideEffectClass.MutatesMetadata),
        Build(Delete, "Delete Studio draft", OperationSideEffectClass.DestroysState),
        Build(
            Validate,
            "Validate Studio draft",
            OperationSideEffectClass.MutatesMetadata,
            OperationDeterminism.RuntimeDynamic),
        Build(
            PreviewPlan,
            "Preview Studio draft plan",
            OperationSideEffectClass.MutatesMetadata,
            OperationDeterminism.RuntimeDynamic),
    ];

    private static OperationDescriptor Build(
        string operationId,
        string title,
        OperationSideEffectClass sideEffectClass,
        OperationDeterminism determinism = OperationDeterminism.Deterministic) => new()
        {
            OperationId = operationId,
            ProviderId = ServicePublishOperation.ProviderId,
            Title = title,
            Description = "Mutates a Studio draft through the canonical durable operation runtime.",
            Category = "studio",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = OperationApprovalModel.OperatorGate,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = OperationBlastRadiusClass.ResourceScope,
                SideEffectClass = sideEffectClass,
                Determinism = determinism,
                SupportsDryRun = false,
            },
            InputSchema =
            [
                new OperationParameterDescriptor
                {
                    Name = PayloadParameter,
                    Title = "Typed Studio mutation payload",
                    Required = true,
                    Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text },
                },
            ],
            OutputSchema =
            [
                new OperationParameterDescriptor
                {
                    Name = ResultParameter,
                    Title = "Typed Studio mutation result",
                    Required = false,
                    Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text },
                },
            ],
        };
}

internal sealed record StudioDraftCreatePayload
{
    public required CreateStudioPackageDraftCommand Command { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
}

internal sealed record StudioDraftUpdatePayload
{
    public required Guid DraftId { get; init; }
    public required UpdateStudioPackageDraftCommand Command { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
}

internal sealed record StudioDraftDeletePayload
{
    public required Guid DraftId { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
}

internal sealed record StudioDraftActorPayload
{
    public required Guid DraftId { get; init; }
    public string? ActorId { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
}

internal abstract class StudioDraftMutationExecutor<TPayload, TResult>(
    IStudioPackageLifecycleService lifecycle,
    TimeProvider clock) : IOperationExecutor
{
    protected IStudioPackageLifecycleService Lifecycle { get; } = lifecycle;

    protected abstract JsonTypeInfo<TPayload> PayloadType { get; }

    protected abstract JsonTypeInfo<TResult> ResultType { get; }

    public abstract string OperationId { get; }

    public Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = Parse(request);
        return Task.FromResult(new OperationValidation
        {
            IsValid = true,
            Status = "valid",
            ApprovalPlan = new OperationProposalPlan
            {
                Summary = $"Execute {OperationId} with its accepted typed payload.",
                RiskLevel = OperationId == StudioDraftOperations.Delete
                    ? ProposalRiskLevel.High
                    : ProposalRiskLevel.Medium,
                ExecutionPayload = RequirePayload(request),
            },
        });
    }

    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        TResult result;
        try
        {
            result = await ActuateAsync(Parse(request), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return new OperationHandle
            {
                OperationInstanceId = context.OperationInstanceId
                    ?? throw new InvalidOperationException("Studio actuation requires a canonical operation instance."),
                OperationId = OperationId,
                CorrelationId = context.CorrelationId
                    ?? throw new InvalidOperationException("Studio actuation requires a canonical correlation identity."),
                Status = OperationHandleStatus.Failed,
                CreatedAt = now,
                UpdatedAt = now,
                Reason = ex.Message,
                Result = new OperationResultSummary
                {
                    Summary = ex.Message,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["errorKind"] = ex switch
                        {
                            ArgumentException => "argument",
                            KeyNotFoundException => "not-found",
                            StudioCompositionConflictException => "owner-conflict",
                            _ => "conflict",
                        },
                    },
                },
            };
        }

        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId
                ?? throw new InvalidOperationException("Studio actuation requires a canonical operation instance."),
            OperationId = OperationId,
            CorrelationId = context.CorrelationId
                ?? throw new InvalidOperationException("Studio actuation requires a canonical correlation identity."),
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Result = new OperationResultSummary
            {
                Summary = $"{OperationId} completed.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [StudioDraftOperations.ResultParameter] = JsonSerializer.Serialize(result, ResultType),
                },
            },
            ResourceIds = ResourceIds(result),
        };
    }

    public Task<OperationStatus> GetStatusAsync(
        OperationHandle handle,
        CancellationToken cancellationToken = default) => Task.FromResult(new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = handle.OperationId,
            CorrelationId = handle.CorrelationId,
            AuditId = handle.AuditId,
            ProposalId = handle.ProposalId,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            AuthorizationOutcome = handle.AuthorizationOutcome,
            PolicyDecision = handle.PolicyDecision,
            Status = handle.Status,
            Result = handle.Result,
            Reason = handle.Reason,
            ResourceIds = handle.ResourceIds,
            EvidenceRefs = handle.EvidenceRefs,
        });

    protected abstract Task<TResult> ActuateAsync(TPayload payload, CancellationToken cancellationToken);

    protected virtual IReadOnlyDictionary<string, string> ResourceIds(TResult result) =>
        new Dictionary<string, string>(StringComparer.Ordinal);

    private TPayload Parse(OperationRequest request) => JsonSerializer.Deserialize(RequirePayload(request), PayloadType)
        ?? throw new ArgumentException($"The typed payload for '{OperationId}' is invalid.", nameof(request));

    private static string RequirePayload(OperationRequest request)
        => request.Parameters.TryGetValue(StudioDraftOperations.PayloadParameter, out var payload)
            && !string.IsNullOrWhiteSpace(payload)
                ? payload
                : throw new ArgumentException("The typed Studio mutation payload is required.", nameof(request));
}

internal sealed class StudioDraftCreateExecutor(IStudioPackageLifecycleService lifecycle, TimeProvider clock)
    : StudioDraftMutationExecutor<StudioDraftCreatePayload, StudioPackageDraft>(lifecycle, clock)
{
    public override string OperationId => StudioDraftOperations.Create;
    protected override JsonTypeInfo<StudioDraftCreatePayload> PayloadType => StudioDraftOperationJsonContext.Default.StudioDraftCreatePayload;
    protected override JsonTypeInfo<StudioPackageDraft> ResultType => StudioDraftOperationJsonContext.Default.StudioPackageDraft;

    protected override Task<StudioPackageDraft> ActuateAsync(
        StudioDraftCreatePayload payload,
        CancellationToken cancellationToken) => Lifecycle.CreateDraftAsync(payload.Command, cancellationToken);

    protected override IReadOnlyDictionary<string, string> ResourceIds(StudioPackageDraft result) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["draftId"] = result.DraftId.ToString("D"),
            ["itemId"] = result.ItemId.ToString("D"),
        };
}

internal sealed class StudioDraftUpdateExecutor(IStudioPackageLifecycleService lifecycle, TimeProvider clock)
    : StudioDraftMutationExecutor<StudioDraftUpdatePayload, StudioPackageDraft>(lifecycle, clock)
{
    public override string OperationId => StudioDraftOperations.Update;
    protected override JsonTypeInfo<StudioDraftUpdatePayload> PayloadType => StudioDraftOperationJsonContext.Default.StudioDraftUpdatePayload;
    protected override JsonTypeInfo<StudioPackageDraft> ResultType => StudioDraftOperationJsonContext.Default.StudioPackageDraft;

    protected override async Task<StudioPackageDraft> ActuateAsync(
        StudioDraftUpdatePayload payload,
        CancellationToken cancellationToken) => await Lifecycle
            .UpdateDraftAsync(payload.DraftId, payload.Command, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Studio draft '{payload.DraftId:D}' was not found.");

    protected override IReadOnlyDictionary<string, string> ResourceIds(StudioPackageDraft result) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["draftId"] = result.DraftId.ToString("D") };
}

internal sealed class StudioDraftDeleteExecutor(IStudioPackageLifecycleService lifecycle, TimeProvider clock)
    : StudioDraftMutationExecutor<StudioDraftDeletePayload, bool>(lifecycle, clock)
{
    public override string OperationId => StudioDraftOperations.Delete;
    protected override JsonTypeInfo<StudioDraftDeletePayload> PayloadType => StudioDraftOperationJsonContext.Default.StudioDraftDeletePayload;
    protected override JsonTypeInfo<bool> ResultType => StudioDraftOperationJsonContext.Default.Boolean;

    protected override Task<bool> ActuateAsync(
        StudioDraftDeletePayload payload,
        CancellationToken cancellationToken) => Lifecycle.DeleteDraftAsync(payload.DraftId, cancellationToken);
}

internal sealed class StudioDraftValidateExecutor(IStudioPackageLifecycleService lifecycle, TimeProvider clock)
    : StudioDraftMutationExecutor<StudioDraftActorPayload, StudioValidationSummary>(lifecycle, clock)
{
    public override string OperationId => StudioDraftOperations.Validate;
    protected override JsonTypeInfo<StudioDraftActorPayload> PayloadType => StudioDraftOperationJsonContext.Default.StudioDraftActorPayload;
    protected override JsonTypeInfo<StudioValidationSummary> ResultType => StudioDraftOperationJsonContext.Default.StudioValidationSummary;

    protected override async Task<StudioValidationSummary> ActuateAsync(
        StudioDraftActorPayload payload,
        CancellationToken cancellationToken) => await Lifecycle
            .ValidateDraftAsync(payload.DraftId, payload.ActorId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Studio draft '{payload.DraftId:D}' was not found.");
}

internal sealed class StudioDraftPreviewPlanExecutor(IStudioPackageLifecycleService lifecycle, TimeProvider clock)
    : StudioDraftMutationExecutor<StudioDraftActorPayload, StudioPreviewPlan>(lifecycle, clock)
{
    public override string OperationId => StudioDraftOperations.PreviewPlan;
    protected override JsonTypeInfo<StudioDraftActorPayload> PayloadType => StudioDraftOperationJsonContext.Default.StudioDraftActorPayload;
    protected override JsonTypeInfo<StudioPreviewPlan> ResultType => StudioDraftOperationJsonContext.Default.StudioPreviewPlan;

    protected override async Task<StudioPreviewPlan> ActuateAsync(
        StudioDraftActorPayload payload,
        CancellationToken cancellationToken) => await Lifecycle
            .PreviewPlanAsync(payload.DraftId, payload.ActorId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Studio draft '{payload.DraftId:D}' was not found.");
}

internal sealed class StudioDraftMutationRuntime(
    IOperationInvoker invoker,
    IOperationInstanceStore instanceStore) : IStudioDraftMutationRuntime
{
    public Task<StudioDraftMutationReceipt<StudioPackageDraft>> CreateAsync(
        CreateStudioPackageDraftCommand command,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default) => InvokeAsync(
            StudioDraftOperations.Create,
            new StudioDraftCreatePayload { Command = command },
            StudioDraftOperationJsonContext.Default.StudioDraftCreatePayload,
            StudioDraftOperationJsonContext.Default.StudioPackageDraft,
            context,
            cancellationToken);

    public Task<StudioDraftMutationReceipt<StudioPackageDraft>> UpdateAsync(
        Guid draftId,
        UpdateStudioPackageDraftCommand command,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default) => InvokeAsync(
            StudioDraftOperations.Update,
            new StudioDraftUpdatePayload { DraftId = draftId, Command = command },
            StudioDraftOperationJsonContext.Default.StudioDraftUpdatePayload,
            StudioDraftOperationJsonContext.Default.StudioPackageDraft,
            context,
            cancellationToken);

    public Task<StudioDraftMutationReceipt<bool>> DeleteAsync(
        Guid draftId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default) => InvokeAsync(
            StudioDraftOperations.Delete,
            new StudioDraftDeletePayload { DraftId = draftId },
            StudioDraftOperationJsonContext.Default.StudioDraftDeletePayload,
            StudioDraftOperationJsonContext.Default.Boolean,
            context,
            cancellationToken);

    public Task<StudioDraftMutationReceipt<StudioValidationSummary>> ValidateAsync(
        Guid draftId,
        string? actorId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default) => InvokeAsync(
            StudioDraftOperations.Validate,
            new StudioDraftActorPayload { DraftId = draftId, ActorId = actorId },
            StudioDraftOperationJsonContext.Default.StudioDraftActorPayload,
            StudioDraftOperationJsonContext.Default.StudioValidationSummary,
            context,
            cancellationToken);

    public Task<StudioDraftMutationReceipt<StudioPreviewPlan>> PreviewAsync(
        Guid draftId,
        string? actorId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default) => InvokeAsync(
            StudioDraftOperations.PreviewPlan,
            new StudioDraftActorPayload { DraftId = draftId, ActorId = actorId },
            StudioDraftOperationJsonContext.Default.StudioDraftActorPayload,
            StudioDraftOperationJsonContext.Default.StudioPreviewPlan,
            context,
            cancellationToken);

    private async Task<StudioDraftMutationReceipt<TResult>> InvokeAsync<TPayload, TResult>(
        string operationId,
        TPayload payload,
        JsonTypeInfo<TPayload> payloadType,
        JsonTypeInfo<TResult> resultType,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken)
    {
        var request = new OperationRequest
        {
            OperationId = operationId,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [StudioDraftOperations.PayloadParameter] = JsonSerializer.Serialize(payload, payloadType),
            },
            GatewayRequest = new OperationGatewayRequest
            {
                OperationId = operationId,
                Kind = OperationClass.StudioDraftMutation,
                RequestedBy = context.PrincipalId,
                CorrelationId = context.CorrelationId,
                IdempotencyKey = ScopeIdempotencyKey(context),
                ScopeGoverned = context.ScopeGoverned,
                RecognizedScopes = context.RecognizedScopes,
            },
        };
        var handle = await invoker.SubmitAsync(request, new OperationPolicyContext
        {
            PrincipalId = context.PrincipalId,
            TenantId = context.TenantId,
            SchemaName = context.SchemaName,
            CorrelationId = context.CorrelationId,
            IdempotencyKey = ScopeIdempotencyKey(context),
            AuthorizationOutcome = context.AuthorizationOutcome,
            Roles = context.Roles,
            ScopeGoverned = context.ScopeGoverned,
            RecognizedScopes = context.RecognizedScopes,
        }, cancellationToken).ConfigureAwait(false);

        var durable = await instanceStore.GetAsync(handle.OperationInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Studio mutation envelope was not durably readable after routing.");
        TResult? value = default;
        if (durable.Status == OperationHandleStatus.Completed
            && durable.Result?.Details.TryGetValue(StudioDraftOperations.ResultParameter, out var serialized) == true)
        {
            value = JsonSerializer.Deserialize(serialized, resultType);
        }

        return new StudioDraftMutationReceipt<TResult> { Operation = durable, Value = value };
    }

    private static string? ScopeIdempotencyKey(StudioDraftMutationContext context)
    {
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            return null;
        }

        var material = Encoding.UTF8.GetBytes(
            $"{context.TenantId ?? "<default>"}:{context.PrincipalId ?? "<anonymous>"}:{context.IdempotencyKey}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }
}

internal sealed class StudioDraftApprovalRequestMapper(string operationId) : IOperationApprovalRequestMapper
{
    public string OperationId { get; } = operationId;

    public OperationGatewayRequest Map(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision)
    {
        if (!string.Equals(descriptor.OperationId, OperationId, StringComparison.Ordinal)
            || !string.Equals(request.OperationId, OperationId, StringComparison.Ordinal)
            || !request.Parameters.TryGetValue(StudioDraftOperations.PayloadParameter, out var payload)
            || string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("The Studio approval mapper requires its exact typed payload.", nameof(request));
        }

        payload = SealScope(payload, context);
        return new OperationGatewayRequest
        {
            OperationId = OperationId,
            OperationInstanceId = context.OperationInstanceId,
            Kind = OperationClass.StudioDraftMutation,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            IdempotencyKey = context.IdempotencyKey,
            ExecutionPayload = payload,
            Plan = new OperationProposalPlan
            {
                Summary = $"Execute {OperationId} with its accepted typed payload.",
                RiskLevel = OperationId == StudioDraftOperations.Delete
                    ? ProposalRiskLevel.High
                    : ProposalRiskLevel.Medium,
                ExecutionPayload = payload,
            },
        };
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        var payload = request.Plan?.ExecutionPayload ?? request.ExecutionPayload
            ?? throw new InvalidOperationException("The persisted Studio mutation payload is unavailable.");
        ValidatePayload(payload);
        var scope = ReadScope(payload);
        return new OperationApprovalReplayMapping
        {
            Request = new OperationRequest
            {
                OperationId = OperationId,
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [StudioDraftOperations.PayloadParameter] = payload,
                },
            },
            TenantId = scope.TenantId,
            SchemaName = scope.SchemaName,
        };
    }

    private string SealScope(string payload, OperationPolicyContext context) => OperationId switch
    {
        StudioDraftOperations.Create => JsonSerializer.Serialize(
            JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftCreatePayload)! with
            { TenantId = context.TenantId, SchemaName = context.SchemaName },
            StudioDraftOperationJsonContext.Default.StudioDraftCreatePayload),
        StudioDraftOperations.Update => JsonSerializer.Serialize(
            JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftUpdatePayload)! with
            { TenantId = context.TenantId, SchemaName = context.SchemaName },
            StudioDraftOperationJsonContext.Default.StudioDraftUpdatePayload),
        StudioDraftOperations.Delete => JsonSerializer.Serialize(
            JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftDeletePayload)! with
            { TenantId = context.TenantId, SchemaName = context.SchemaName },
            StudioDraftOperationJsonContext.Default.StudioDraftDeletePayload),
        StudioDraftOperations.Validate or StudioDraftOperations.PreviewPlan => JsonSerializer.Serialize(
            JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftActorPayload)! with
            { TenantId = context.TenantId, SchemaName = context.SchemaName },
            StudioDraftOperationJsonContext.Default.StudioDraftActorPayload),
        _ => throw new InvalidOperationException($"Unsupported Studio mutation descriptor '{OperationId}'."),
    };

    private (string? TenantId, string? SchemaName) ReadScope(string payload) => OperationId switch
    {
        StudioDraftOperations.Create => Read(JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftCreatePayload)!),
        StudioDraftOperations.Update => Read(JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftUpdatePayload)!),
        StudioDraftOperations.Delete => Read(JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftDeletePayload)!),
        StudioDraftOperations.Validate or StudioDraftOperations.PreviewPlan => Read(JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftActorPayload)!),
        _ => throw new InvalidOperationException($"Unsupported Studio mutation descriptor '{OperationId}'."),
    };

    private static (string? TenantId, string? SchemaName) Read(StudioDraftCreatePayload payload) => (payload.TenantId, payload.SchemaName);
    private static (string? TenantId, string? SchemaName) Read(StudioDraftUpdatePayload payload) => (payload.TenantId, payload.SchemaName);
    private static (string? TenantId, string? SchemaName) Read(StudioDraftDeletePayload payload) => (payload.TenantId, payload.SchemaName);
    private static (string? TenantId, string? SchemaName) Read(StudioDraftActorPayload payload) => (payload.TenantId, payload.SchemaName);

    private void ValidatePayload(string payload)
    {
        object? parsed = OperationId switch
        {
            StudioDraftOperations.Create => JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftCreatePayload),
            StudioDraftOperations.Update => JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftUpdatePayload),
            StudioDraftOperations.Delete => JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftDeletePayload),
            StudioDraftOperations.Validate or StudioDraftOperations.PreviewPlan => JsonSerializer.Deserialize(payload, StudioDraftOperationJsonContext.Default.StudioDraftActorPayload),
            _ => throw new InvalidOperationException($"Unsupported Studio mutation descriptor '{OperationId}'."),
        };
        if (parsed is null)
        {
            throw new InvalidOperationException("The persisted Studio mutation payload is invalid.");
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StudioDraftCreatePayload))]
[JsonSerializable(typeof(StudioDraftUpdatePayload))]
[JsonSerializable(typeof(StudioDraftDeletePayload))]
[JsonSerializable(typeof(StudioDraftActorPayload))]
[JsonSerializable(typeof(StudioPackageDraft))]
[JsonSerializable(typeof(StudioValidationSummary))]
[JsonSerializable(typeof(StudioPreviewPlan))]
[JsonSerializable(typeof(bool))]
internal sealed partial class StudioDraftOperationJsonContext : JsonSerializerContext;
