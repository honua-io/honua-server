// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Migrations;

/// <summary>
/// A point-in-time view of the binary versions running across the live serving cluster, used by the
/// migration runner to decide whether a contract-phase (schema-narrowing) migration is safe to apply.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0060 expand/contract requires two adjacent server versions to coexist over one schema during a
/// rolling upgrade. The classifier and the journal-scoped gate (#2565) are authoring-time and
/// single-node signals; neither knows how many nodes are actually running or at which version. This
/// snapshot supplies the missing <em>live</em> signal so contract DDL never applies at the first
/// upgraded node's boot while N−1 old nodes still serve (#2812).
/// </para>
/// <para>
/// The snapshot is deliberately conservative: it reports only <see cref="OtherActiveVersions"/> that a
/// coordinated inventory could positively observe. Nodes whose version is unknown (for example, older
/// binaries from before this feature shipped that never advertised a version) are excluded rather than
/// treated as skew, so the barrier never produces a false positive that would wedge a routine upgrade.
/// </para>
/// </remarks>
public sealed record ActiveNodeVersionSnapshot
{
    /// <summary>
    /// Whether a live, multi-node inventory was actually consulted. <see langword="false"/> means no
    /// coordination backend was available (single-node/dev-compose, or Redis unreachable), in which
    /// case the barrier stays inert and boot proceeds exactly as today with zero new required config.
    /// </summary>
    public required bool Coordinated { get; init; }

    /// <summary>
    /// The binary version of the node performing the migration, or <see langword="null"/> when it
    /// cannot be determined.
    /// </summary>
    public string? LocalVersion { get; init; }

    /// <summary>
    /// The positively-observed binary versions of <em>other</em> live serving nodes (excluding this
    /// node and any node whose version is unknown). Empty when this is the only node or when no other
    /// node advertised a version.
    /// </summary>
    public IReadOnlyList<string> OtherActiveVersions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// <see langword="true"/> when a coordinated inventory positively observed at least one other live
    /// node running a binary version different from this node's — the mixed-version rolling-upgrade
    /// window during which contract-phase DDL is unsafe to apply.
    /// </summary>
    public bool MixedVersionDetected =>
        Coordinated
        && !string.IsNullOrWhiteSpace(LocalVersion)
        && OtherActiveVersions.Any(version =>
            !string.IsNullOrWhiteSpace(version)
            && !string.Equals(version, LocalVersion, StringComparison.Ordinal));

    /// <summary>
    /// The distinct set of other-node versions that differ from <see cref="LocalVersion"/>, in
    /// first-seen order — the operator-facing evidence for a barrier rejection.
    /// </summary>
    public IReadOnlyList<string> DivergentVersions =>
        string.IsNullOrWhiteSpace(LocalVersion)
            ? Array.Empty<string>()
            : OtherActiveVersions
                .Where(version =>
                    !string.IsNullOrWhiteSpace(version)
                    && !string.Equals(version, LocalVersion, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// A snapshot indicating no coordinated inventory was available; the barrier stays inert.
    /// </summary>
    public static ActiveNodeVersionSnapshot NotCoordinated { get; } = new() { Coordinated = false };
}

/// <summary>
/// Supplies the migration runner with a live view of the binary versions running across the serving
/// cluster so contract-phase migrations can consult a real min-active-binary-version signal rather than
/// config-declared release pins (#2812, part of ADR-0060).
/// </summary>
public interface IActiveNodeVersionInventory
{
    /// <summary>
    /// Reads a point-in-time snapshot of the live cluster's binary versions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The current cluster version snapshot. Implementations must fail open — returning
    /// <see cref="ActiveNodeVersionSnapshot.NotCoordinated"/> — when the coordination backend is
    /// unavailable, so an inventory outage never blocks an otherwise-safe migration.
    /// </returns>
    Task<ActiveNodeVersionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IActiveNodeVersionInventory"/> used when no coordination backend is wired: always
/// reports no coordination, keeping the mixed-version barrier inert for single-node and dev deployments.
/// </summary>
public sealed class NullActiveNodeVersionInventory : IActiveNodeVersionInventory
{
    /// <summary>
    /// A shared stateless instance.
    /// </summary>
    public static NullActiveNodeVersionInventory Instance { get; } = new();

    /// <inheritdoc />
    public Task<ActiveNodeVersionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ActiveNodeVersionSnapshot.NotCoordinated);
}
