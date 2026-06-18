// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.MapGeneration;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Concrete executor for <c>map.generate</c>. It WRAPS the existing
/// <see cref="IMapGenerationService"/> rather than reimplementing generation:
/// <see cref="ValidateAsync"/> validates the prompt input, and <see cref="SubmitAsync"/> calls
/// <c>GenerateAsync</c> and wraps the produced draft map package into an
/// <see cref="OperationHandle"/>.
/// </summary>
/// <remarks>
/// This is the strangler proof. Unlike the synchronous <see cref="ServicePublishExecutor"/>
/// (which returns a Completed handle for a committed mutation), this executor returns a
/// Completed handle whose <see cref="OperationHandle.Result"/> carries the produced DRAFT and
/// whose <see cref="OperationHandle.ApprovalLane"/> is the Studio publish-request lane: the
/// generator produced a draft that now awaits the Studio draft → version → publish-request
/// lifecycle. The same Validate / Submit→Handle / GetStatus contract that models a sync publish
/// also models a generator-yields-a-draft-entering-a-lane — the toolset absorbs generators.
/// Generation itself is owned by <see cref="IMapGenerationService"/> in Honua.Ai; this executor
/// is a thin wrapper that adapts the request and frames the result as a lifecycle handle.
/// </remarks>
internal sealed class MapGenerateExecutor : IOperationExecutor
{
    private readonly IMapGenerationService _mapGenerationService;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of <see cref="MapGenerateExecutor"/>.
    /// </summary>
    /// <param name="mapGenerationService">Shared map generation service (reused, not reimplemented).</param>
    /// <param name="clock">Time provider for handle id generation.</param>
    public MapGenerateExecutor(IMapGenerationService mapGenerationService, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(mapGenerationService);
        ArgumentNullException.ThrowIfNull(clock);
        _mapGenerationService = mapGenerationService;
        _clock = clock;
    }

    /// <inheritdoc />
    public string OperationId => MapGenerateOperation.OperationId;

    /// <inheritdoc />
    public Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read-only, side-effect-free input validation: a non-empty prompt is required (the same
        // precondition the Studio endpoint enforces before invoking the generator). The richer
        // structural validation of the produced map happens inside the generation service itself.
        var prompt = GetOptional(request, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult(new OperationValidation
            {
                IsValid = false,
                Status = "invalid",
                Messages = ["Required operation parameter 'prompt' is missing or empty."]
            });
        }

        return Task.FromResult(new OperationValidation
        {
            IsValid = true,
            Status = "valid",
            Messages = ["Prompt accepted; generation will produce a draft map package for the Studio publish-request lane."]
        });
    }

    /// <inheritdoc />
    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var prompt = GetRequired(request, "prompt");

        // WRAP the existing generator — generation is NOT reimplemented here.
        var result = await _mapGenerationService
            .GenerateAsync(
                new MapGenerationRequest
                {
                    Prompt = prompt,
                    Provider = GetOptional(request, "provider"),
                    Model = GetOptional(request, "model")
                },
                cancellationToken)
            .ConfigureAwait(false);

        // The generator produced a DRAFT. Frame it as a handle entering the Studio
        // publish-request lifecycle: Status=Completed (the draft exists synchronously), with the
        // produced draft on Result and ApprovalLane set to the Studio publish-request lane the
        // draft now awaits. A non-"generated" outcome (clarification / unsupported / error)
        // produced no draft, so it carries no approval lane.
        var producedDraft = string.Equals(result.Status, GeneratedStatus, StringComparison.Ordinal)
            && result.Package is not null;

        return new OperationHandle
        {
            OperationId = OperationId,
            HandleId = NewHandleId(),
            Status = OperationHandleStatus.Completed,
            ApprovalLane = producedDraft ? MapGenerateOperation.StudioPublishRequestLane : null,
            Reason = producedDraft ? null : result.Rationale,
            Result = new OperationResultSummary
            {
                Summary = producedDraft
                    ? $"Generated draft map package '{result.Package!.MapPackageId}' ({result.Package.Status}); awaiting the Studio publish-request lane."
                    : $"Generation returned status '{result.Status}' without a draft.",
                Details = BuildDetails(result, producedDraft)
            }
        };
    }

    /// <inheritdoc />
    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        // map.generate produces the draft synchronously: the submit handle is terminal. Status
        // mirrors it. The downstream publish-request lifecycle is tracked by the Studio lane, not
        // by this executor's status poll.
        return Task.FromResult(new OperationStatus
        {
            OperationId = OperationId,
            HandleId = handle.HandleId,
            Status = handle.Status,
            Result = handle.Result,
            JobId = handle.JobId,
            MetadataRevision = handle.MetadataRevision
        });
    }

    private const string GeneratedStatus = "generated";

    private static Dictionary<string, string> BuildDetails(MapGenerationResult result, bool producedDraft)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = result.Status
        };

        if (producedDraft)
        {
            var draft = result.Package!;
            details["mapPackageId"] = draft.MapPackageId;
            details["packageStatus"] = draft.Status.ToString();
            details["format"] = draft.Format;
            details["approvalLane"] = MapGenerateOperation.StudioPublishRequestLane;
        }

        if (!string.IsNullOrWhiteSpace(result.Provider))
        {
            details["provider"] = result.Provider!;
        }

        if (!string.IsNullOrWhiteSpace(result.Model))
        {
            details["model"] = result.Model!;
        }

        return details;
    }

    private static string GetRequired(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value!
            : throw new ArgumentException($"Required operation parameter '{name}' is missing.");

    private static string? GetOptional(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private string NewHandleId()
        => $"op-{_clock.GetUtcNow().ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}"[..32];
}
