// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

public interface IMetadataV2GraphProvider
{
    ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);

    ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default);
}

public interface IMetadataV2GraphStore : IMetadataV2GraphProvider
{
    Task<MetadataV2GraphSnapshot> SaveAsync(
        MetadataV2Graph graph,
        string? expectedEtag,
        CancellationToken cancellationToken = default);

    Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(
        long revision,
        string? expectedCurrentEtag,
        CancellationToken cancellationToken = default);
}

public interface IMetadataV2GraphRevisionStager
{
    Task<MetadataV2GraphSnapshot> StageAsync(
        MetadataV2Graph graph,
        CancellationToken cancellationToken = default);

    Task<bool> DiscardStagedAsync(
        long revision,
        CancellationToken cancellationToken = default);
}

public sealed class MetadataV2GraphConcurrencyException : InvalidOperationException
{
    public MetadataV2GraphConcurrencyException(string message, string? expectedEtag, string? actualEtag)
        : base(message)
    {
        ExpectedEtag = expectedEtag;
        ActualEtag = actualEtag;
    }

    public string? ExpectedEtag { get; }

    public string? ActualEtag { get; }
}

public sealed class MetadataV2GraphCommitOutcomeUnknownException : Exception
{
    public MetadataV2GraphCommitOutcomeUnknownException(
        MetadataV2GraphSnapshot pendingSnapshot,
        string transactionId,
        Exception innerException)
        : base("The Metadata v2 graph commit outcome could not be determined.", innerException)
    {
        PendingSnapshot = pendingSnapshot ?? throw new ArgumentNullException(nameof(pendingSnapshot));
        TransactionId = transactionId ?? throw new ArgumentNullException(nameof(transactionId));
    }

    public MetadataV2GraphSnapshot PendingSnapshot { get; }

    public string TransactionId { get; }
}
