// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Services;

/// <summary>
/// Default checkpoint resolver (slices 2-5 of honua-server#1166). Resolves the canonical generation
/// cursor natively and delegates timestamp/transaction/release/job/edit-session/named checkpoints to the
/// provider-backed <see cref="ITemporalHistoryStore"/>, which maps them onto the change log's recorded
/// dimensions. Every checkpoint is collapsed to a concrete generation so all temporal surfaces share the
/// slice-1 change-log foundation.
/// </summary>
public sealed class TemporalCheckpointResolver : ITemporalCheckpointResolver
{
    private readonly ITemporalHistoryStore _historyStore;

    /// <summary>Creates the resolver.</summary>
    /// <param name="historyStore">Provider-backed history store used to resolve non-generation checkpoints.</param>
    public TemporalCheckpointResolver(ITemporalHistoryStore historyStore)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
    }

    /// <inheritdoc />
    public async Task<ResolvedTemporalCheckpoint> ResolveAsync(
        int storageLayerId,
        TemporalCheckpoint checkpoint,
        long currentGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (checkpoint.Kind == TemporalCheckpointKind.Generation)
        {
            var generation = ResolveGenerationValue(checkpoint, currentGeneration);
            return new ResolvedTemporalCheckpoint(checkpoint, generation);
        }

        if (string.IsNullOrWhiteSpace(checkpoint.Value))
        {
            throw new TemporalValidationException(
                $"A {checkpoint.Kind} checkpoint requires a value.");
        }

        var resolved = await _historyStore
            .ResolveCheckpointGenerationAsync(storageLayerId, checkpoint.Kind, checkpoint.Value, currentGeneration, cancellationToken)
            .ConfigureAwait(false);

        if (resolved is not { } resolvedGeneration)
        {
            throw new TemporalValidationException(
                $"The {checkpoint.Kind} checkpoint '{checkpoint.Value}' did not resolve to a point in this layer's history.");
        }

        return new ResolvedTemporalCheckpoint(checkpoint, Math.Min(resolvedGeneration, currentGeneration));
    }

    private static long ResolveGenerationValue(TemporalCheckpoint checkpoint, long currentGeneration)
    {
        long generation;
        if (checkpoint.Generation is { } explicitGeneration)
        {
            generation = explicitGeneration;
        }
        else if (!string.IsNullOrWhiteSpace(checkpoint.Value)
            && long.TryParse(checkpoint.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            generation = parsed;
        }
        else
        {
            throw new TemporalValidationException("A generation checkpoint requires a numeric generation value.");
        }

        if (generation < 0)
        {
            throw new TemporalValidationException("Generation must be non-negative.");
        }

        return Math.Min(generation, currentGeneration);
    }
}
