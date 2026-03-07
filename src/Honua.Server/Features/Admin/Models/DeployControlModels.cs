// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Instance-scoped deploy preflight response for coordinated Honua rollouts.
/// </summary>
public sealed class DeployPreflightResponse
{
    /// <summary>
    /// Current status for coordinated deployment eligibility.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Whether this instance is ready to participate in a coordinated deployment.
    /// </summary>
    [JsonPropertyName("readyForCoordinatedDeploy")]
    public bool ReadyForCoordinatedDeploy { get; init; }

    /// <summary>
    /// Operator-facing summary for the current preflight result.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Honua server version currently running on this instance.
    /// </summary>
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = string.Empty;

    /// <summary>
    /// ASP.NET host environment for this instance.
    /// </summary>
    [JsonPropertyName("environment")]
    public string Environment { get; init; } = string.Empty;

    /// <summary>
    /// Deployment mode configured for this instance.
    /// </summary>
    [JsonPropertyName("deploymentMode")]
    public string DeploymentMode { get; init; } = string.Empty;

    /// <summary>
    /// Machine or instance name serving the request.
    /// </summary>
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp when the preflight payload was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Current readiness state for this instance.
    /// </summary>
    [JsonPropertyName("readiness")]
    public required DeployPreflightReadiness Readiness { get; init; }

    /// <summary>
    /// Current migration and schema alignment state for this instance.
    /// </summary>
    [JsonPropertyName("migration")]
    public required DeployPreflightMigration Migration { get; init; }
}

/// <summary>
/// Readiness summary embedded in deploy preflight responses.
/// </summary>
public sealed class DeployPreflightReadiness
{
    /// <summary>
    /// Whether the instance is currently ready to accept traffic.
    /// </summary>
    [JsonPropertyName("isReady")]
    public bool IsReady { get; init; }

    /// <summary>
    /// HTTP status code that would be returned by the readiness endpoint.
    /// </summary>
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; }

    /// <summary>
    /// Human-readable readiness message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Migration and schema alignment summary embedded in deploy preflight responses.
/// </summary>
public sealed class DeployPreflightMigration
{
    /// <summary>
    /// Current migration lifecycle status observed by this instance.
    /// </summary>
    [JsonPropertyName("lifecycleStatus")]
    public string LifecycleStatus { get; init; } = string.Empty;

    /// <summary>
    /// Optional operator-facing lifecycle detail.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Whether a migration plan could be generated successfully.
    /// </summary>
    [JsonPropertyName("planAvailable")]
    public bool PlanAvailable { get; init; }

    /// <summary>
    /// Whether the current instance detects pending migration scripts.
    /// </summary>
    [JsonPropertyName("upgradeRequired")]
    public bool UpgradeRequired { get; init; }

    /// <summary>
    /// Pending migration scripts for this instance and current database.
    /// </summary>
    [JsonPropertyName("pendingScripts")]
    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Scripts previously executed against the database but no longer discovered by the current binary.
    /// </summary>
    [JsonPropertyName("executedButNotDiscoveredScripts")]
    public IReadOnlyList<string> ExecutedButNotDiscoveredScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Error detail when migration planning could not be completed.
    /// </summary>
    [JsonPropertyName("planError")]
    public string? PlanError { get; init; }
}
