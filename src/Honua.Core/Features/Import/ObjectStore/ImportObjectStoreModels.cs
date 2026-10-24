// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.ObjectStore;

/// <summary>
/// Machine-readable problem codes for an object-store import source.
/// </summary>
public static class ImportObjectStoreProblems
{
    /// <summary>The bucket or role is not allow-listed, or the host identity cannot read the object.</summary>
    public const string Forbidden = "import.source_forbidden";

    /// <summary>The object or bucket does not exist.</summary>
    public const string NotFound = "import.source_not_found";

    /// <summary>The object is larger than <c>MaxObjectStoreImportBytes</c>.</summary>
    public const string TooLarge = "import.source_too_large";

    /// <summary>The object changed after the preflight ETag was recorded.</summary>
    public const string Changed = "import.source_changed";

    /// <summary>The source URI is not an <c>s3://</c> URI.</summary>
    public const string UnsupportedScheme = "import.source_unsupported_scheme";

    /// <summary>Problem-type prefix. The code is appended to form the <c>type</c> URI.</summary>
    public const string TypePrefix = "https://honua.io/problems/";

    /// <summary>Builds the problem-type URI for <paramref name="code"/>.</summary>
    public static string TypeFor(string code) => TypePrefix + code;
}

/// <summary>
/// Allow-list and endpoint settings for customer object-store imports, bound from
/// <c>Import:ObjectStore</c>. An empty bucket list denies every source.
/// </summary>
public sealed class ImportObjectStoreOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Import:ObjectStore";

    /// <summary>
    /// Bucket names the host may read. An entry ending in <c>*</c> matches that prefix.
    /// Matching is case-insensitive. Empty denies every bucket.
    /// </summary>
    public string[] AllowedBuckets { get; set; } = [];

    /// <summary>Roles the request may ask the host to assume. The external id is never taken from the request.</summary>
    public ImportObjectStoreRoleOptions[] AssumeRoles { get; set; } = [];

    /// <summary>AWS region for the customer-bucket client. When omitted, the SDK default chain supplies it.</summary>
    public string? Region { get; set; }

    /// <summary>
    /// Optional service endpoint override (a VPC endpoint or a development emulator).
    /// Production imports leave this unset and use the host identity against AWS.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Path-style addressing. Enabled automatically when <see cref="ServiceUrl"/> is set.</summary>
    public bool ForcePathStyle { get; set; }
}

/// <summary>
/// One allow-listed role the host may assume for an object-store import.
/// </summary>
public sealed class ImportObjectStoreRoleOptions
{
    /// <summary>IAM role ARN.</summary>
    public string RoleArn { get; set; } = string.Empty;

    /// <summary>
    /// External id required by the role trust policy. Configured on the server and never
    /// accepted from the import request, logged, or written to the audit record.
    /// </summary>
    public string? ExternalId { get; set; }
}

/// <summary>
/// A parsed <c>s3://bucket/key</c> location. The role ARN is the caller's selection; the
/// external id stays in <see cref="ImportObjectStoreRoleOptions"/>.
/// </summary>
public sealed record ImportObjectLocation
{
    /// <summary>Bucket name.</summary>
    public required string Bucket { get; init; }

    /// <summary>Object key.</summary>
    public required string Key { get; init; }

    /// <summary>Allow-listed role to assume, or null to use the host identity.</summary>
    public string? AssumeRoleArn { get; init; }

    /// <summary>Source URI as supplied by the caller.</summary>
    public required string SourceUri { get; init; }
}

/// <summary>
/// Preflight description of an object. The ETag is pinned on the later read.
/// </summary>
public sealed record ImportObjectHead
{
    /// <summary>Object size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Provider ETag, preserved exactly (including quotes) for <c>If-Match</c>.</summary>
    public required string ETag { get; init; }

    /// <summary>Provider content type, when the object has one.</summary>
    public string? ContentType { get; init; }
}

/// <summary>
/// A refused or failed object-store read. <see cref="Code"/> is one of
/// <see cref="ImportObjectStoreProblems"/>.
/// </summary>
public sealed class ImportObjectStoreException : Exception
{
    /// <summary>Creates an exception with a stable problem code and a client-safe message.</summary>
    public ImportObjectStoreException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable problem code.</summary>
    public string Code { get; }

    /// <summary>The host identity or the allow-list refused the read.</summary>
    public static ImportObjectStoreException Forbidden(string message)
        => new(ImportObjectStoreProblems.Forbidden, message);

    /// <summary>The bucket or object does not exist.</summary>
    public static ImportObjectStoreException NotFound(string message)
        => new(ImportObjectStoreProblems.NotFound, message);

    /// <summary>The object no longer matches the pinned ETag.</summary>
    public static ImportObjectStoreException Changed(string message)
        => new(ImportObjectStoreProblems.Changed, message);
}

/// <summary>
/// Reads a customer object with the host identity. Head runs before any byte is read.
/// </summary>
public interface IImportObjectStoreReader
{
    /// <summary>Returns the object size, ETag, and content type without reading the body.</summary>
    Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the object body only when its current ETag equals <paramref name="etag"/>.
    /// The caller owns the returned stream.
    /// </summary>
    Task<Stream> OpenReadAsync(ImportObjectLocation location, string etag, CancellationToken cancellationToken);
}

/// <summary>
/// A client-facing refusal produced before or during an object-store import.
/// </summary>
public sealed record ImportObjectStoreProblem
{
    /// <summary>HTTP status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Short problem title.</summary>
    public required string Title { get; init; }

    /// <summary>Client-safe detail. Names both sizes for <see cref="ImportObjectStoreProblems.TooLarge"/>.</summary>
    public required string Detail { get; init; }

    /// <summary>Stable code, or null for a plain admin problem such as the background-import 503.</summary>
    public string? Code { get; init; }

    /// <summary>Problem-type URI.</summary>
    public string Type => Code is null
        ? "https://honua.io/problems/admin"
        : ImportObjectStoreProblems.TypeFor(Code);
}
