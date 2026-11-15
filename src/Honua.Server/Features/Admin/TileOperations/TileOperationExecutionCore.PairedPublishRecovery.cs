// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Descriptors;
using Honua.Server.Features.Admin.PairedPublications;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed partial class TileOperationExecutionCore
{
    // A durable retry keeps its operation id. Recover its activated version before reading
    // fresh data: a missing job receipt must not turn one operation into a second publication.
    private static async Task<TileOperationProgress> RecoverActivatedAsync(
        PairedPublishContext context, PublicationPointer recoveryPointer, TileOperationProgress progress, CancellationToken cancellationToken)
    {
        if (recoveryPointer.PublicationId != context.Run.PublicationId
            || await context.Store.PointerStateAsync(recoveryPointer.Version).ConfigureAwait(false) != PointerState.NamesVersion)
        {
            throw new PairedActivationUnknownException(
                "This operation already started a version whose activation is no longer observable; its artifacts were retained. Start a new publish explicitly if needed.");
        }

        var previous = await context.Store.ReadDescriptorAsync(recoveryPointer, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.PublicationId != context.Run.PublicationId || previous.Version != recoveryPointer.Version
                || previous.SourceLayer.Service != context.Source.Published.Service.Metadata.Id
                || previous.SourceLayer.Layer != context.Source.Resource.Metadata.Id
                || previous.Validation.Status != PublicationValidation.Passed
                || PublicationDescriptorRules.Violations(previous).Count != 0)
            {
                throw new PairedPublicationException("The stored publication does not match this operation's source.");
            }

            return progress with
            {
                PublishedPublication = new PublishedPublication(previous.PublicationId, previous.Version,
                    previous.Capture.CaptureId, context.Store.VersionKey(previous.Version, PairedPublicationDescriptorFactory.DescriptorFileName),
                    context.Store.PointerKey),
                Status = OperationStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null,
                CurrentPhase = "Recovered activated paired publication"
            };
        }

        throw new PairedActivationUnknownException("The activated operation's descriptor could not be recovered; its artifacts were retained.");
    }

    // The switch is not cancellable: once storage may have applied it, the job must learn which
    // version is current rather than abandon the answer.
    internal static async Task SwitchPointerAsync(PairedPublicationStore store, PairedPublicationRun run, string? expectedETag)
    {
        var pointer = new PublicationPointer(
            run.PublicationId, run.Version, $"{run.Version}/{PairedPublicationDescriptorFactory.DescriptorFileName}", DateTimeOffset.UtcNow);
        bool switched;
        try
        {
            switched = await store.SwitchPointerAsync(pointer, expectedETag, CancellationToken.None).ConfigureAwait(false);
            // UploadResult has no typed rejection/unknown distinction. Providers also return
            // failure for network errors, so a false result cannot authorize deleting artifacts.
        }
        catch (Exception)
        {
            // A write that threw may still land later, so no read now can rule it out.
            switched = false;
        }

        if (switched)
        {
            return;
        }

        var state = await store.PointerStateAsync(run.Version).ConfigureAwait(false);
        switch (state)
        {
            case PointerState.NamesVersion:
                return;
            case PointerState.Unknown:
            case PointerState.NamesOther:
                throw new PairedActivationUnknownException(
                    $"The activation outcome of version {run.Version} is unknown; the version was kept. Check the publication pointer before retrying.");
        }
    }

}
