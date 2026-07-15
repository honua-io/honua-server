// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

/// <summary>
/// Gateway executor that resumes an approved destructive/sink geoprocessing plan.
/// A gated plan submission does not execute directly — it is persisted as an
/// <see cref="OperationProposalStatus.AwaitingApproval"/> proposal carrying the
/// serialized <see cref="AnalysisPlan"/> as its execution payload. When a human
/// approves the proposal, the shared <see cref="IOperationGateway"/> resolves this
/// executor by <see cref="OperationClass.Geoprocess"/> and calls
/// <see cref="ExecuteAsync"/>, which re-submits the plan through the geoprocessing
/// job pipeline with the approval gate already satisfied (ADR-0064, #2814).
/// </summary>
/// <remarks>
/// The executor resolves <see cref="IGeoprocessingJobService"/> lazily from the
/// service provider inside <see cref="ExecuteAsync"/> rather than through the
/// constructor. The gateway depends on the executor set, the job service depends on
/// the gateway, so constructor-injecting the job service here would form a
/// dependency cycle; a lazy service-locator resolution at execution time breaks it.
/// </remarks>
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

    /// <summary>
    /// Builds the approval-surface plan summary shown to a reviewer for a gated plan.
    /// </summary>
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

/// <summary>
/// Class-specific execution payload for a gated geoprocessing plan carried through
/// the operation proposal. Persisted with the proposal so an approval can replay the
/// exact submission (ADR-0064, #2814).
/// </summary>
internal sealed record GeoprocessExecutionPayload
{
    /// <summary>The analysis plan to submit once the proposal is approved.</summary>
    public required AnalysisPlan Plan { get; init; }

    /// <summary>Optional idempotency key propagated from the original submission.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Stable identifier of the principal that submitted the plan, so the resumed job
    /// is attributed to (and owned by) the original submitter rather than the approver.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>Protocol metadata captured from the original submission.</summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>Parses a serialized payload, returning null when missing/malformed.</summary>
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

    /// <summary>Serializes this payload to JSON for storage on the proposal.</summary>
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
