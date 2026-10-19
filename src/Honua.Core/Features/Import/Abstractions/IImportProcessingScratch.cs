// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>Allocates private processing scratch charged to the original verified transfer.</summary>
public interface IImportProcessingScratchFactory
{
    /// <summary>Creates a workspace on the host-configured accounting volume.</summary>
    /// <param name="transfer">Trusted transfer provenance, not execution authority.</param>
    /// <param name="maximumBytes">Maximum aggregate bytes retained by this workspace.</param>
    /// <param name="maximumFiles">Maximum number of processing files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A workspace whose disposal deletes files before releasing their reservations.</returns>
    Task<IImportProcessingScratch> CreateAsync(ImportTransferProvenance transfer, long maximumBytes,
        int maximumFiles, CancellationToken cancellationToken = default);
}

/// <summary>
/// Private managed-file workspace. Consumers must use <see cref="WriteFileAsync"/> for
/// all writes; this contract does not constrain arbitrary native-process writes.
/// </summary>
public interface IImportProcessingScratch : IAsyncDisposable
{
    /// <summary>Gets the private directory for derived files and their relative layout.</summary>
    string DirectoryPath { get; }

    /// <summary>Reserves capacity, then copies exactly the declared bytes with bounded memory.</summary>
    /// <param name="source">Caller-owned input stream.</param>
    /// <param name="path">New absolute file path inside this private directory.</param>
    /// <param name="expectedBytes">Exact expected content length, including zero for empty files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after the output handle is closed.</returns>
    Task WriteFileAsync(Stream source, string path, long expectedBytes,
        CancellationToken cancellationToken = default);
}
