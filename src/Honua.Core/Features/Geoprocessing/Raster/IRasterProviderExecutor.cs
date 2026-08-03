// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Canonical durable parameter keys consumed by raster provider executors.</summary>
public static class RasterProviderExecutionParameterKeys
{
    /// <summary>
    /// Tenant identity pinned by the authenticated submission path. Provider executors must also
    /// apply their normal tenant context/schema fence; this value is never a SQL identifier.
    /// </summary>
    public const string TenantId = "raster.tenant_id";
}

/// <summary>Provider health and policy availability for one exact raster semantic variant.</summary>
public enum RasterProviderAvailability
{
    /// <summary>The provider can accept this exact variant.</summary>
    Available,

    /// <summary>The provider supports the variant but is temporarily unhealthy.</summary>
    Unhealthy,

    /// <summary>The provider does not expose the variant under its current capability policy.</summary>
    Unavailable,
}

/// <summary>
/// Exact semantic and implementation identity selected before a raster attempt is created.
/// </summary>
public sealed record RasterSemanticVariant
{
    /// <summary>Canonical public raster process identifier.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Engine-independent semantic contract version.</summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Provider implementation version for the semantic contract.</summary>
    public required string ImplementationVersion { get; init; }
}

/// <summary>
/// Provider-neutral discovery record for one executable raster semantic variant.
/// </summary>
public sealed record RasterProviderCapability
{
    /// <summary>Stable provider identity, for example <c>postgis</c>.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Raster engine implemented by the provider.</summary>
    public required RasterEngine Engine { get; init; }

    /// <summary>Exact semantic variant implemented by the provider.</summary>
    public required RasterSemanticVariant Variant { get; init; }

    /// <summary>Stable provider capability-policy version used for admission.</summary>
    public required string PolicyVersion { get; init; }

    /// <summary>Current availability of the exact semantic variant.</summary>
    public required RasterProviderAvailability Availability { get; init; }

    /// <summary>Actionable bounded reason when the capability is not available.</summary>
    public string? UnavailabilityReason { get; init; }
}

/// <summary>Immutable reference returned by a raster provider for canonical publication.</summary>
public sealed record RasterProviderResultReference
{
    /// <summary>Opaque immutable reference consumed by the canonical artifact publication path.</summary>
    public required string Reference { get; init; }

    /// <summary>IANA media type of the referenced result.</summary>
    public required string MediaType { get; init; }

    /// <summary>Optional lowercase SHA-256 digest of the referenced bytes.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Optional bounded byte length of the referenced result.</summary>
    public long? Length { get; init; }
}

/// <summary>Provider-neutral outcome for a raster execution attempt.</summary>
public enum RasterProviderExecutionStatus
{
    /// <summary>The provider completed and returned immutable result references.</summary>
    Succeeded,

    /// <summary>The pinned capability or semantic variant is not executable by this provider.</summary>
    CapabilityUnavailable,

    /// <summary>The provider attempted execution and failed.</summary>
    Failed,
}

/// <summary>Immutable request passed from the durable GP runtime to a provider executor.</summary>
public sealed record RasterProviderExecutionRequest
{
    /// <summary>Stable durable operation identifier.</summary>
    public required string OperationId { get; init; }

    /// <summary>One-based durable attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>Tenant identity pinned by the submit path and revalidated by the provider.</summary>
    public required string TenantId { get; init; }

    /// <summary>Exact provider, policy, semantic, and placement decision persisted at submission.</summary>
    public required RasterExecutionDecision Decision { get; init; }

    /// <summary>Canonical immutable job parameters. Providers must treat these as untrusted input.</summary>
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
}

/// <summary>Result returned by a provider-neutral raster executor.</summary>
public sealed record RasterProviderExecutionResult
{
    /// <summary>Execution outcome.</summary>
    public required RasterProviderExecutionStatus Status { get; init; }

    /// <summary>Immutable result references produced on success.</summary>
    public IReadOnlyList<RasterProviderResultReference> Outputs { get; init; } = [];

    /// <summary>Stable machine-readable failure code.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Bounded provider-neutral failure description.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Whether a fresh health/capacity snapshot can make the same attempt executable.</summary>
    public bool IsRetryable { get; init; }

    /// <summary>Creates a successful result with immutable references.</summary>
    public static RasterProviderExecutionResult Succeeded(
        IReadOnlyList<RasterProviderResultReference> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        return new()
        {
            Status = RasterProviderExecutionStatus.Succeeded,
            Outputs = Array.AsReadOnly(outputs.ToArray()),
        };
    }

    /// <summary>Creates an explicit capability-unavailable result.</summary>
    public static RasterProviderExecutionResult CapabilityUnavailable(
        string errorCode,
        string errorMessage,
        bool isRetryable = false) => new()
        {
            Status = RasterProviderExecutionStatus.CapabilityUnavailable,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            IsRetryable = isRetryable,
        };

    /// <summary>Creates a failed execution result.</summary>
    public static RasterProviderExecutionResult Failed(
        string errorCode,
        string errorMessage,
        bool isRetryable = false) => new()
        {
            Status = RasterProviderExecutionStatus.Failed,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            IsRetryable = isRetryable,
        };
}

/// <summary>
/// Provider-neutral raster execution seam. Provider packages implement this interface; Core and
/// Honua.Geoprocessing never depend on provider SQL, connections, commands, or native bindings.
/// </summary>
public interface IRasterProviderExecutor
{
    /// <summary>Exact semantic variants discoverable from this provider.</summary>
    IReadOnlyList<RasterProviderCapability> Capabilities { get; }

    /// <summary>
    /// Executes the pinned request. The cancellation token is linked to operator cancellation,
    /// worker shutdown, timeout, and durable lease loss by the canonical job substrate.
    /// </summary>
    Task<RasterProviderExecutionResult> ExecuteAsync(
        RasterProviderExecutionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Exact key used to route a pinned raster decision without replanning.</summary>
public readonly record struct RasterProviderRouteKey(
    RasterEngine Engine,
    string ProviderId,
    string ProcessId,
    string SemanticVersion,
    string ImplementationVersion,
    string PolicyVersion);

/// <summary>Validated provider executor plus the exact capability it implements.</summary>
public sealed record RasterProviderExecutorRegistration(
    IRasterProviderExecutor Executor,
    RasterProviderCapability Capability);

/// <summary>AOT-safe route-table builder for provider raster executors.</summary>
public static class RasterProviderExecutorRouteTable
{
    /// <summary>
    /// Validates capability declarations and creates an exact immutable route table. Duplicate
    /// routes fail startup rather than allowing registration order to select a provider.
    /// </summary>
    public static FrozenDictionary<RasterProviderRouteKey, RasterProviderExecutorRegistration> Build(
        IEnumerable<IRasterProviderExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);

        var routes = new Dictionary<RasterProviderRouteKey, RasterProviderExecutorRegistration>();
        foreach (var executor in executors)
        {
            if (executor is null || executor.Capabilities is null || executor.Capabilities.Count == 0)
            {
                throw new InvalidOperationException(
                    "Every raster provider executor must declare at least one capability.");
            }

            foreach (var capability in executor.Capabilities)
            {
                ValidateCapability(capability);
                var variant = capability.Variant;
                var key = new RasterProviderRouteKey(
                    capability.Engine,
                    capability.ProviderId,
                    variant.ProcessId,
                    variant.SemanticVersion,
                    variant.ImplementationVersion,
                    capability.PolicyVersion);
                if (!routes.TryAdd(key, new RasterProviderExecutorRegistration(executor, capability)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate raster provider route '{capability.ProviderId}/"
                        + $"{variant.ProcessId}@{variant.SemanticVersion}/"
                        + $"{variant.ImplementationVersion}/{capability.PolicyVersion}'.");
                }
            }
        }

        if (routes.Count == 0)
        {
            throw new InvalidOperationException(
                "The raster provider worker requires at least one executable capability route.");
        }

        return routes.ToFrozenDictionary();
    }

    private static void ValidateCapability(RasterProviderCapability capability)
    {
        if (capability is null
            || capability.Variant is null
            || !Enum.IsDefined(capability.Engine)
            || !Enum.IsDefined(capability.Availability)
            || string.IsNullOrWhiteSpace(capability.ProviderId)
            || string.IsNullOrWhiteSpace(capability.PolicyVersion)
            || string.IsNullOrWhiteSpace(capability.Variant.ProcessId)
            || string.IsNullOrWhiteSpace(capability.Variant.SemanticVersion)
            || string.IsNullOrWhiteSpace(capability.Variant.ImplementationVersion))
        {
            throw new InvalidOperationException(
                "Raster provider capability declarations must contain defined engine/availability "
                + "values and non-empty provider, policy, process, semantic, and implementation identities.");
        }

        if ((capability.Availability == RasterProviderAvailability.Available)
            == !string.IsNullOrWhiteSpace(capability.UnavailabilityReason))
        {
            throw new InvalidOperationException(
                "A raster provider capability must carry an unavailability reason exactly when "
                + "it is unhealthy or unavailable.");
        }
    }
}
