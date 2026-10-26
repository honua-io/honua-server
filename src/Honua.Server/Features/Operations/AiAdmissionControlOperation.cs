// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Admission;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Operations;

internal static class AiAdmissionControlOperation
{
    internal const string ActorPin = "__aiAcceptedActorId";
    internal static readonly IReadOnlyDictionary<string, AiAdmissionControlAction> Actions =
        new Dictionary<string, AiAdmissionControlAction>(StringComparer.Ordinal)
        {
            ["admin.ai.admission.initialize"] = AiAdmissionControlAction.Initialize,
            ["admin.ai.admission.policy-update"] = AiAdmissionControlAction.UpdatePolicy,
            ["admin.ai.admission.stop"] = AiAdmissionControlAction.Stop,
            ["admin.ai.admission.resume"] = AiAdmissionControlAction.Resume
        };

    internal static OperationDescriptor Descriptor(string id) => new()
    {
        OperationId = id,
        ProviderId = ServicePublishOperation.ProviderId,
        Category = "admin",
        Title = Actions[id] switch
        {
            AiAdmissionControlAction.Initialize => "Initialize AI request budgets",
            AiAdmissionControlAction.UpdatePolicy => "Update AI request budgets",
            AiAdmissionControlAction.Stop => "Stop model invocations",
            _ => "Resume model invocations"
        },
        Description = "Controls durable AI invocation attempts without resetting accounting.",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.DeploymentScope,
            SideEffectClass = OperationSideEffectClass.MutatesMetadata,
            Determinism = OperationDeterminism.RuntimeDynamic,
            SupportsDryRun = false
        },
        InputSchema = new[] { "deploymentId", "expectedGeneration", "expectedRevision", "expectedEpoch", "policy" }
            .Select(name => new OperationParameterDescriptor
            {
                Name = name,
                Title = name,
                Required = name == "policy" && Actions[id] is AiAdmissionControlAction.Initialize or AiAdmissionControlAction.UpdatePolicy,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            }).ToArray(),
        OutputSchema = []
    };

    internal static Dictionary<string, string?> ValidateParameters(OperationRequest request, string deploymentId)
    {
        if (!Actions.ContainsKey(request.OperationId) || string.IsNullOrWhiteSpace(deploymentId)
            || deploymentId.Length > 128 || request.DryRun || request.ConnectionId is not null
            || request.ServiceName is not null || request.Fields.Count != 0 || request.SecretParameters.Count != 0)
            throw new ArgumentException("Invalid AI admission operation request.");
        var allowed = new HashSet<string>(["deploymentId", "expectedGeneration", "expectedRevision", "expectedEpoch", "policy", ActorPin], StringComparer.Ordinal);
        if (request.Parameters.Count > allowed.Count || request.Parameters.Any(pair => !allowed.Contains(pair.Key) || pair.Value?.Length > 4096))
            throw new ArgumentException("Invalid AI admission operation parameters.");
        var parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal);
        if (parameters.TryGetValue("deploymentId", out var target) && !string.Equals(target, deploymentId, StringComparison.Ordinal))
            throw new ArgumentException("AI admission controls target only this configured deployment.");
        parameters["deploymentId"] = deploymentId;
        var action = Actions[request.OperationId];
        if (action is AiAdmissionControlAction.Initialize or AiAdmissionControlAction.UpdatePolicy)
        {
            if (!parameters.TryGetValue("policy", out var json) || string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("An explicit AI admission policy is required.");
            var policy = JsonSerializer.Deserialize(json, AiAdmissionControlJsonContext.Default.AiAdmissionPolicy);
            if (policy is not { IsValid: true })
                throw new ArgumentException("Invalid AI admission policy.");
            parameters["policy"] = JsonSerializer.Serialize(policy, AiAdmissionControlJsonContext.Default.AiAdmissionPolicy);
        }
        else if (parameters.ContainsKey("policy"))
            throw new ArgumentException("This AI admission action does not change policy.");
        var expectedCount = new[] { "expectedGeneration", "expectedRevision", "expectedEpoch" }.Count(parameters.ContainsKey);
        if (expectedCount is not (0 or 3))
            throw new ArgumentException("Supply the complete AI admission expected-state tuple or omit it.");
        if (expectedCount == 3)
            _ = BuildCommand(request.OperationId, parameters, "validation", "validation", "validation");
        return parameters;
    }

    internal static AiAdmissionControlCommand BuildCommand(string id, IReadOnlyDictionary<string, string?> parameters,
        string actor, string invocation, string correlation)
    {
        if (!Guid.TryParse(parameters.GetValueOrDefault("expectedGeneration"), out var generation)
            || !long.TryParse(parameters.GetValueOrDefault("expectedRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || !long.TryParse(parameters.GetValueOrDefault("expectedEpoch"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            throw new ArgumentException("Invalid AI admission expected-state tuple.");
        var policy = parameters.TryGetValue("policy", out var json)
            ? JsonSerializer.Deserialize(json!, AiAdmissionControlJsonContext.Default.AiAdmissionPolicy) : null;
        var command = new AiAdmissionControlCommand(parameters["deploymentId"]!, Actions[id], generation, revision, epoch,
            actor, invocation, correlation, policy);
        if (!command.IsValid)
            throw new ArgumentException("Invalid AI admission control.");
        return command;
    }
}

internal sealed class AiAdmissionControlExecutor(string operationId, IServiceProvider services)
    : Honua.Core.Features.Operations.Abstractions.IOperationExecutor, IOperationRequestPreparer
{
    // Scoped operation executors can still serve concurrent submissions (for example one
    // MCP batch). Bind immutable commands to exact prepared request objects, never shared
    // "last prepared" fields that another submission can overwrite between check and apply.
    private readonly ConditionalWeakTable<OperationRequest, AiAdmissionControlCommand> _accepted = new();
    public string OperationId => operationId;

    public async Task<OperationRequest> PrepareAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
    {
        if (request.OperationId != OperationId)
            throw new ArgumentException("AI admission operation identity mismatch.");
        var deployment = services.GetRequiredService<IOptions<AiExecutionAdmissionOptions>>().Value.DeploymentId;
        var parameters = AiAdmissionControlOperation.ValidateParameters(request, deployment);
        string actor;
        if (string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            if (request.Parameters.ContainsKey(AiAdmissionControlOperation.ActorPin))
                throw new ArgumentException("Caller-supplied AI admission actor pins are forbidden.");
            var principal = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
            actor = principal is null ? null! : JobOwnershipSecurity.ResolveOwner(principal)!;
            if (string.IsNullOrWhiteSpace(actor) || !string.Equals(actor, context.PrincipalId, StringComparison.Ordinal))
                throw new InvalidOperationException("AI admission controls require a durable authenticated actor.");
        }
        else
        {
            var proposals = services.GetService<IOperationProposalStore>()
                ?? throw new InvalidOperationException("AI admission replay requires durable proposal authority.");
            var proposal = await proposals.GetAsync(context.ApprovedProposalId, cancellationToken).ConfigureAwait(false);
            if (proposal is not { Status: OperationProposalStatus.Executing, SealedPlanHash: not null }
                || proposal.Kind != OperationClass.AdminConfigChange || proposal.OperationId != OperationId || proposal.RequestedBy != context.PrincipalId
                || proposal.Audit.OperationInstanceId != context.OperationInstanceId
                || proposal.SealedPlanHash != context.ApprovedPlanHash
                || proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan))
                throw new InvalidOperationException("AI admission replay proof does not match its accepted operation.");
            var payload = JsonSerializer.Deserialize(proposal.Plan.ExecutionPayload!, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                ?? throw new InvalidOperationException("AI admission replay payload is unavailable.");
            if (payload.OperationId != OperationId || payload.TenantId != context.TenantId || payload.SchemaName != context.SchemaName
                || payload.Parameters.Count != parameters.Count
                || payload.Parameters.Any(pair => !parameters.TryGetValue(pair.Key, out var value) || pair.Value != value)
                || !payload.Parameters.TryGetValue(AiAdmissionControlOperation.ActorPin, out var sealedActor)
                || string.IsNullOrWhiteSpace(sealedActor) || sealedActor != proposal.RequestedBy)
                throw new InvalidOperationException("AI admission replay parameters or requester were changed.");
            actor = sealedActor;
        }
        parameters[AiAdmissionControlOperation.ActorPin] = actor;
        if (!parameters.ContainsKey("expectedGeneration"))
        {
            var state = AiAdmissionControlOperation.Actions[OperationId] == AiAdmissionControlAction.Initialize ? null
                : await services.GetRequiredService<IAiExecutionAdmissionStore>().GetStateAsync(deployment, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("AI admission ledger is uninitialized.");
            parameters["expectedGeneration"] = (state?.Generation ?? Guid.Empty).ToString("D");
            parameters["expectedRevision"] = (state?.Revision ?? 0).ToString(CultureInfo.InvariantCulture);
            parameters["expectedEpoch"] = (state?.Epoch ?? 0).ToString(CultureInfo.InvariantCulture);
        }
        var acceptedCommand = AiAdmissionControlOperation.BuildCommand(OperationId, parameters, actor,
            context.OperationInstanceId ?? throw new InvalidOperationException("Canonical invocation identity is missing."),
            context.CorrelationId ?? throw new InvalidOperationException("Canonical correlation identity is missing."));
        var prepared = request with { Parameters = parameters.ToFrozenDictionary(StringComparer.Ordinal) };
        _accepted.Add(prepared, acceptedCommand);
        return prepared;
    }

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        _ = AiAdmissionControlOperation.ValidateParameters(request,
            services.GetRequiredService<IOptions<AiExecutionAdmissionOptions>>().Value.DeploymentId);
        return Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
    {
        if (!_accepted.TryGetValue(request, out var acceptedCommand)
            || acceptedCommand.InvocationId != context.OperationInstanceId || acceptedCommand.ActorId != context.PrincipalId)
            throw new InvalidOperationException("AI admission control was not prepared under verified server authority.");
        var result = await services.GetRequiredService<IAiExecutionAdmissionStore>().ApplyControlAsync(acceptedCommand, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var succeeded = result.Applied || result.Code is "stopped" or "resumed";
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = result.Code };
        if (result.State is { } state)
        {
            details["generation"] = state.Generation.ToString("D");
            details["revision"] = state.Revision.ToString(CultureInfo.InvariantCulture);
            details["epoch"] = state.Epoch.ToString(CultureInfo.InvariantCulture);
            details["stopped"] = state.Stopped.ToString(CultureInfo.InvariantCulture);
            details["notBeforeUtc"] = state.NotBeforeUtc.ToString("O", CultureInfo.InvariantCulture);
        }
        return new OperationHandle
        {
            OperationId = OperationId,
            OperationInstanceId = context.OperationInstanceId!,
            CorrelationId = context.CorrelationId!,
            Status = succeeded ? OperationHandleStatus.Completed : OperationHandleStatus.Denied,
            CreatedAt = now,
            UpdatedAt = now,
            Reason = result.Code,
            Result = new OperationResultSummary { Summary = "AI admission control: " + result.Code, Details = details }
        };
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
        => Task.FromResult(new OperationStatus
        {
            OperationId = handle.OperationId,
            OperationInstanceId = handle.OperationInstanceId,
            CorrelationId = handle.CorrelationId,
            Status = handle.Status,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            Result = handle.Result,
            Reason = handle.Reason,
            EvidenceRefs = handle.EvidenceRefs
        });
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AiAdmissionPolicy))]
internal sealed partial class AiAdmissionControlJsonContext : JsonSerializerContext;
