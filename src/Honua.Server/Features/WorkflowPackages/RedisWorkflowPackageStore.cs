// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>
/// Redis-backed durable <see cref="IWorkflowPackageStore"/>. Packages, immutable
/// versions, and publications survive process restarts and are visible to every
/// replica sharing the Redis instance, keeping publication records alive for the
/// durable cron <c>WorkflowDefinition</c>s that schedule publications create.
/// </summary>
/// <remarks>
/// Key layout (no TTL — packages, versions, and publications are operator-managed
/// artifacts that must outlive restarts; payloads are small JSON graphs, not bulk data):
/// <list type="bullet">
/// <item><c>workflow-packages:pkg:{packageId}</c> — package draft JSON.</item>
/// <item><c>workflow-packages:pkg:index</c> — set of package ids for enumeration.</item>
/// <item><c>workflow-packages:ver:{packageId}:{version}</c> — immutable version JSON.</item>
/// <item><c>workflow-packages:ver:{packageId}:index</c> — set of version numbers.</item>
/// <item><c>workflow-packages:ver:{packageId}:seq</c> — monotonic version counter
/// (INCR) so concurrent replicas never allocate the same version number.</item>
/// <item><c>workflow-packages:pub:{publicationId}</c> — publication JSON (created
/// with <c>SET NX</c> so a duplicate publish maps to a 409 conflict).</item>
/// <item><c>workflow-packages:pub:index</c> — set of publication ids.</item>
/// </list>
/// The package record's <c>LatestVersion</c> is overlaid from the sequence counter at
/// read time, so a racing package write on another replica cannot make the observed
/// latest version regress.
/// </remarks>
internal sealed class RedisWorkflowPackageStore(IConnectionMultiplexer redis) : IWorkflowPackageStore
{
    private const string PackageKeyPrefix = "workflow-packages:pkg:";
    private const string PackageIndexKey = "workflow-packages:pkg:index";
    private const string VersionKeyPrefix = "workflow-packages:ver:";
    private const string PublicationKeyPrefix = "workflow-packages:pub:";
    private const string PublicationIndexKey = "workflow-packages:pub:index";

    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<IReadOnlyList<WorkflowPackage>> ListPackagesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = await _database.SetMembersAsync(PackageIndexKey).ConfigureAwait(false);
        if (ids.Length == 0)
        {
            return Array.Empty<WorkflowPackage>();
        }

        var packages = new List<WorkflowPackage>(ids.Length);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!id.HasValue)
            {
                continue;
            }

            var package = await GetPackageAsync(id.ToString(), cancellationToken).ConfigureAwait(false);
            if (package != null)
            {
                packages.Add(package);
            }
        }

        return packages
            .OrderBy(package => package.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<WorkflowPackage?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetPackageKey(packageId)).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            return null;
        }

        var package = JsonSerializer.Deserialize(
            payload.ToString(),
            WorkflowPackagesJsonContext.Default.WorkflowPackage);
        if (package is null)
        {
            return null;
        }

        // Overlay the durable version counter so a stale package write from a racing
        // replica can never make the observed latest version regress.
        var latest = await GetVersionSequenceAsync(packageId).ConfigureAwait(false);
        if (latest is int sequence && sequence > (package.LatestVersion ?? 0))
        {
            package = package with { LatestVersion = sequence };
        }

        return package;
    }

    public async Task<WorkflowPackage> SavePackageAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        var sequence = await GetVersionSequenceAsync(package.PackageId).ConfigureAwait(false);
        var stored = package with { LatestVersion = sequence ?? package.LatestVersion };

        var payload = JsonSerializer.Serialize(stored, WorkflowPackagesJsonContext.Default.WorkflowPackage);
        await _database.StringSetAsync(GetPackageKey(package.PackageId), payload).ConfigureAwait(false);
        await _database.SetAddAsync(PackageIndexKey, package.PackageId).ConfigureAwait(false);
        return stored;
    }

    public async Task<WorkflowPackageVersion> CreateVersionAsync(
        string packageId,
        string packageHash,
        WorkflowPackageValidationResult validation,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageHash);
        ArgumentNullException.ThrowIfNull(validation);
        cancellationToken.ThrowIfCancellationRequested();

        var package = await GetPackageAsync(packageId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workflow package '{packageId}' was not found.");

        // INCR allocates the next version number atomically across replicas.
        var nextVersion = (int)await _database.StringIncrementAsync(GetVersionSequenceKey(packageId)).ConfigureAwait(false);
        var version = new WorkflowPackageVersion
        {
            PackageId = packageId,
            Version = nextVersion,
            SchemaVersion = package.Graph.SchemaVersion,
            PackageHash = packageHash,
            Graph = package.Graph,
            Validation = validation,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy
        };

        var versionPayload = JsonSerializer.Serialize(version, WorkflowPackagesJsonContext.Default.WorkflowPackageVersion);
        await _database.StringSetAsync(GetVersionKey(packageId, nextVersion), versionPayload).ConfigureAwait(false);
        await _database.SetAddAsync(GetVersionIndexKey(packageId), nextVersion).ConfigureAwait(false);

        // Best-effort refresh of the stored package record; reads overlay LatestVersion
        // from the sequence counter, so a lost update here cannot regress observers.
        var updatedPackage = package with { LatestVersion = nextVersion, UpdatedAt = package.UpdatedAt };
        var packagePayload = JsonSerializer.Serialize(updatedPackage, WorkflowPackagesJsonContext.Default.WorkflowPackage);
        await _database.StringSetAsync(GetPackageKey(packageId), packagePayload).ConfigureAwait(false);

        return version;
    }

    public async Task<WorkflowPackageVersion?> GetVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetVersionKey(packageId, version)).ConfigureAwait(false);
        return payload.HasValue
            ? JsonSerializer.Deserialize(payload.ToString(), WorkflowPackagesJsonContext.Default.WorkflowPackageVersion)
            : null;
    }

    public async Task<IReadOnlyList<WorkflowPackageVersion>> ListVersionsAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        cancellationToken.ThrowIfCancellationRequested();

        var members = await _database.SetMembersAsync(GetVersionIndexKey(packageId)).ConfigureAwait(false);
        if (members.Length == 0)
        {
            return Array.Empty<WorkflowPackageVersion>();
        }

        var versions = new List<WorkflowPackageVersion>(members.Length);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!member.TryParse(out long versionNumber))
            {
                continue;
            }

            var version = await GetVersionAsync(packageId, (int)versionNumber, cancellationToken).ConfigureAwait(false);
            if (version != null)
            {
                versions.Add(version);
            }
        }

        return versions
            .OrderByDescending(version => version.Version)
            .ToArray();
    }

    public async Task<WorkflowPublication> SavePublicationAsync(
        WorkflowPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.Serialize(publication, WorkflowPackagesJsonContext.Default.WorkflowPublication);

        // Publications are immutable, stable run targets. SET NX rejects re-use of an
        // existing publication id across all replicas, so a second publish cannot
        // silently retarget a live publication to a different package/version.
        var created = await _database.StringSetAsync(
            GetPublicationKey(publication.PublicationId),
            payload,
            when: When.NotExists).ConfigureAwait(false);
        if (!created)
        {
            throw new WorkflowPublicationConflictException(publication.PublicationId);
        }

        await _database.SetAddAsync(PublicationIndexKey, publication.PublicationId).ConfigureAwait(false);
        return publication;
    }

    public async Task<WorkflowPublication?> GetPublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetPublicationKey(publicationId)).ConfigureAwait(false);
        return payload.HasValue
            ? JsonSerializer.Deserialize(payload.ToString(), WorkflowPackagesJsonContext.Default.WorkflowPublication)
            : null;
    }

    public async Task<IReadOnlyList<WorkflowPublication>> ListPublicationsAsync(
        string? packageId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = await _database.SetMembersAsync(PublicationIndexKey).ConfigureAwait(false);
        if (ids.Length == 0)
        {
            return Array.Empty<WorkflowPublication>();
        }

        var publications = new List<WorkflowPublication>(ids.Length);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!id.HasValue)
            {
                continue;
            }

            var publication = await GetPublicationAsync(id.ToString(), cancellationToken).ConfigureAwait(false);
            if (publication is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(packageId)
                && !string.Equals(publication.PackageId, packageId, StringComparison.Ordinal))
            {
                continue;
            }

            publications.Add(publication);
        }

        return publications
            .OrderByDescending(publication => publication.CreatedAt)
            .ToArray();
    }

    private async Task<int?> GetVersionSequenceAsync(string packageId)
    {
        var payload = await _database.StringGetAsync(GetVersionSequenceKey(packageId)).ConfigureAwait(false);
        return payload.HasValue && payload.TryParse(out long sequence) && sequence > 0
            ? (int)sequence
            : null;
    }

    private static string GetPackageKey(string packageId) => PackageKeyPrefix + packageId;

    private static string GetVersionKey(string packageId, int version)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{VersionKeyPrefix}{packageId}:{version}");

    private static string GetVersionIndexKey(string packageId) => VersionKeyPrefix + packageId + ":index";

    private static string GetVersionSequenceKey(string packageId) => VersionKeyPrefix + packageId + ":seq";

    private static string GetPublicationKey(string publicationId) => PublicationKeyPrefix + publicationId;
}
