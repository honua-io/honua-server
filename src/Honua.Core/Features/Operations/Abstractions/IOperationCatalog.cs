// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IOperationDescriptorProvider
{
    string ProviderId { get; }

    Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default);
}

public interface IOperationCatalog
{
    Task<OperationCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<OperationDescriptor?> GetDescriptorAsync(string operationId, CancellationToken cancellationToken = default);
}
