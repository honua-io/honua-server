// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Import.FileImport.Resumable;

// Root names select host-bound directories, never paths supplied by an agent.
internal enum ImportStorageRootKind
{
    ProviderObjects,
    SourceScratch,
    ProcessingScratch
}

internal sealed record ImportStoragePhysicalLocation(ImportStorageRootKind Root,
    string RelativePath, long MaximumContentBytes)
{
    internal void Validate(ImportStorageReservation reservation)
    {
        if (!Enum.IsDefined(Root) || string.IsNullOrEmpty(RelativePath) || RelativePath.Length > 1280 ||
            RelativePath.Any(char.IsControl) || RelativePath.Contains('\\') || Path.IsPathRooted(RelativePath) ||
            MaximumContentBytes < 0 || MaximumContentBytes > reservation.Bytes)
            throw new ArgumentException("Invalid host-relative storage location.");
        var segments = RelativePath.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Storage locations cannot contain path aliases.");
        var leaf = reservation.FileId[(reservation.FileId.LastIndexOf('/') + 1)..];
        switch (Root)
        {
            case ImportStorageRootKind.ProviderObjects:
                if (RelativePath != reservation.FileId || leaf.StartsWith("scratch-", StringComparison.Ordinal) ||
                    MaximumContentBytes != reservation.Bytes)
                    throw new ArgumentException("Provider storage must bind its exact immutable object key and length.");
                break;
            case ImportStorageRootKind.SourceScratch:
                if (!leaf.StartsWith("scratch-", StringComparison.Ordinal) ||
                    RelativePath != $"honua-upload-{reservation.UploadId}-{leaf[8..]}.tmp" || MaximumContentBytes != reservation.Bytes)
                    throw new ArgumentException("Source scratch must bind its generated filename and complete source length.");
                break;
            case ImportStorageRootKind.ProcessingScratch:
                var prefix = $"honua-processing-{reservation.UploadId}-";
                if (!leaf.StartsWith("scratch-", StringComparison.Ordinal) || segments.Length is < 2 or > 9 ||
                    !segments[0].StartsWith(prefix, StringComparison.Ordinal) || !IsIdentity(segments[0][prefix.Length..]) ||
                    segments.Skip(1).Any(segment => segment == ".reservations") ||
                    reservation.Bytes != checked(Math.Max(1, MaximumContentBytes) + ImportProcessingScratch.FileAccountingOverhead))
                    throw new ArgumentException("Processing scratch must bind its private workspace, content and overhead.");
                break;
        }
    }

    internal string? MetadataPath(ImportStorageReservation reservation) => Root switch
    {
        ImportStorageRootKind.ProviderObjects => ".metadata/" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reservation.FileId))) + ".json",
        ImportStorageRootKind.ProcessingScratch => RelativePath[..RelativePath.IndexOf('/')] + "/.reservations/" +
            reservation.FileId[(reservation.FileId.LastIndexOf("scratch-", StringComparison.Ordinal) + 8)..] + ".json",
        _ => null
    };

    internal static bool IsIdentity(string value) => value.Length == 32 &&
        Guid.TryParseExact(value, "N", out var parsed) && parsed.ToString("N") == value;
}
