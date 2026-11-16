// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Geoprocessing;

namespace Honua.Server.Features.Operations;

internal sealed class StagedImportOperationDescriptorProvider : IOperationDescriptorProvider
{
    public string ProviderId => StagedImportOperation.ProviderId;

    public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(StagedImportOperation.BuildDescriptors());
}
