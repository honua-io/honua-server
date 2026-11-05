// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

internal sealed class GeoprocessOperationExecutor : IOperationExecutor
{
    private readonly IServiceProvider _serviceProvider;

    public GeoprocessOperationExecutor(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public OperationClass OperationClass => OperationClass.Geoprocess;

    public Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = GeoprocessExecutionPayload.Parse(request.ExecutionPayload);
        if (payload?.Plan == null)
        {
            return Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
            {
                Summary = "Geoprocessing plan",
                BlockingReasons = ["Geoprocessing execution payload is missing or malformed."],
                RiskLevel = ProposalRiskLevel.High,
                ExecutionPayload = request.ExecutionPayload,
            });
        }

        return Task.FromResult<OperationProposalPlan?>(BuildPlanSummary(payload, request.ExecutionPayload));
    }

    public async Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
    {
        var payload = GeoprocessExecutionPayload.Parse(executionPayload);
        if (payload?.Plan == null)
        {
            throw new InvalidOperationException("Geoprocessing execution payload is missing or malformed.");
        }

        // Resolve the job service lazily to avoid the gateway <-> job-service cycle.
        var jobService = _serviceProvider.GetRequiredService<IGeoprocessingJobService>();
        var record = await jobService
            .ResumeApprovedJobAsync(payload, cancellationToken)
            .ConfigureAwait(false);

        return record.OperationId;
    }

    public async Task<OperationBackendStatus?> GetBackendStatusAsync(
        string executionOperationId,
        CancellationToken cancellationToken = default)
    {
        var store = _serviceProvider.GetService<IExecutionJobStore>();
        var job = store is null
            ? null
            : await store.GetAsync(executionOperationId, cancellationToken).ConfigureAwait(false);
        return job is null ? null : new OperationBackendStatus
        {
            State = job.Status switch
            {
                ExecutionJobStatus.Succeeded => OperationBackendState.Succeeded,
                ExecutionJobStatus.Failed => OperationBackendState.Failed,
                ExecutionJobStatus.Cancelled => OperationBackendState.Cancelled,
                ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning => OperationBackendState.Queued,
                _ => OperationBackendState.Running,
            },
            Reason = job.Status is ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled
                ? $"Backend job reached {job.Status}."
                : null,
        };
    }

    internal static OperationProposalPlan BuildPlanSummary(GeoprocessExecutionPayload payload, string? executionPayload)
    {
        var plan = payload.Plan!;
        var gatedProcessId = plan.Steps
            .Where(step => step.Kind == AnalysisPlanStepKind.Geoprocess && !string.IsNullOrWhiteSpace(step.ProcessId))
            .Select(step => step.ProcessId!)
            .FirstOrDefault();

        var diff = new List<string> { $"plan: {plan.PlanId}" };
        if (gatedProcessId != null)
        {
            diff.Add($"process: {gatedProcessId}");
        }

        return new OperationProposalPlan
        {
            Summary = gatedProcessId == null
                ? $"Geoprocessing plan {plan.PlanId}"
                : $"Geoprocessing plan {plan.PlanId} ({gatedProcessId})",
            Diff = diff,
            RiskLevel = ProposalRiskLevel.High,
            ExecutionPayload = executionPayload,
        };
    }
}

internal sealed record GeoprocessExecutionPayload
{
    public required AnalysisPlan Plan { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? RequestedBy { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }

    // The submitter security snapshot is pinned on the proposal because resume rebuilds the principal from that snapshot alone and would otherwise apply no row filter.
    public JobSecurityContext? SubmitterSecurityContext { get; init; }

    public static GeoprocessExecutionPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, GeoprocessExecutionPayloadJsonContext.Default.GeoprocessExecutionPayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Serialize()
        => JsonSerializer.Serialize(this, GeoprocessExecutionPayloadJsonContext.Default.GeoprocessExecutionPayload);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeoprocessExecutionPayload))]
internal sealed partial class GeoprocessExecutionPayloadJsonContext : JsonSerializerContext
{
}
