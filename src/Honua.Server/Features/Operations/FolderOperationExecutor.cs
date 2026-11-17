// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Admin;

namespace Honua.Server.Features.Operations;

internal sealed class FolderOperationExecutor(
    AdminApiOperationCatalog.Definition definition, IServiceProvider services, IHttpContextAccessor accessor) : IOperationExecutor
{
    public string OperationId => definition.OperationId;

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(AdminOperationRequestValidator.Validate(definition, request));

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var response = await ExecuteAsync(request, context, cancellationToken).ConfigureAwait(false);
        return CreateHandle(response, context, now);
    }

    private async Task<FolderOperationResponse> ExecuteAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken)
    {
        FolderOperationResponse response;
        if (request.OperationId != OperationId || request.DryRun)
            response = FolderOperationResponse.Problem(400, "The folder operation identity or execution mode is invalid.");
        else if (AdminOperationRequestValidator.Validate(definition, request) is { IsValid: false } validation)
            response = FolderOperationResponse.Problem(400, string.Join(" ", validation.Messages));
        else if (string.IsNullOrWhiteSpace(context.ApprovedProposalId) && accessor.HttpContext?.User.Identity?.IsAuthenticated != true)
            response = FolderOperationResponse.Problem(401, "Authentication is required for this operation.");
        else
        {
            var current = accessor.HttpContext;
            var approved = !string.IsNullOrWhiteSpace(context.ApprovedProposalId);
            var execution = await FolderMutationService.ExecuteAsync(new FolderMutationExecution
            {
                Services = services,
                Principal = approved ? new ClaimsPrincipal() : current!.User,
                Command = FolderMutationCommand.FromRequest(request),
                ExpectedEtag = request.Parameters.GetValueOrDefault("If-Match"),
                Namespace = request.Parameters.GetValueOrDefault("namespace"),
                OperationReplay = approved ? new FolderOperationReplayAdmission(request, context) : null,
                RequestCache = approved ? null : current!.Items
            }, cancellationToken).ConfigureAwait(false);
            response = FolderOperationResponse.From(execution, current?.Request.PathBase.Value);
        }
        return response;
    }

    private OperationHandle CreateHandle(FolderOperationResponse response, OperationPolicyContext context, DateTimeOffset now)
    {
        var success = response.Status is >= 200 and < 300;
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = OperationId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            CreatedAt = now,
            UpdatedAt = now,
            Status = success ? OperationHandleStatus.Completed : OperationHandleStatus.Failed,
            Reason = success ? null : FailureReason(response, context),
            Result = new OperationResultSummary
            {
                Summary = $"{definition.Title} {(success ? "completed" : "failed")}.",
                Details = response.Details()
            }
        };
    }

    internal static string FailureReason(FolderOperationResponse response, OperationPolicyContext context)
        => !string.IsNullOrWhiteSpace(context.ApprovedProposalId) && response.ApprovalRefusal is { } refusal
            ? refusal
            : $"Admin API returned {response.Status} ({Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(response.Status)}).";

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
        => Task.FromResult(OperationStatusProjection.FromHandle(handle));
}
