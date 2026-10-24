// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Geoprocessing.CustomCode;

/// <summary>Credential-free worker output transport, fenced to one live job attempt.</summary>
internal static class CustomCodeArtifactEndpoints
{
    internal const long MaximumArtifactBytes = CustomCodeArtifactQuota.MaximumArtifactBytes;
    internal const long MaximumJobBytes = CustomCodeArtifactQuota.MaximumJobBytes;
    internal const long MaximumJournalBytes = CustomCodeArtifactQuota.MaximumJournalBytes;
    internal const int MaximumArtifacts = CustomCodeArtifactQuota.MaximumArtifacts;
    private const int MaximumCasAttempts = 8;
    private const int MaximumUploadAttempts = 3;

    public static void MapCustomCodeArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}", UploadAsync)
            // UploadAsync validates the dedicated callback credential and current job
            // attempt before reading bytes; ordinary user/admin auth cannot grant upload.
            .AllowAnonymous()
            .WithTags("Geoprocessing")
            .WithName("UploadCustomCodeArtifact")
            .WithSummary("Upload an immutable output using a job and attempt bound callback credential")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}", DownloadAsync)
            .WithTags("Geoprocessing")
            .WithName("DownloadCustomCodeArtifact")
            .WithSummary("Download an authorized immutable custom-code output")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> UploadAsync(
        string jobId, string name, [FromQuery] int attempt, HttpContext context,
        [FromServices] IScopedJobTokenIssuer issuer)
    {
        context.Response.Headers.CacheControl = "no-store";
        var credential = context.Request.Headers["X-Api-Key"];
        if (credential.Count != 1 || string.IsNullOrWhiteSpace(credential[0]))
        {
            return Problem(context, 401, "A job-bound callback credential is required.");
        }

        var session = await issuer.ValidateAsync(credential[0]!, jobId, context.RequestAborted).ConfigureAwait(false);
        if (session is null || session.Attempt != attempt)
        {
            return Problem(context, 401, "The callback credential does not authorize this job attempt.");
        }

        if (!SafeSegment(jobId) || !SafeSegment(name) || attempt < 1)
        {
            return Problem(context, 400, "Job and artifact names must be bounded single path segments and attempt must be positive.");
        }

        var journal = string.Equals(name, HarnessEvents.FileName, StringComparison.Ordinal);
        var maximum = journal ? MaximumJournalBytes : MaximumArtifactBytes;
        if (context.Request.ContentLength is not long length || length < 0 || length > maximum)
        {
            return Problem(context, 413, "Content-Length is required and must fit the artifact byte limit.");
        }

        var jobs = context.RequestServices.GetService<IExecutionJobStore>();
        var storage = context.RequestServices.GetService<ICloudFileStorage>();
        var ingestor = context.RequestServices.GetService<ICustomCodeArtifactIngestor>();
        if (jobs is null || storage is null || storage.Provider is not (CloudStorageProvider.AwsS3 or CloudStorageProvider.AzureBlob)
            || (journal && ingestor is null))
        {
            return Problem(context, 503, "Durable artifact storage and journal ingestion must be configured.");
        }

        var artifact = new CustomCodeArtifactDescriptor
        {
            JobId = jobId,
            Attempt = attempt,
            Name = name,
            ObjectKey = $"gp/custom-artifacts/{jobId}/a{attempt}/{name}",
            ContentType = "application/octet-stream",
            SizeBytes = length,
            Provider = storage.Provider,
        };
        ExecutionJobRecord? reservedJob = null;
        for (var retry = 0; retry < MaximumCasAttempts; retry++)
        {
            var job = await jobs.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false);
            if (!LiveAttempt(job, attempt, credential[0]!))
            {
                return Problem(context, 409, "This attempt is no longer accepting artifacts.");
            }

            var receipts = Receipts(job!);
            var existing = receipts.FirstOrDefault(item => item.Attempt == attempt && item.Name == name);
            if (existing is not null)
            {
                // A completed journal may be retried only to finish idempotent ingestion.
                // Its bytes are immutable and never replaced by the retried request body.
                if (journal && existing.State == "available")
                {
                    await ingestor!.IngestAsync(job!, existing, context.RequestAborted).ConfigureAwait(false);
                    return Results.StatusCode(StatusCodes.Status200OK);
                }

                if (existing.State == "available" && existing.SizeBytes == length
                    && await MatchesReceiptAsync(context.Request.Body, existing, context.RequestAborted).ConfigureAwait(false))
                {
                    return Results.StatusCode(StatusCodes.Status200OK);
                }

                if (existing.State != "pending" || existing.SizeBytes != length
                    || existing.Provider != storage.Provider || existing.UploadAttempts >= MaximumUploadAttempts)
                {
                    return Problem(context, 409, "Artifact names are immutable and upload recovery attempts are bounded.");
                }

                artifact = existing with { UploadAttempts = existing.UploadAttempts + 1 };
                var recovery = job! with
                {
                    ArtifactReferences = job!.ArtifactReferences.Select(reference =>
                        CustomCodeArtifactDescriptor.TryParse(reference, out var item)
                        && item!.Attempt == attempt && item.Name == name ? artifact.Serialize() : reference).ToArray(),
                };
                if (await jobs.TrySetAsync(recovery, cancellationToken: context.RequestAborted).ConfigureAwait(false))
                {
                    reservedJob = recovery;
                    break;
                }

                continue;
            }

            // Reserve the required journal inside the existing total caps so hostile
            // user output cannot consume its final slot or byte allowance.
            var reserveJournal = !journal && !receipts.Any(item => item.Attempt == attempt && item.Name == HarnessEvents.FileName);
            var availableCount = MaximumArtifacts - (reserveJournal ? 1 : 0);
            var availableBytes = MaximumJobBytes - (reserveJournal ? MaximumJournalBytes : 0);
            if (receipts.Count >= availableCount || receipts.Sum(item => item.SizeBytes) + length > availableBytes)
            {
                return Problem(context, 413, "The job artifact count or aggregate byte limit has been reached; journal capacity is reserved.");
            }

            var updated = job! with { ArtifactReferences = [.. job!.ArtifactReferences, artifact.Serialize()] };
            if (await jobs.TrySetAsync(updated, cancellationToken: context.RequestAborted).ConfigureAwait(false))
            {
                reservedJob = updated;
                break;
            }
        }

        if (reservedJob is null)
        {
            return Problem(context, 409, "Concurrent job changes prevented an artifact reservation; retry later.");
        }

        // Bound disk use before the cloud upload. Reservations stay charged on any failure,
        // so repeated interrupted requests cannot bypass the per-job byte and count caps.
        await using var spool = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long readTotal = 0;
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            readTotal += read;
            if (readTotal > length)
            {
                return Problem(context, 413, "The body exceeds its reserved byte length.");
            }

            hash.AppendData(buffer, 0, read);
            await spool.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
        }

        if (readTotal != length)
        {
            return Problem(context, 400, "The body does not match its declared byte length.");
        }

        artifact = artifact with { Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()), State = "available" };
        spool.Position = 0;
        var beforeUpload = await jobs.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false);
        if (!LiveAttempt(beforeUpload, attempt, credential[0]!))
        {
            return Problem(context, 409, "The job attempt ended during upload.");
        }

        var uploaded = await storage.UploadIfMatchAsync(new FileUploadRequest
        {
            Content = spool,
            FileName = name,
            ContentType = artifact.ContentType,
            SizeBytes = length,
            ObjectKeyOverride = artifact.ObjectKey,
            TimeToLive = TimeSpan.FromDays(7),
            EnableChunkedUpload = false,
            Metadata = ImmutableDictionary<string, string>.Empty.Add("sha256", artifact.Sha256!),
        }, expectedETag: null, context.RequestAborted).ConfigureAwait(false);
        if (!uploaded.Success)
        {
            // A previous write may have committed before its acknowledgement was lost.
            // Recover only the exact bytes this request supplied, never replace an object.
            await using var stored = await storage.DownloadAsync(artifact.ObjectKey, context.RequestAborted).ConfigureAwait(false);
            if (stored is null)
            {
                return Problem(context, 503, "The immutable artifact could not be stored; its bounded reservation can be retried.");
            }

            if (!await MatchesReceiptAsync(stored, artifact, context.RequestAborted).ConfigureAwait(false))
            {
                return Problem(context, 409, "Existing immutable artifact bytes differ from this upload.");
            }
        }

        for (var retry = 0; retry < MaximumCasAttempts; retry++)
        {
            var job = await jobs.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false);
            if (!LiveAttempt(job, attempt, credential[0]!))
            {
                break;
            }

            var receipt = Receipts(job!).FirstOrDefault(item => item.Attempt == attempt && item.Name == name);
            if (receipt is null)
            {
                break;
            }

            if (receipt.State == "available")
            {
                if (receipt.Sha256 != artifact.Sha256 || receipt.SizeBytes != artifact.SizeBytes)
                {
                    return Problem(context, 409, "Existing immutable artifact bytes differ from this upload.");
                }

                if (journal)
                {
                    await ingestor!.IngestAsync(job!, receipt, context.RequestAborted).ConfigureAwait(false);
                }

                return Results.StatusCode(StatusCodes.Status200OK);
            }

            artifact = artifact with { UploadAttempts = receipt.UploadAttempts };
            var updated = job! with
            {
                ArtifactReferences = job!.ArtifactReferences.Select(reference =>
                    CustomCodeArtifactDescriptor.TryParse(reference, out var item)
                    && item!.Attempt == attempt && item.Name == name ? artifact.Serialize() : reference).ToArray(),
            };
            if (await jobs.TrySetAsync(updated, cancellationToken: context.RequestAborted).ConfigureAwait(false))
            {
                if (journal)
                {
                    await ingestor!.IngestAsync(updated, artifact, context.RequestAborted).ConfigureAwait(false);
                }

                context.Response.Headers.Location = artifact.ContentUrl;
                return Results.StatusCode(StatusCodes.Status201Created);
            }
        }

        // A lost attempt cannot publish its object into a later attempt's artifact list.
        // The cloud TTL reclaims any orphan if compensation cannot complete.
        var latest = await jobs.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (!LiveAttempt(latest, attempt, credential[0]!)
            && (latest is null || !Receipts(latest).Any(receipt => receipt.ObjectKey == artifact.ObjectKey && receipt.State == "available"))
            && uploaded.File?.ETag is string etag)
        {
            await storage.DeleteIfMatchAsync(artifact.ObjectKey, etag, CancellationToken.None).ConfigureAwait(false);
        }

        return Problem(context, 409, "The job attempt changed before artifact publication.");
    }

    private static async Task<IResult> DownloadAsync(
        string jobId, string name, [FromQuery] int attempt, HttpContext context,
        [FromServices] IGeoprocessingJobService jobService)
    {
        context.Response.Headers.CacheControl = "no-store";
        ExecutionJobRecord job;
        try
        {
            job = await jobService.GetJobAsync(jobId, context.User, context.RequestAborted).ConfigureAwait(false);
        }
        catch (GeoprocessingAuthorizationException exception)
        {
            return Problem(context, exception.RequiresAuthentication ? 401 : 403, exception.Message);
        }
        catch (GeoprocessingNotFoundException)
        {
            return Problem(context, 404, "Job not found.");
        }
        catch (GeoprocessingStoreUnavailableException exception)
        {
            return GeoprocessingProblemDetailsHelpers.StoreUnavailable(context, exception);
        }

        var artifact = Receipts(job).FirstOrDefault(item => item.Name == name && item.Attempt == attempt
            && item.JobId == jobId && item.State == "available");
        if (artifact is null || !SafeSegment(jobId) || !SafeSegment(name)
            || artifact.ObjectKey != $"gp/custom-artifacts/{jobId}/a{attempt}/{name}")
        {
            return Problem(context, 404, "Artifact not found.");
        }

        var storage = context.RequestServices.GetService<ICloudFileStorage>();
        if (storage is null || storage.Provider != artifact.Provider)
        {
            return Problem(context, 503, "The artifact's durable cloud storage is unavailable.");
        }

        var file = await storage.GetMetadataAsync(artifact.ObjectKey, context.RequestAborted).ConfigureAwait(false);
        if (file is null || file.ExpiresAt <= DateTimeOffset.UtcNow || file.SizeBytes != artifact.SizeBytes)
        {
            return Problem(context, 404, "Artifact expired or unavailable.");
        }

        var stream = await storage.DownloadAsync(artifact.ObjectKey, context.RequestAborted).ConfigureAwait(false);
        if (stream is null)
        {
            return Problem(context, 404, "Artifact expired or unavailable.");
        }

        // Verify the stored content before returning any bytes. A changed bucket/account
        // under the same provider must not serve unrelated content with the original ETag.
        await using var cloudStream = stream;
        var verified = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        var responseOwnsStream = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await cloudStream.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > artifact.SizeBytes || total > MaximumArtifactBytes)
                {
                    return Problem(context, 409, "Artifact content does not match its immutable receipt.");
                }

                hash.AppendData(buffer, 0, read);
                await verified.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
            }

            if (total != artifact.SizeBytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), artifact.Sha256, StringComparison.Ordinal))
            {
                return Problem(context, 409, "Artifact content does not match its immutable receipt.");
            }

            verified.Position = 0;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var result = Results.Stream(verified, "application/octet-stream", name,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue('"' + artifact.Sha256 + '"'));
            responseOwnsStream = true;
            return result;
        }
        finally
        {
            if (!responseOwnsStream)
            {
                await verified.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> MatchesReceiptAsync(Stream stream, CustomCodeArtifactDescriptor artifact, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > artifact.SizeBytes || total > MaximumArtifactBytes)
            {
                return false;
            }

            hash.AppendData(buffer, 0, read);
        }

        return total == artifact.SizeBytes
            && string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), artifact.Sha256, StringComparison.Ordinal);
    }

    private static bool LiveAttempt(ExecutionJobRecord? job, int attempt, string credential)
        => job is not null && job.Spec.Kind == ExecutionJobKind.Geoprocessing
            && job.Spec.RuntimeProfile == CustomCodeJobContract.RuntimeProfile
            && job.AttemptCount == attempt && job.Status == ExecutionJobStatus.Running
            && job.Spec.Parameters.TryGetValue(CustomCodeJobContract.JobTokenEnvParam, out var currentToken)
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(currentToken)), SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private static List<CustomCodeArtifactDescriptor> Receipts(ExecutionJobRecord job)
        => job.ArtifactReferences.Select(reference => CustomCodeArtifactDescriptor.TryParse(reference, out var artifact) ? artifact : null)
            .OfType<CustomCodeArtifactDescriptor>().ToList();

    private static bool SafeSegment(string value)
        => value.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(value[0])
            && !value.Contains("..", StringComparison.Ordinal)
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static IResult Problem(HttpContext context, int status, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, status, ProblemDetailsHelpers.GetTitle(status), detail);
}
