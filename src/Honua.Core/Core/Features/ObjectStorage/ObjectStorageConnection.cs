// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ObjectStorage;

public static class ObjectStorageConnectionKinds
{
    public const string Kind = "object-storage";

    public const string Provider = "aws-s3";

    // Customer roles must carry this name prefix; the server refuses any other role ARN.
    public const string RoleNamePrefix = "honua-connection-";
}

// A read-only grant on one bucket and prefix. Built-in connections use the task identity and carry no role.
public sealed record ObjectStorageConnection
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string Provider { get; init; } = ObjectStorageConnectionKinds.Provider;

    public required string Bucket { get; init; }

    // Normalised: empty, or ending in '/'.
    public string Prefix { get; init; } = string.Empty;

    public required string Region { get; init; }

    public string? RoleArn { get; init; }

    // Bound once at create time and never returned by the API or written to logs.
    public string? ExternalId { get; init; }

    public bool BuiltIn { get; init; }

    public string CreatedBy { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    // The default record printer would include the external id.
    public override string ToString() => $"ObjectStorageConnection {{ Id = {Id}, Bucket = {Bucket}, Prefix = {Prefix} }}";
}

// A server-minted external id waiting for its single use by create.
public sealed record ObjectStorageConnectionSetup
{
    public required string ExternalId { get; init; }

    public required string Bucket { get; init; }

    public string Prefix { get; init; } = string.Empty;

    public required DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => $"ObjectStorageConnectionSetup {{ Bucket = {Bucket}, Prefix = {Prefix} }}";
}

public enum ObjectStorageCreateOutcome
{
    Created,
    SetupRejected,
}

public interface IObjectStorageConnectionStore
{
    // False when the installation already holds the maximum number of open setups.
    Task<bool> SaveSetupAsync(ObjectStorageConnectionSetup setup, CancellationToken cancellationToken);

    // Returns the setup only while it is unexpired and unconsumed.
    Task<ObjectStorageConnectionSetup?> FindOpenSetupAsync(string externalId, CancellationToken cancellationToken);

    // Consumes the matching open setup and inserts the connection in one transaction.
    Task<ObjectStorageCreateOutcome> CreateAsync(ObjectStorageConnection connection, CancellationToken cancellationToken);

    Task<ObjectStorageConnection?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken);

    // Writes name, description, region and role ARN only; bucket, prefix and external id stay as created.
    Task<bool> UpdateAsync(ObjectStorageConnection connection, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record ObjectStorageTestResult
{
    public required bool IsHealthy { get; init; }

    public required string Message { get; init; }
}

public sealed record ObjectStorageBrowseRequest
{
    public required ObjectStorageConnection Connection { get; init; }

    // Already checked to lie under the connection prefix.
    public required string Prefix { get; init; }
}

public sealed record ObjectStorageBrowseEntry
{
    public required string Key { get; init; }

    public long? Bytes { get; init; }

    public string Kind { get; init; } = ObjectStorageObjectKinds.Other;

    public DateTimeOffset? LastModified { get; init; }
}

public sealed record ObjectStorageBrowseResult
{
    public required string Bucket { get; init; }

    public required string Prefix { get; init; }

    public IReadOnlyList<string> Folders { get; init; } = [];

    public IReadOnlyList<ObjectStorageBrowseEntry> Objects { get; init; } = [];

    public bool Truncated { get; init; }
}

public interface IObjectStoreConnector
{
    Task<ObjectStorageTestResult> TestAsync(ObjectStorageConnection connection, CancellationToken cancellationToken);

    Task<ObjectStorageBrowseResult> BrowseAsync(ObjectStorageBrowseRequest request, CancellationToken cancellationToken);
}

public sealed class ObjectStorageConnectionOptions
{
    public const string SectionName = "Connections:ObjectStorage";

    // CloudFormation template that creates the honua-connection-* role; quick-create links are omitted when unset.
    public string? QuickCreateTemplateUrl { get; set; }

    // Principal the template trusts; passed to the template as a parameter when set.
    public string? TrustedPrincipalArn { get; set; }

    public TimeSpan SetupLifetime { get; set; } = TimeSpan.FromHours(24);
}
