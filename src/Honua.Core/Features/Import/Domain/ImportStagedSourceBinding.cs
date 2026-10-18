// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Exact shared-storage source accepted by the server. A worker must verify the
/// retained generation, actual byte length and complete SHA-256 before importing.
/// This receipt is not caller-supplied authority or a storage credential.
/// </summary>
/// <param name="FileId">Server-selected shared-storage object identifier.</param>
/// <param name="ETag">Opaque provider generation returned when that object was staged.</param>
/// <param name="Bytes">Exact verified source length.</param>
/// <param name="Sha256">Lowercase SHA-256 of the complete source bytes.</param>
public sealed record ImportStagedSourceBinding(string FileId, string ETag, long Bytes, string Sha256)
{
    /// <summary>Validates a complete source receipt without granting access to the source.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FileId) || FileId.Length > 2048 || FileId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(ETag) || ETag.Length > 1024 || ETag.Any(char.IsControl) || Bytes <= 0 ||
            Sha256 is null || Sha256.Length != 64 || Sha256.Any(static value => !char.IsAsciiHexDigitLower(value)))
        {
            throw new ArgumentException("A complete staged source generation, length and SHA-256 are required.");
        }
    }
}
