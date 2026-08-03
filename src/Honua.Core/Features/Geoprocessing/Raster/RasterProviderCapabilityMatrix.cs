// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>How far a provider primitive is wired into Honua's online serving path.</summary>
public enum RasterServingPrimitiveStatus
{
    /// <summary>The allowlisted primitive is composed by an existing Honua serving path.</summary>
    HonuaServingPath,

    /// <summary>The provider offers the primitive, but Honua has not modeled this serving semantic.</summary>
    ProviderLibraryOnly,

    /// <summary>No suitable provider primitive has been identified for this semantic variant.</summary>
    Unavailable,
}

/// <summary>Minimum provider-extension version required by one semantic variant.</summary>
public sealed record RasterProviderExtensionRequirement
{
    /// <summary>Stable provider extension identifier.</summary>
    public required string ExtensionName { get; init; }

    /// <summary>Minimum normalized numeric extension version.</summary>
    public required string MinimumVersion { get; init; }
}

/// <summary>Discovered provider extension and its normalized numeric version.</summary>
public sealed record RasterProviderExtensionSnapshot
{
    /// <summary>Stable provider extension identifier.</summary>
    public required string ExtensionName { get; init; }

    /// <summary>Normalized numeric extension version.</summary>
    public required string Version { get; init; }
}

/// <summary>Bounded runtime discovery input used to evaluate provider capability rows.</summary>
public sealed record RasterProviderRuntimeSnapshot
{
    /// <summary>Stable provider identity.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Raster engine exposed by the provider.</summary>
    public required RasterEngine Engine { get; init; }

    /// <summary>Normalized numeric provider runtime version.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Installed provider extensions relevant to capability admission.</summary>
    public required IReadOnlyList<RasterProviderExtensionSnapshot> Extensions { get; init; }
}

/// <summary>
/// Provider-neutral definition of one process semantic variant. Serving support is inventory
/// metadata only; it never proves that a durable GP route exists.
/// </summary>
public sealed record RasterProviderOperationCapabilityRow
{
    /// <summary>Stable provider identity.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Raster engine implemented by the provider.</summary>
    public required RasterEngine Engine { get; init; }

    /// <summary>Canonical public raster process identifier.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Engine-independent semantic contract version.</summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Canonical semantic variant identifier pinned by the planner.</summary>
    public required string SemanticVariantId { get; init; }

    /// <summary>Provider implementation version for the semantic contract.</summary>
    public required string ImplementationVersion { get; init; }

    /// <summary>Provider capability-policy version used for admission.</summary>
    public required string PolicyVersion { get; init; }

    /// <summary>Current relationship between the variant and Honua's serving implementation.</summary>
    public required RasterServingPrimitiveStatus ServingPrimitiveStatus { get; init; }

    /// <summary>Bounded allowlisted provider function names supporting the serving inventory.</summary>
    public required IReadOnlyList<string> ServingPrimitives { get; init; }

    /// <summary>Bounded human-readable distinction between serving and durable GP support.</summary>
    public required string ServingPrimitiveNotes { get; init; }

    /// <summary>Minimum normalized numeric provider runtime version.</summary>
    public required string MinimumRuntimeVersion { get; init; }

    /// <summary>Provider extensions required for this exact semantic variant.</summary>
    public required IReadOnlyList<RasterProviderExtensionRequirement> RequiredExtensions { get; init; }

    /// <summary>
    /// Stable semantic-oracle fixtures that require passing executable provider proof. An empty
    /// collection is an explicit semantic-proof gap and keeps the row unavailable.
    /// </summary>
    public required IReadOnlyList<string> RequiredFixtureIds { get; init; }
}

/// <summary>
/// Registration tying one semantic variant to an actual RAST-010 provider executor. The executor
/// contract can return immutable references only, so this cannot attest a <c>byte[]</c> payload path.
/// </summary>
public sealed record RasterProviderExecutableSemanticVariant
{
    /// <summary>Actual durable executor registered for the provider route.</summary>
    public required IRasterProviderExecutor Executor { get; init; }

    /// <summary>Exact route capability declared by <see cref="Executor"/>.</summary>
    public required RasterProviderCapability Capability { get; init; }

    /// <summary>Exact semantic variant implemented by the durable route.</summary>
    public required string SemanticVariantId { get; init; }
}

/// <summary>Executable provider-proof receipt for one exact semantic fixture and runtime.</summary>
public sealed record RasterProviderSemanticProof
{
    /// <summary>Stable provider identity.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Raster engine exercised by the proof.</summary>
    public required RasterEngine Engine { get; init; }

    /// <summary>Canonical public raster process identifier.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Engine-independent semantic contract version.</summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Canonical semantic variant exercised by the proof.</summary>
    public required string SemanticVariantId { get; init; }

    /// <summary>Provider implementation version exercised by the proof.</summary>
    public required string ImplementationVersion { get; init; }

    /// <summary>Provider capability-policy version exercised by the proof.</summary>
    public required string PolicyVersion { get; init; }

    /// <summary>Stable semantic-oracle fixture identifier executed by the provider runner.</summary>
    public required string FixtureId { get; init; }

    /// <summary>Exact normalized numeric provider runtime version exercised by the proof.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Whether the executable provider runner passed the fixture without widened tolerance.</summary>
    public required bool Passed { get; init; }
}

/// <summary>Stable fail-closed discovery rejection codes. These are not metric dimensions.</summary>
public static class RasterProviderCapabilityRejectionCodes
{
    /// <summary>No provider primitive supports the semantic variant.</summary>
    public const string ServingPrimitiveUnavailable = "serving_primitive_unavailable";

    /// <summary>No semantic fixture has been assigned to the variant.</summary>
    public const string SemanticFixtureUnassigned = "semantic_fixture_unassigned";

    /// <summary>No runtime snapshot exists for the provider.</summary>
    public const string ProviderRuntimeUndiscovered = "provider_runtime_undiscovered";

    /// <summary>The provider runtime version is not normalized numeric data.</summary>
    public const string ProviderRuntimeVersionInvalid = "provider_runtime_version_invalid";

    /// <summary>The provider runtime is older than the row's supported baseline.</summary>
    public const string ProviderRuntimeBelowMinimum = "provider_runtime_below_minimum";

    /// <summary>A required provider extension was not discovered.</summary>
    public const string ProviderExtensionMissing = "provider_extension_missing";

    /// <summary>A required provider extension version is not normalized numeric data.</summary>
    public const string ProviderExtensionVersionInvalid = "provider_extension_version_invalid";

    /// <summary>A required provider extension is older than the row's supported baseline.</summary>
    public const string ProviderExtensionBelowMinimum = "provider_extension_below_minimum";

    /// <summary>No actual durable reference-output executor is registered for the exact variant.</summary>
    public const string DurableReferenceExecutorMissing = "durable_reference_executor_missing";

    /// <summary>The exact durable reference-output executor route is explicitly unavailable.</summary>
    public const string DurableReferenceExecutorUnavailable = "durable_reference_executor_unavailable";

    /// <summary>No executable provider proof covers a required fixture.</summary>
    public const string ProviderProofMissing = "provider_proof_missing";

    /// <summary>A provider proof exists, but for a different runtime version.</summary>
    public const string ProviderProofRuntimeMismatch = "provider_proof_runtime_mismatch";

    /// <summary>The executable provider proof failed.</summary>
    public const string ProviderProofFailed = "provider_proof_failed";
}

/// <summary>One bounded, machine-readable reason a provider row remains unavailable.</summary>
public sealed record RasterProviderCapabilityRejection
{
    /// <summary>Stable low-cardinality rejection code.</summary>
    public required string Code { get; init; }

    /// <summary>Exact actionable discovery reason.</summary>
    public required string Reason { get; init; }
}

/// <summary>Evaluated discovery metadata for one provider operation semantic variant.</summary>
public sealed record RasterProviderCapabilityDiscovery
{
    /// <summary>Static capability definition that was evaluated.</summary>
    public required RasterProviderOperationCapabilityRow Row { get; init; }

    /// <summary>Serving-path inventory copied from <see cref="Row"/> for discovery clients.</summary>
    public RasterServingPrimitiveStatus ServingPrimitiveStatus => Row.ServingPrimitiveStatus;

    /// <summary>Whether an actual exact reference-output executor registration was found.</summary>
    public required bool HasDurableReferenceOutputExecutor { get; init; }

    /// <summary>Whether every required semantic fixture has passing exact-runtime provider proof.</summary>
    public required bool HasProviderProof { get; init; }

    /// <summary>Bounded ordered reasons the row is unavailable.</summary>
    public required IReadOnlyList<RasterProviderCapabilityRejection> Rejections { get; init; }

    /// <summary>RAST-010 provider capability projection for this exact evaluated row.</summary>
    public required RasterProviderCapability Capability { get; init; }
}

/// <summary>Fail-closed evaluator for provider operation and semantic-variant capability rows.</summary>
public static class RasterProviderCapabilityMatrix
{
    /// <summary>
    /// Evaluates static rows against provider runtime discovery, actual durable executors, and
    /// executable semantic proofs. Serving primitives alone never make a row available.
    /// </summary>
    public static IReadOnlyList<RasterProviderCapabilityDiscovery> Discover(
        IEnumerable<RasterProviderOperationCapabilityRow> rows,
        IEnumerable<RasterProviderRuntimeSnapshot> runtimes,
        IEnumerable<RasterProviderExecutableSemanticVariant> executors,
        IEnumerable<RasterProviderSemanticProof> proofs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(proofs);

        var rowArray = rows.ToArray();
        var runtimeArray = runtimes.ToArray();
        var executorArray = executors.ToArray();
        var proofArray = proofs.ToArray();
        ValidateRows(rowArray);
        ValidateRuntimes(runtimeArray);
        ValidateExecutors(executorArray);
        ValidateProofs(proofArray);

        return Array.AsReadOnly(rowArray
            .Select(row => DiscoverRow(row, runtimeArray, executorArray, proofArray))
            .ToArray());
    }

    /// <summary>
    /// Collapses semantic rows into the process-level RAST-010 provider seam. A process is available
    /// only when every declared semantic variant is available, preventing partial proof from
    /// advertising broader process semantics through an older process-level planner contract.
    /// </summary>
    public static IReadOnlyList<RasterProviderCapability> ProjectOperations(
        IEnumerable<RasterProviderCapabilityDiscovery> discoveries)
    {
        ArgumentNullException.ThrowIfNull(discoveries);
        var discoveryArray = discoveries.ToArray();
        if (discoveryArray.Any(discovery => discovery is null))
        {
            throw new InvalidOperationException("Raster provider capability discoveries cannot contain null rows.");
        }

        var projected = discoveryArray
            .GroupBy(discovery => new
            {
                discovery.Row.ProviderId,
                discovery.Row.Engine,
                discovery.Row.ProcessId,
                discovery.Row.SemanticVersion,
                discovery.Row.ImplementationVersion,
                discovery.Row.PolicyVersion,
            })
            .Select(group =>
            {
                var unavailable = group
                    .Where(discovery => discovery.Capability.Availability != RasterProviderAvailability.Available)
                    .ToArray();
                var reason = unavailable.Length == 0
                    ? null
                    : string.Join(
                        "; ",
                        unavailable.Select(discovery =>
                            $"semantic variant '{discovery.Row.SemanticVariantId}' "
                            + $"[{string.Join(',', discovery.Rejections.Select(rejection => rejection.Code))}]"));

                return new RasterProviderCapability
                {
                    ProviderId = group.Key.ProviderId,
                    Engine = group.Key.Engine,
                    Variant = new RasterSemanticVariant
                    {
                        ProcessId = group.Key.ProcessId,
                        SemanticVersion = group.Key.SemanticVersion,
                        ImplementationVersion = group.Key.ImplementationVersion,
                    },
                    PolicyVersion = group.Key.PolicyVersion,
                    Availability = unavailable.Length == 0
                        ? RasterProviderAvailability.Available
                        : RasterProviderAvailability.Unavailable,
                    UnavailabilityReason = reason,
                };
            })
            .ToArray();

        return Array.AsReadOnly(projected);
    }

    private static RasterProviderCapabilityDiscovery DiscoverRow(
        RasterProviderOperationCapabilityRow row,
        IReadOnlyList<RasterProviderRuntimeSnapshot> runtimes,
        IReadOnlyList<RasterProviderExecutableSemanticVariant> executors,
        IReadOnlyList<RasterProviderSemanticProof> proofs)
    {
        var rejections = new List<RasterProviderCapabilityRejection>();
        if (row.ServingPrimitiveStatus == RasterServingPrimitiveStatus.Unavailable)
        {
            Reject(
                rejections,
                RasterProviderCapabilityRejectionCodes.ServingPrimitiveUnavailable,
                $"No provider primitive is modeled for {Display(row)}.");
        }

        if (row.RequiredFixtureIds.Count == 0)
        {
            Reject(
                rejections,
                RasterProviderCapabilityRejectionCodes.SemanticFixtureUnassigned,
                $"No semantic-oracle fixture is assigned to {Display(row)}.");
        }

        var runtime = runtimes.SingleOrDefault(candidate =>
            candidate.Engine == row.Engine
            && string.Equals(candidate.ProviderId, row.ProviderId, StringComparison.Ordinal));
        if (runtime is null)
        {
            Reject(
                rejections,
                RasterProviderCapabilityRejectionCodes.ProviderRuntimeUndiscovered,
                $"No runtime snapshot was discovered for provider '{row.ProviderId}' and engine '{row.Engine}'.");
        }
        else
        {
            EvaluateRuntime(row, runtime, rejections);
        }

        var registrations = executors.Where(registration => Matches(row, registration)).ToArray();
        var hasExecutor = registrations.Any(registration =>
            registration.Capability.Availability == RasterProviderAvailability.Available);
        if (!hasExecutor)
        {
            var unavailable = registrations.FirstOrDefault();
            if (unavailable is null)
            {
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.DurableReferenceExecutorMissing,
                    $"No registered durable reference-output executor declares {Display(row)}.");
            }
            else
            {
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.DurableReferenceExecutorUnavailable,
                    $"The durable reference-output executor for {Display(row)} is unavailable: "
                    + $"{unavailable.Capability.UnavailabilityReason}");
            }
        }

        var hasProof = runtime is not null
            && row.RequiredFixtureIds.Count > 0
            && EvaluateProofs(row, runtime, proofs, rejections);
        var unavailableReason = rejections.Count == 0
            ? null
            : string.Join("; ", rejections.Select(rejection => $"{rejection.Code}: {rejection.Reason}"));
        var capability = new RasterProviderCapability
        {
            ProviderId = row.ProviderId,
            Engine = row.Engine,
            Variant = new RasterSemanticVariant
            {
                ProcessId = row.ProcessId,
                SemanticVersion = row.SemanticVersion,
                ImplementationVersion = row.ImplementationVersion,
            },
            PolicyVersion = row.PolicyVersion,
            Availability = rejections.Count == 0
                ? RasterProviderAvailability.Available
                : RasterProviderAvailability.Unavailable,
            UnavailabilityReason = unavailableReason,
        };

        return new RasterProviderCapabilityDiscovery
        {
            Row = row,
            HasDurableReferenceOutputExecutor = hasExecutor,
            HasProviderProof = hasProof,
            Rejections = new ReadOnlyCollection<RasterProviderCapabilityRejection>(rejections),
            Capability = capability,
        };
    }

    private static void EvaluateRuntime(
        RasterProviderOperationCapabilityRow row,
        RasterProviderRuntimeSnapshot runtime,
        ICollection<RasterProviderCapabilityRejection> rejections)
    {
        if (!Version.TryParse(runtime.RuntimeVersion, out var runtimeVersion))
        {
            Reject(
                rejections,
                RasterProviderCapabilityRejectionCodes.ProviderRuntimeVersionInvalid,
                $"Discovered provider runtime version '{runtime.RuntimeVersion}' is not normalized numeric data.");
        }
        else
        {
            var minimumVersion = Version.Parse(row.MinimumRuntimeVersion);
            if (runtimeVersion < minimumVersion)
            {
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.ProviderRuntimeBelowMinimum,
                    $"PostGIS runtime {runtime.RuntimeVersion} is below the minimum supported version "
                    + $"{row.MinimumRuntimeVersion} for {Display(row)}.");
            }
        }

        foreach (var requirement in row.RequiredExtensions)
        {
            var extension = runtime.Extensions.SingleOrDefault(candidate => string.Equals(
                candidate.ExtensionName,
                requirement.ExtensionName,
                StringComparison.Ordinal));
            if (extension is null)
            {
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.ProviderExtensionMissing,
                    $"Required provider extension '{requirement.ExtensionName}' at version "
                    + $"{requirement.MinimumVersion} or later was not discovered.");
                continue;
            }

            if (!Version.TryParse(extension.Version, out var extensionVersion))
            {
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.ProviderExtensionVersionInvalid,
                    $"Discovered extension '{requirement.ExtensionName}' version '{extension.Version}' "
                    + "is not normalized numeric data.");
                continue;
            }

            if (extensionVersion < Version.Parse(requirement.MinimumVersion))
            {
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.ProviderExtensionBelowMinimum,
                    $"Provider extension '{requirement.ExtensionName}' version {extension.Version} is below "
                    + $"the minimum supported version {requirement.MinimumVersion} for {Display(row)}.");
            }
        }
    }

    private static bool EvaluateProofs(
        RasterProviderOperationCapabilityRow row,
        RasterProviderRuntimeSnapshot runtime,
        IReadOnlyList<RasterProviderSemanticProof> proofs,
        ICollection<RasterProviderCapabilityRejection> rejections)
    {
        var allPassed = true;
        foreach (var fixtureId in row.RequiredFixtureIds)
        {
            var matchingFixture = proofs.Where(proof =>
                Matches(row, proof)
                && string.Equals(proof.FixtureId, fixtureId, StringComparison.Ordinal)).ToArray();
            if (matchingFixture.Length == 0)
            {
                allPassed = false;
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.ProviderProofMissing,
                    $"No passing provider proof covers fixture '{fixtureId}' for {Display(row)} "
                    + $"on runtime {runtime.RuntimeVersion}.");
                continue;
            }

            var matchingRuntime = matchingFixture.Where(proof => string.Equals(
                proof.RuntimeVersion,
                runtime.RuntimeVersion,
                StringComparison.Ordinal)).ToArray();
            if (matchingRuntime.Length == 0)
            {
                allPassed = false;
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.ProviderProofRuntimeMismatch,
                    $"Provider proof for fixture '{fixtureId}' does not cover discovered runtime "
                    + $"{runtime.RuntimeVersion} for {Display(row)}.");
                continue;
            }

            if (!matchingRuntime.Any(proof => proof.Passed))
            {
                allPassed = false;
                Reject(
                    rejections,
                    RasterProviderCapabilityRejectionCodes.ProviderProofFailed,
                    $"Provider proof for fixture '{fixtureId}' failed on runtime {runtime.RuntimeVersion} "
                    + $"for {Display(row)}.");
            }
        }

        return allPassed;
    }

    private static bool Matches(
        RasterProviderOperationCapabilityRow row,
        RasterProviderExecutableSemanticVariant registration)
    {
        var capability = registration.Capability;
        return capability.Engine == row.Engine
            && string.Equals(capability.ProviderId, row.ProviderId, StringComparison.Ordinal)
            && string.Equals(capability.Variant.ProcessId, row.ProcessId, StringComparison.Ordinal)
            && string.Equals(capability.Variant.SemanticVersion, row.SemanticVersion, StringComparison.Ordinal)
            && string.Equals(capability.Variant.ImplementationVersion, row.ImplementationVersion, StringComparison.Ordinal)
            && string.Equals(capability.PolicyVersion, row.PolicyVersion, StringComparison.Ordinal)
            && string.Equals(registration.SemanticVariantId, row.SemanticVariantId, StringComparison.Ordinal);
    }

    private static bool Matches(
        RasterProviderOperationCapabilityRow row,
        RasterProviderSemanticProof proof) =>
        proof.Engine == row.Engine
        && string.Equals(proof.ProviderId, row.ProviderId, StringComparison.Ordinal)
        && string.Equals(proof.ProcessId, row.ProcessId, StringComparison.Ordinal)
        && string.Equals(proof.SemanticVersion, row.SemanticVersion, StringComparison.Ordinal)
        && string.Equals(proof.SemanticVariantId, row.SemanticVariantId, StringComparison.Ordinal)
        && string.Equals(proof.ImplementationVersion, row.ImplementationVersion, StringComparison.Ordinal)
        && string.Equals(proof.PolicyVersion, row.PolicyVersion, StringComparison.Ordinal);

    private static void ValidateRows(IReadOnlyList<RasterProviderOperationCapabilityRow> rows)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null
                || !Enum.IsDefined(row.Engine)
                || !Enum.IsDefined(row.ServingPrimitiveStatus)
                || string.IsNullOrWhiteSpace(row.ProviderId)
                || string.IsNullOrWhiteSpace(row.ProcessId)
                || string.IsNullOrWhiteSpace(row.SemanticVersion)
                || string.IsNullOrWhiteSpace(row.SemanticVariantId)
                || string.IsNullOrWhiteSpace(row.ImplementationVersion)
                || string.IsNullOrWhiteSpace(row.PolicyVersion)
                || string.IsNullOrWhiteSpace(row.ServingPrimitiveNotes)
                || row.ServingPrimitives is null
                || row.ServingPrimitives.Count == 0
                || row.ServingPrimitives.Any(string.IsNullOrWhiteSpace)
                || row.RequiredExtensions is null
                || row.RequiredExtensions.Count == 0
                || row.RequiredFixtureIds is null
                || row.RequiredFixtureIds.Any(string.IsNullOrWhiteSpace)
                || !Version.TryParse(row.MinimumRuntimeVersion, out _))
            {
                throw new InvalidOperationException(
                    "Raster provider operation rows require defined identities, serving inventory, "
                    + "normalized minimum versions, and extension/fixture collections.");
            }

            foreach (var requirement in row.RequiredExtensions)
            {
                if (requirement is null
                    || string.IsNullOrWhiteSpace(requirement.ExtensionName)
                    || !Version.TryParse(requirement.MinimumVersion, out _))
                {
                    throw new InvalidOperationException(
                        "Raster provider extension requirements need a stable name and normalized minimum version.");
                }
            }

            var key = $"{row.Engine}/{row.ProviderId}/{row.ProcessId}/{row.SemanticVersion}/"
                + $"{row.SemanticVariantId}/{row.ImplementationVersion}/{row.PolicyVersion}";
            if (!keys.Add(key))
            {
                throw new InvalidOperationException($"Duplicate raster provider semantic capability row '{key}'.");
            }
        }
    }

    private static void ValidateRuntimes(IReadOnlyList<RasterProviderRuntimeSnapshot> runtimes)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runtime in runtimes)
        {
            if (runtime is null
                || !Enum.IsDefined(runtime.Engine)
                || string.IsNullOrWhiteSpace(runtime.ProviderId)
                || string.IsNullOrWhiteSpace(runtime.RuntimeVersion)
                || runtime.Extensions is null
                || runtime.Extensions.Any(extension =>
                    extension is null
                    || string.IsNullOrWhiteSpace(extension.ExtensionName)
                    || string.IsNullOrWhiteSpace(extension.Version)))
            {
                throw new InvalidOperationException("Raster provider runtime snapshots contain invalid discovery data.");
            }

            var key = $"{runtime.Engine}/{runtime.ProviderId}";
            if (!keys.Add(key))
            {
                throw new InvalidOperationException($"Duplicate raster provider runtime snapshot '{key}'.");
            }

            if (runtime.Extensions.Select(extension => extension.ExtensionName)
                .Distinct(StringComparer.Ordinal).Count() != runtime.Extensions.Count)
            {
                throw new InvalidOperationException($"Runtime snapshot '{key}' contains duplicate extensions.");
            }
        }
    }

    private static void ValidateExecutors(IReadOnlyList<RasterProviderExecutableSemanticVariant> executors)
    {
        foreach (var registration in executors)
        {
            if (registration is null
                || registration.Executor is null
                || registration.Capability is null
                || string.IsNullOrWhiteSpace(registration.SemanticVariantId)
                || registration.Executor.Capabilities is null
                || !registration.Executor.Capabilities.Contains(registration.Capability))
            {
                throw new InvalidOperationException(
                    "Every semantic variant registration must be declared by its actual "
                    + "IRasterProviderExecutor capability collection.");
            }
        }
    }

    private static void ValidateProofs(IReadOnlyList<RasterProviderSemanticProof> proofs)
    {
        var keys = new HashSet<SemanticProofKey>();
        foreach (var proof in proofs)
        {
            if (proof is null
                || !Enum.IsDefined(proof.Engine)
                || string.IsNullOrWhiteSpace(proof.ProviderId)
                || string.IsNullOrWhiteSpace(proof.ProcessId)
                || string.IsNullOrWhiteSpace(proof.SemanticVersion)
                || string.IsNullOrWhiteSpace(proof.SemanticVariantId)
                || string.IsNullOrWhiteSpace(proof.ImplementationVersion)
                || string.IsNullOrWhiteSpace(proof.PolicyVersion)
                || string.IsNullOrWhiteSpace(proof.FixtureId)
                || !Version.TryParse(proof.RuntimeVersion, out _))
            {
                throw new InvalidOperationException("Raster provider semantic proofs contain invalid identity data.");
            }

            var key = new SemanticProofKey(
                proof.ProviderId,
                proof.Engine,
                proof.ProcessId,
                proof.SemanticVersion,
                proof.SemanticVariantId,
                proof.ImplementationVersion,
                proof.PolicyVersion,
                proof.FixtureId,
                proof.RuntimeVersion);
            if (!keys.Add(key))
            {
                throw new InvalidOperationException(
                    "Raster provider semantic proofs must contain at most one receipt for each "
                    + "exact provider, variant, fixture, and runtime identity.");
            }
        }
    }

    private static string Display(RasterProviderOperationCapabilityRow row) =>
        $"{row.ProviderId}/{row.ProcessId}@{row.SemanticVersion} variant '{row.SemanticVariantId}'";

    private static void Reject(
        ICollection<RasterProviderCapabilityRejection> rejections,
        string code,
        string reason) => rejections.Add(new RasterProviderCapabilityRejection
        {
            Code = code,
            Reason = reason,
        });

    private readonly record struct SemanticProofKey(
        string ProviderId,
        RasterEngine Engine,
        string ProcessId,
        string SemanticVersion,
        string SemanticVariantId,
        string ImplementationVersion,
        string PolicyVersion,
        string FixtureId,
        string RuntimeVersion);
}
