// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IWorkspaceStreamReferenceStore
{
    Task<bool> HasRetainedStreamAsync(string objectKey, string storeReference, CancellationToken cancellationToken = default);
}
