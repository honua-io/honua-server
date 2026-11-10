// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.ObjectStore;

public static class ImportObjectStoreProblems
{
    public const string Forbidden = "import.source_forbidden";

    public const string NotFound = "import.source_not_found";

    public const string TooLarge = "import.source_too_large";

    public const string Changed = "import.source_changed";

    public const string UnsupportedScheme = "import.source_unsupported_scheme";

    public const string TypePrefix = "https://honua.io/problems/";

    public static string TypeFor(string code) => TypePrefix + code;
}

public sealed class ImportObjectStoreOptions
{
    public const string SectionName = "Import:ObjectStore";

    public string[] AllowedBuckets { get; set; } = [];

    // The external id is never taken from the request.
    public ImportObjectStoreRoleOptions[] AssumeRoles { get; set; } = [];

    public string? Region { get; set; }

    public string? ServiceUrl { get; set; }

    public bool ForcePathStyle { get; set; }
}

public sealed class ImportObjectStoreRoleOptions
{
    public string RoleArn { get; set; } = string.Empty;

    // The role-trust external id is configured on the server and is never accepted from the request, logged, or written to the audit record.
    public string? ExternalId { get; set; }
}

public sealed record ImportObjectLocation
{
    public required string Bucket { get; init; }

    public required string Key { get; init; }

    public string? AssumeRoleArn { get; init; }

    public required string SourceUri { get; init; }
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
