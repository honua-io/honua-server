// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing.CustomCode;

/// <summary>Bounded ingestion of immutable broker journals into canonical job events.</summary>
internal sealed class CustomCodeArtifactIngestor(IServiceProvider services) : ICustomCodeArtifactIngestor
{
    public async Task IngestAsync(ExecutionJobRecord job, CustomCodeArtifactDescriptor artifact, CancellationToken cancellationToken)
    {
        if (artifact.Name != HarnessEvents.FileName)
        {
            return;
        }
        if (artifact.State != "available" || artifact.JobId != job.OperationId || artifact.Attempt != job.AttemptCount ||
            artifact.SizeBytes is < 0 or > 1048576 || string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            throw new InvalidOperationException("The harness journal receipt does not identify this admitted attempt.");
        }
        var storage = services.GetRequiredService<ICloudFileStorage>();
        if (storage.Provider != artifact.Provider)
        {
            throw new InvalidOperationException("The journal storage provider no longer matches its receipt.");
        }
        var logs = services.GetRequiredService<IExecutionLogStore>();
        var jobs = services.GetRequiredService<IExecutionJobStore>();
        var current = await jobs.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.AttemptCount != artifact.Attempt ||
            !current.ArtifactReferences.Contains(artifact.Serialize(), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The harness journal is not a durable artifact of this attempt.");
        }
        await using var stream = await storage.DownloadAsync(artifact.ObjectKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The harness journal object is unavailable.");
        var bytes = new byte[checked((int)artifact.SizeBytes + 1)];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (length != artifact.SizeBytes ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, length))), artifact.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The harness journal object does not match its immutable receipt.");
        }
        var attempt = artifact.Attempt.ToString(CultureInfo.InvariantCulture);
        // Parse incrementally: a 1 MiB journal of tiny lines must not retain hundreds
        // of thousands of event objects before the bounded projection runs.
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes, 0, length));
        IEnumerable<HarnessEvent> BoundEvents()
        {
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in HarnessEvents.Parse([line]))
                {
                    // Identity comes from the authenticated broker receipt, never the journal.
                    yield return entry with { Attempt = attempt };
                }
            }
        }
        var entries = HarnessEvents.ProjectExecutionLogEntries(BoundEvents(), job.UpdatedAt);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // One immutable journal is allowed per attempt. Its position is stable even when a
            // malformed line becomes a text event or the projection adds a truncation record.
            var eventId = $"harness:{attempt}:{artifact.Sha256}:{index.ToString(CultureInfo.InvariantCulture)}";
            var metadata = new Dictionary<string, string>(entries[index].Metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                [HarnessEvents.AttemptKey] = attempt
            };
            await logs.AppendOnceAsync(job.OperationId, eventId, entries[index] with { Metadata = metadata }, cancellationToken).ConfigureAwait(false);
        }
    }
}
