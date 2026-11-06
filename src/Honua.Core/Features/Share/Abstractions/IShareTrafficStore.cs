// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Share.Domain;

namespace Honua.Core.Features.Share.Abstractions;

public sealed class ShareTrafficStoreUnavailableException : Exception
{
    public ShareTrafficStoreUnavailableException(string message)
        : base(message)
    {
    }

    public ShareTrafficStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IShareTrafficStore
{
    Task<ShareTrafficSummary> GetSummaryAsync(
        ShareTrafficQuery query,
        CancellationToken cancellationToken = default);

    Task<ShareTrafficSeries> GetSeriesAsync(
        ShareTrafficQuery query,
        CancellationToken cancellationToken = default);
}
