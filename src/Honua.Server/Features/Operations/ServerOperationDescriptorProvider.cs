// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Surfaces the server-side operation descriptors into the grounding catalog. Today it
/// contributes the <c>service.publish</c> descriptor; DevOps descriptors join the catalog
/// through their own provider in a later phase (descriptors only — execution stays remote).
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
            ServicePublishOperation.BuildDescriptor()
        ]);
}
