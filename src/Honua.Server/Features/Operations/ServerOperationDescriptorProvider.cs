// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Surfaces the server-side operation descriptors into the grounding catalog. It contributes
/// the synchronous <c>service.publish</c> descriptor and the Studio <c>map.generate</c>
/// generator descriptor (a draft-producing operation that enters the publish-request lane);
/// DevOps descriptors join the catalog through their own provider in a later phase (descriptors
/// only — execution stays remote).
/// </summary>
internal sealed class ServerOperationDescriptorProvider : IOperationDescriptorProvider
{
    /// <inheritdoc />
    public string ProviderId => ServicePublishOperation.ProviderId;

    /// <inheritdoc />
    public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IOperationDescriptor>>(
        [
            ServicePublishOperation.BuildDescriptor(),
            MapGenerateOperation.BuildDescriptor()
        ]);
}
