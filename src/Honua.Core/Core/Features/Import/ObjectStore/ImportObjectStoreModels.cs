// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ObjectStorage;

namespace Honua.Core.Features.Import.ObjectStore;

public static class ImportObjectStoreProblems
{
    public const string Forbidden = "import.source_forbidden";

    public const string NotFound = "import.source_not_found";

    public const string TooLarge = "import.source_too_large";

    public const string Changed = "import.source_changed";

    public const string RequesterPays = "import.source_requester_pays";

    public const string TypePrefix = "https://honua.io/problems/";

    public static string TypeFor(string code) => TypePrefix + code;
}

// Endpoint settings for S3 clients. Which buckets and roles are reachable is decided by connections.
public sealed class ImportObjectStoreOptions
{
    public const string SectionName = "Import:ObjectStore";

    public string? Region { get; set; }

    // Emulators only; refused when the environment is Production.
    public string? ServiceUrl { get; set; }

    public bool ForcePathStyle { get; set; }
}

public sealed record ImportObjectLocation
{
    public required ObjectStorageConnection Connection { get; init; }

    public required string Key { get; init; }

    public string Bucket => Connection.Bucket;

    public string SourceUri => $"s3://{Connection.Bucket}/{Key}";

    public static ImportObjectLocation From(ObjectStorageResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.Succeeded || resolution.Connection is null || resolution.Key is null)
        {
            throw new ArgumentException("Only a resolved connection object has a location.", nameof(resolution));
        }

        return new ImportObjectLocation
        {
            Connection = resolution.Connection,
            Key = resolution.Key,
        };
    }

    public override string ToString() => $"ImportObjectLocation {{ SourceUri = {SourceUri}, ConnectionId = {Connection.Id} }}";
}

public sealed record ImportObjectHead
{
    public required long SizeBytes { get; init; }

    public required string ETag { get; init; }

    public string? ContentType { get; init; }
}

public sealed class ImportObjectStoreException : Exception
{
    public ImportObjectStoreException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public static ImportObjectStoreException Forbidden(string message)
        => new(ImportObjectStoreProblems.Forbidden, message);

    public static ImportObjectStoreException NotFound(string message)
        => new(ImportObjectStoreProblems.NotFound, message);

    public static ImportObjectStoreException Changed(string message)
        => new(ImportObjectStoreProblems.Changed, message);

    public static ImportObjectStoreException RequesterPays()
        => new(
            ImportObjectStoreProblems.RequesterPays,
            "Requester-pays buckets are not supported. Turn off requester pays or copy the data to a bucket you pay for.");
}

public interface IImportObjectStoreReader
{
    Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(ImportObjectLocation location, string etag, CancellationToken cancellationToken);
}

public sealed record ImportObjectStoreProblem
{
    public required int StatusCode { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public string? Code { get; init; }

    public string Type => Code is null
        ? "https://honua.io/problems/admin"
        : ImportObjectStoreProblems.TypeFor(Code);
}
