// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Progress;

internal static class ProgressStoreReader
{
    public static async IAsyncEnumerable<IOperationProgress> ReadAvailableAsync(
        IUniversalProgressStore store,
        IReadOnlyList<string> operationIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var operationId in operationIds)
        {
            IOperationProgress? progress;
            try
            {
                progress = await store.GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidPersistedExportProgressException)
            {
                continue;
            }
            if (progress is not null)
            {
                yield return progress;
            }
        }
    }
}
