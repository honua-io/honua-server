// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Mrtr;

// The operation and actor a resumed approval must belong to.
internal sealed record ApprovalRequester(string OperationId, string? ActorId)
{
    public Func<OperationHandle, CancellationToken, Task<McpToolsCallResult>>? OnCompleted { get; init; }
    public Func<OperationHandle, McpToolsCallResult?>? OnRunning { get; init; }
    public Func<OperationProposal, OperationHandle, CancellationToken, Task>? OnFailed { get; init; }
}

// In-client approval over MRTR. The agent's call files the proposal as before; a client that declared
// URL-mode elicitation is sent to the Console approval page for that proposal, where the human-session
// check decides. The retry only reads the proposal row back: the client's answer is never approval
// evidence, and the retry never executes or asks again.
internal static class ApprovalContinuation
{
    internal const string InputKey = "approve";

    internal static readonly TimeSpan StateLifetime = TimeSpan.FromHours(1);

    internal const string ConsentNotice =
        "Review and approve this change in Honua Console, signed in as yourself. "
        + "Accepting in your MCP client only opens the page; it does not approve the change.";

    public static McpToolsCallResult? TryRequest(HttpContext httpContext, OperationHandle handle)
    {
        if (McpRequestContinuation.From(httpContext) is not { Modes.Url: true } continuation
            || handle.Status != OperationHandleStatus.RequiresApproval
            || handle.ProposalId is not { Length: > 0 } proposalId
            || httpContext.RequestServices.GetService<McpMrtrRuntime>()?.ApprovalUrl(proposalId) is not { } url)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mode", "url");
            writer.WriteString("message", ConsentNotice);
            writer.WriteString("url", url.AbsoluteUri);
            writer.WriteString("elicitationId", proposalId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return continuation.InputRequired(
            InputKey,
            document.RootElement.Clone(),
            new McpRequestStateGrant(McpRequestStateKind.Approval, proposalId, continuation.Codec.UtcNow + StateLifetime));
    }

    // Returns null when this call is not an approval retry.
    public static async Task<McpToolsCallResult?> TryResumeAsync(
        HttpContext httpContext,
        ApprovalRequester requester,
        CancellationToken cancellationToken)
    {
        if (McpRequestContinuation.From(httpContext) is not { State.Kind: McpRequestStateKind.Approval } continuation)
        {
            return null;
        }

        if (continuation.Response(InputKey) is null)
        {
            throw new GeoprocessingValidationException($"An approval retry requires inputResponses.{InputKey}.");
        }

        var store = httpContext.RequestServices.GetService<IOperationProposalStore>()
            ?? throw new InvalidOperationException("The proposal store is unavailable.");
        var proposal = await store.GetAsync(continuation.State!.Reference, cancellationToken).ConfigureAwait(false);
        RequireProposalAccess(httpContext, requester, proposal);

        var operation = await ReadOperationAsync(httpContext, proposal, cancellationToken).ConfigureAwait(false);
        return await ProjectRetryAsync(requester, (proposal, operation), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<McpToolsCallResult> ProjectRetryAsync(
        ApprovalRequester requester, (OperationProposal Proposal, OperationHandle? Operation) execution,
        CancellationToken cancellationToken)
    {
        var (proposal, operation) = execution;
        var output = Project(proposal, OperationProposalExecutionView.From(proposal, operation));
        if (requester.OnCompleted is { } onCompleted && output.Status == nameof(OperationHandleStatus.Completed))
        {
            if (operation?.Status != OperationHandleStatus.Completed || !MatchesOperation(proposal, operation))
            {
                throw new GeoprocessingPreconditionFailedException("The completed approval result is unavailable.");
            }

            return await onCompleted(operation, cancellationToken).ConfigureAwait(false);
        }

        if (requester.OnRunning is { } onRunning && output.Status == nameof(OperationHandleStatus.Running)
            && operation is { Status: OperationHandleStatus.Queued or OperationHandleStatus.Running }
            && !string.IsNullOrWhiteSpace(output.JobId) && operation.JobId == output.JobId
            && MatchesOperation(proposal, operation)
            && onRunning(operation with { Status = OperationHandleStatus.Running }) is { } running)
        {
            return running;
        }

        if (requester.OnFailed is { } onFailed && output.Status == nameof(OperationHandleStatus.Failed)
            && operation?.Status == OperationHandleStatus.Failed && MatchesOperation(proposal, operation))
        {
            await onFailed(proposal, operation, cancellationToken).ConfigureAwait(false);
        }

        return StatusResult(output);
    }

    private static McpToolsCallResult StatusResult(McpOperationToolOutput output)
    {
        var result = McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpOperationToolOutput);
        result.IsError = output.Status is nameof(OperationHandleStatus.Failed)
            or nameof(OperationHandleStatus.Rejected)
            or nameof(OperationHandleStatus.Cancelled);
        return result;
    }

    private static async Task<OperationHandle?> ReadOperationAsync(
        HttpContext httpContext, OperationProposal proposal, CancellationToken cancellationToken)
        => proposal.Audit.OperationInstanceId is { Length: > 0 } operationInstanceId
            ? await (httpContext.RequestServices.GetService<IOperationInstanceStore>()
                ?? throw new InvalidOperationException("The canonical operation instance store is unavailable."))
                .GetAsync(operationInstanceId, cancellationToken).ConfigureAwait(false)
            : null;

    private static void RequireProposalAccess(HttpContext httpContext, ApprovalRequester requester, [NotNull] OperationProposal? proposal)
    {
        var tenantId = httpContext.RequestServices.GetService<ITenantContext>()?.TenantId;
        if (proposal is null
            || !string.Equals(proposal.OperationId, requester.OperationId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(requester.ActorId)
            || !string.Equals(proposal.RequestedBy, requester.ActorId, StringComparison.Ordinal)
            || !string.Equals(proposal.TenantId, tenantId, StringComparison.Ordinal))
        {
            // Same answer for missing and foreign proposals, so the retry reveals nothing.
            throw new GeoprocessingValidationException("The proposal for this approval is not available.");
        }
    }

    private static bool MatchesOperation(OperationProposal proposal, [NotNullWhen(true)] OperationHandle? operation)
        => operation is not null
            && operation.OperationInstanceId == proposal.Audit.OperationInstanceId
            && operation.OperationId == proposal.OperationId
            && operation.ProposalId == proposal.ProposalId
            && operation.TenantId == proposal.TenantId;

    internal static OperationHandleStatus Map(OperationProposalStatus status) => status switch
    {
        OperationProposalStatus.Planned or OperationProposalStatus.AwaitingApproval => OperationHandleStatus.RequiresApproval,
        OperationProposalStatus.Executing or OperationProposalStatus.Submitted
            or OperationProposalStatus.Reconciling => OperationHandleStatus.Running,
        OperationProposalStatus.Succeeded => OperationHandleStatus.Completed,
        OperationProposalStatus.Rejected => OperationHandleStatus.Rejected,
        OperationProposalStatus.Cancelled => OperationHandleStatus.Cancelled,
        _ => OperationHandleStatus.Failed,
    };

    // A failed proposal's recorded reason says what to do next (for example, submit a fresh proposal).
    private static string Describe(OperationProposalStatus status, string? resolutionReason)
        => status is OperationProposalStatus.Failed or OperationProposalStatus.Rejected or OperationProposalStatus.Cancelled
            && !string.IsNullOrWhiteSpace(resolutionReason)
            ? resolutionReason
            : Describe(status);

    private static string Describe(OperationProposalStatus status) => status switch
    {
        OperationProposalStatus.Planned or OperationProposalStatus.AwaitingApproval =>
            "The proposal is still awaiting approval in Honua Console; observe the proposal for its result.",
        OperationProposalStatus.Executing or OperationProposalStatus.Submitted or OperationProposalStatus.Reconciling =>
            "The approved change is running; observe the proposal for its result.",
        OperationProposalStatus.Succeeded => "The approved change completed.",
        OperationProposalStatus.Rejected => "The proposal was rejected.",
        OperationProposalStatus.Cancelled => "The proposal was cancelled.",
        OperationProposalStatus.RolledBack => "The approved change was applied and then rolled back.",
        _ => "The approved change did not complete.",
    };

    private static McpOperationToolOutput Project(OperationProposal proposal, OperationProposalExecutionView execution)
    {
        var status = execution.Status == proposal.Status.ToString()
            ? Map(proposal.Status)
            : Enum.Parse<OperationHandleStatus>(execution.Status);
        var instanceId = proposal.Audit.OperationInstanceId ?? string.Empty;
        return new McpOperationToolOutput
        {
            Status = status.ToString(),
            RequiresApproval = status == OperationHandleStatus.RequiresApproval,
            OperationId = proposal.OperationId ?? string.Empty,
            HandleId = instanceId,
            OperationInstanceId = instanceId,
            ProposalId = proposal.ProposalId,
            CorrelationId = proposal.Audit.CorrelationId ?? string.Empty,
            CreatedAt = proposal.CreatedAt,
            UpdatedAt = execution.UpdatedAt,
            JobId = proposal.ExecutionOperationId,
            Message = execution.Status == proposal.Status.ToString()
                ? Describe(proposal.Status, execution.ResolutionReason)
                : execution.ResolutionReason ?? $"The approved change is {status.ToString().ToLowerInvariant()}.",
        };
    }
}
