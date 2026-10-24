// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>Indexed retention lookup for server-published managed stream workspace artifacts.</summary>
public interface IWorkspaceStreamReferenceStore
{
    /// <summary>Whether an available or promoted workspace artifact still references the object.</summary>
    /// <param name="objectKey">Canonical staged object key.</param>
    /// <param name="storeReference">Configured shared store identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the canonical workspace lifecycle still retains the object.</returns>
    Task<bool> HasRetainedStreamAsync(string objectKey, string storeReference, CancellationToken cancellationToken = default);
}
