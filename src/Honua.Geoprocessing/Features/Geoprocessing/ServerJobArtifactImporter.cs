// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Geoprocessing.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

/// <summary>One committed import of an authorized job artifact. Replay returns this receipt and does not import again.</summary>
internal sealed record ServerJobArtifactImportReceipt(
    string IntentId,
    string Schema,
    string Table,
    string PhysicalTable,
    int FeatureCount,
    int SourceSrid,
    int TargetSrid,
    string ContentSha256,
    string CrsState);

/// <summary>Caller-supplied command. The server reads the artifact; the caller does not supply bytes or a path.</summary>
internal sealed record ServerJobArtifactImportCommand(
    string IntentId,
    string ActorId,
    string Schema,
    string Table,
    DatasetImportServerJobArtifactReference Reference,
    int TargetSrid,
    int? SourceSrid);

/// <summary>Remembers one committed command so an exact replay cannot insert a second copy.</summary>
internal interface IServerJobArtifactImportLedger
{
    /// <summary>Returns the original receipt when <paramref name="fingerprint"/> still matches.</summary>
    Task<ServerJobArtifactImportReceipt> GetOrImportAsync(
        string actorId,
        string intentId,
        string fingerprint,
        Func<CancellationToken, Task<ServerJobArtifactImportReceipt>> import,
        CancellationToken cancellationToken);
}

/// <summary>Process-local ledger. The command key is the actor plus the original intent id.</summary>
internal sealed class MemoryServerJobArtifactImportLedger : IServerJobArtifactImportLedger
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Fingerprint, ServerJobArtifactImportReceipt Receipt)> _entries = new(StringComparer.Ordinal);

    public async Task<ServerJobArtifactImportReceipt> GetOrImportAsync(
        string actorId,
        string intentId,
        string fingerprint,
        Func<CancellationToken, Task<ServerJobArtifactImportReceipt>> import,
        CancellationToken cancellationToken)
    {
        var key = actorId + "\n" + intentId;
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new GeoprocessingIdempotencyConflictException(intentId);
                }

                return existing.Receipt;
            }

            var receipt = await import(cancellationToken).ConfigureAwait(false);
            _entries[key] = (fingerprint, receipt);
            return receipt;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Imports a completed built-in job artifact through <see cref="IFileImportService"/>
/// using the server-held artifact reference. A caller path, URL, or inline geometry is not a source.
/// </summary>
internal static class ServerJobArtifactImporter
{
    internal const long MaximumInlineBytes = 16L * 1024 * 1024;
    internal const string ImportFileName = "job-artifact.geojson";

    internal static async Task<ServerJobArtifactImportReceipt> ImportAsync(
        ServerJobArtifactImportCommand command,
        ClaimsPrincipal principal,
        IGeoprocessingJobService jobs,
        IFileImportService imports,
        IServerJobArtifactImportLedger ledger,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(services);
        ValidateCommand(command);
        var fingerprint = Fingerprint(command);
        return await ledger.GetOrImportAsync(
            command.ActorId,
            command.IntentId,
            fingerprint,
            token => ImportNewAsync(command, principal, jobs, imports, services, token),
            cancellationToken).ConfigureAwait(false);
    }

    internal static int ResolveSourceSrid(string crsState, int? producerSrid, int? requested)
    {
        if (crsState == "known" && producerSrid is > 0)
        {
            if (requested is not null && requested != producerSrid)
            {
                throw new GeoprocessingValidationException(
                    "The source SRID conflicts with the verified producer output. Select a target SRID to transform it.");
            }

            return producerSrid.Value;
        }

        if (crsState == "unknown" && producerSrid is null && requested is > 0)
        {
            return requested.Value;
        }

        throw new GeoprocessingValidationException(
            "This output has no verified producer CRS. Explicitly choose its source SRID before importing.");
    }

    private static async Task<ServerJobArtifactImportReceipt> ImportNewAsync(
        ServerJobArtifactImportCommand command,
        ClaimsPrincipal principal,
        IGeoprocessingJobService jobs,
        IFileImportService imports,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var authorized = await ReadAuthorizedAsync(
            command, principal, jobs, services, imports.Limits.MaxMemoryBytes, cancellationToken).ConfigureAwait(false);
        await using (authorized.Content.ConfigureAwait(false))
        {
            var latest = await ReadJobAsync(command.Reference.JobId, principal, jobs, cancellationToken).ConfigureAwait(false);
            if (!SameBinding(latest, authorized.Job, authorized.RawReference))
            {
                throw new GeoprocessingPreconditionFailedException(
                    "The job artifact changed before import. Read the original result again.");
            }

            var sourceSrid = ResolveSourceSrid(
                authorized.Summary.SpatialReference.State,
                authorized.Summary.SpatialReference.Srid,
                command.SourceSrid);
            authorized.Content.Position = 0;
            var result = await imports.ImportFileAsync(
                new ImportRequest
                {
                    FileStream = authorized.Content,
                    FileName = ImportFileName,
                    TableName = command.Table,
                    TargetSchema = command.Schema,
                    SourceKind = DatasetImportSourceKinds.ServerJobArtifact,
                    SourceSrid = sourceSrid,
                    TargetSrid = command.TargetSrid,
                    OverwriteExisting = false,
                    LoadMode = ImportLoadMode.Append,
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new GeoprocessingPreconditionFailedException(
                    result.ErrorMessage ?? "The job artifact could not be imported.");
            }

            return new ServerJobArtifactImportReceipt(
                command.IntentId,
                result.Schema ?? command.Schema,
                result.PhysicalTableName ?? result.TableName,
                result.PhysicalTableName ?? result.TableName,
                result.FeatureCount,
                sourceSrid,
                command.TargetSrid,
                authorized.Summary.Sha256,
                authorized.Summary.SpatialReference.State);
        }
    }

    private static async Task<AuthorizedContent> ReadAuthorizedAsync(
        ServerJobArtifactImportCommand command,
        ClaimsPrincipal principal,
        IGeoprocessingJobService jobs,
        IServiceProvider services,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var reference = command.Reference;
        var job = await ReadJobAsync(reference.JobId, principal, jobs, cancellationToken).ConfigureAwait(false);
        if (job.Status != ExecutionJobStatus.Succeeded || job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            throw new GeoprocessingPreconditionFailedException(
                $"Job '{reference.JobId}' did not complete successfully; its artifact cannot be imported.");
        }

        if (job.Version <= 0 || $"{job.OperationId}:v{job.Version}" != reference.ResultPackageId)
        {
            throw new GeoprocessingNotFoundException(
                $"Result package '{reference.ResultPackageId}' does not match the completed job.");
        }

        if (!TryParseArtifactOrdinal(reference, out var ordinal)
            || ordinal > job.ArtifactReferences.Count)
        {
            throw new GeoprocessingNotFoundException(
                $"Artifact '{reference.ArtifactId}' was not found on the completed job.");
        }

        var raw = job.ArtifactReferences[ordinal - 1];
        if (!SupportsImportEnvelope(raw))
        {
            throw new GeoprocessingValidationException(
                "The job artifact is not an inline feature document or a verified shared feature stream. URIs and filesystem paths are not import sources.");
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        if (!string.Equals(digest, reference.ReferenceSha256, StringComparison.Ordinal))
        {
            throw new GeoprocessingPreconditionFailedException(
                "The artifact reference does not match the completed job output.");
        }

        var stream = new FileStream(
            Path.Combine(Path.GetTempPath(), "honua-job-artifact-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            65536,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var summary = await GeoprocessingImportArtifactWriter.WriteAsync(
                raw,
                job,
                stream,
                services.GetService<IGeoprocessingFeatureStreamReader>(),
                maximumBytes,
                MaximumInlineBytes,
                cancellationToken).ConfigureAwait(false);
            if (summary.SpatialReference.State == "mixed" || summary.FeatureCount <= 0)
            {
                throw new GeoprocessingValidationException(
                    "A nonempty output with one coordinate frame is required. Mixed coordinate frames cannot be imported together.");
            }

            return new AuthorizedContent(job, raw, stream, summary);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool SameBinding(ExecutionJobRecord latest, ExecutionJobRecord original, string rawReference)
        => latest.OperationId == original.OperationId
            && latest.Version == original.Version
            && latest.Status == ExecutionJobStatus.Succeeded
            && latest.ArtifactReferences.Contains(rawReference, StringComparer.Ordinal);

    private static async Task<ExecutionJobRecord> ReadJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        IGeoprocessingJobService jobs,
        CancellationToken cancellationToken)
        => await jobs.GetJobAsync(jobId, principal, cancellationToken).ConfigureAwait(false);

    internal static bool SupportsImportEnvelope(string? reference)
        => !string.IsNullOrWhiteSpace(reference)
            && (reference.StartsWith(FeatureCollectionArtifact.DataUriPrefix, StringComparison.Ordinal)
                    && reference.Length <= FeatureCollectionArtifact.DataUriPrefix.Length + (MaximumInlineBytes + 2) / 3 * 4
                || DurableFeatureStream.TryParse(reference, out _));

    private static bool TryParseArtifactOrdinal(DatasetImportServerJobArtifactReference reference, out int ordinal)
    {
        ordinal = 0;
        var prefix = reference.JobId + ":artifact:";
        if (!reference.ArtifactId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = reference.ArtifactId.AsSpan(prefix.Length);
        if (suffix.Length == 0 || suffix.Length > 9 || suffix[0] == '0')
        {
            return false;
        }

        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) && ordinal > 0;
    }

    private static void ValidateCommand(ServerJobArtifactImportCommand command)
    {
        RequireToken(command.IntentId, "intentId");
        RequireToken(command.ActorId, "actor");
        RequireToken(command.Reference.TargetId, "targetId");
        RequireJobId(command.Reference.JobId);
        if (command.Reference.ResultPackageId != command.Reference.JobId + ":v" + PositiveSuffix(command.Reference.ResultPackageId, command.Reference.JobId + ":v"))
        {
            throw new GeoprocessingValidationException("resultPackageId must be the job id plus a positive version.");
        }

        _ = TryParseArtifactOrdinal(command.Reference, out var ordinal);
        if (ordinal <= 0)
        {
            throw new GeoprocessingValidationException("artifactId must be the job id plus a positive artifact ordinal.");
        }

        if (command.Reference.ReferenceSha256 is not { Length: 64 }
            || command.Reference.ReferenceSha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new GeoprocessingValidationException("referenceSha256 must be a lowercase SHA-256 digest of the server artifact reference.");
        }

        RequireIdentifier(command.Schema, "schema");
        RequireIdentifier(command.Table, "table");
        if (command.TargetSrid <= 0)
        {
            throw new GeoprocessingValidationException("targetSrid must be a positive SRID.");
        }

        if (command.SourceSrid is <= 0)
        {
            throw new GeoprocessingValidationException("sourceSrid must be a positive SRID when it is set.");
        }
    }

    private static string PositiveSuffix(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new GeoprocessingValidationException("resultPackageId must be the job id plus a positive version.");
        }

        var suffix = value.AsSpan(prefix.Length);
        if (suffix.Length == 0 || suffix.Length > 9 || suffix[0] == '0'
            || !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version <= 0)
        {
            throw new GeoprocessingValidationException("resultPackageId must be the job id plus a positive version.");
        }

        return version.ToString(CultureInfo.InvariantCulture);
    }

    private static void RequireJobId(string jobId)
    {
        if (jobId.Length is < 1 or > 128
            || !jobId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new GeoprocessingValidationException("jobId must be a canonical job identity, not a URI or path.");
        }
    }

    private static void RequireToken(string value, string name)
    {
        if (value.Length is < 1 or > 128
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Any(static character => char.IsControl(character) || !char.IsAscii(character)))
        {
            throw new GeoprocessingValidationException($"{name} must be a bounded canonical identity.");
        }
    }

    private static void RequireIdentifier(string value, string name)
    {
        if (value.Length is < 1 or > 63
            || !char.IsAsciiLetter(value[0]) && value[0] != '_'
            || value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new GeoprocessingValidationException($"{name} must be a SQL identifier, not a path.");
        }
    }

    private static string Fingerprint(ServerJobArtifactImportCommand command)
    {
        var text = string.Join(
            "\n",
            command.Reference.TargetId,
            command.Reference.JobId,
            command.Reference.ResultPackageId,
            command.Reference.ArtifactId,
            command.Reference.ReferenceSha256,
            command.Schema,
            command.Table,
            command.TargetSrid.ToString(CultureInfo.InvariantCulture),
            command.SourceSrid?.ToString(CultureInfo.InvariantCulture) ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private sealed class AuthorizedContent(
        ExecutionJobRecord job,
        string rawReference,
        FileStream content,
        GeoprocessingImportContentSummary summary)
    {
        internal ExecutionJobRecord Job { get; } = job;
        internal string RawReference { get; } = rawReference;
        internal FileStream Content { get; } = content;
        internal GeoprocessingImportContentSummary Summary { get; } = summary;
    }
}
