// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Configuration-backed catalogs for control-plane deploy targets and workload definitions.
/// </summary>
internal sealed class ControlPlaneOptions
{
    /// <summary>
    /// Configuration section name for control-plane catalogs.
    /// </summary>
    public const string SectionName = "ControlPlane";

    /// <summary>
    /// Stable deploy target catalog entries.
    /// </summary>
    public List<DeployTargetOptions> DeployTargets { get; set; } = [];

    /// <summary>
    /// Stable execution workload catalog entries.
    /// </summary>
    public List<ExecutionWorkloadOptions> ExecutionWorkloads { get; set; } = [];

    /// <summary>
    /// Named telemetry query connections used for deploy health gating.
    /// </summary>
    public List<DeployTelemetryConnectionOptions> TelemetryConnections { get; set; } = [];

    /// <summary>
    /// Optional Kubernetes execution backend configuration. Left at defaults when
    /// Kubernetes is not used; populated when operators enable the adapter.
    /// </summary>
    public KubernetesExecutionOptions Kubernetes { get; set; } = new();
}

/// <summary>
/// Configuration model for the optional Kubernetes Jobs execution backend.
/// </summary>
internal sealed class KubernetesExecutionOptions
{
    /// <summary>
    /// When true, the adapter attempts to discover its API server, bearer token,
    /// and CA bundle from the projected service-account mount before falling back
    /// to explicit configuration. Defaults to <c>true</c> so in-cluster
    /// deployments are zero-config.
    /// </summary>
    public bool InClusterAutoDetect { get; set; } = true;

    /// <summary>
    /// Explicit Kubernetes API server URL. Required when the adapter is used
    /// out-of-cluster or when auto-detection is disabled.
    /// </summary>
    public string? ApiServerUrl { get; set; }

    /// <summary>
    /// File path to a bearer token the adapter should present. Typically a
    /// projected service-account token or a kubeconfig-derived token file.
    /// </summary>
    public string? BearerTokenPath { get; set; }

    /// <summary>
    /// Optional literal bearer token. Use <see cref="BearerTokenPath"/> when
    /// possible so rotation is picked up without restarts.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Optional path to a PEM-encoded CA bundle that the adapter should trust when
    /// verifying the Kubernetes API server certificate. Required for out-of-cluster
    /// targets whose API server certificate is signed by a private or self-signed CA
    /// that does not chain to the OS trust store. Ignored only when
    /// <see cref="InClusterAutoDetect"/> is enabled and the projected service-account
    /// CA bundle is available; in that case the in-cluster bundle is used. When
    /// <see cref="InClusterAutoDetect"/> is disabled — including from inside a pod
    /// that targets a different cluster — this value is honored even if the local
    /// projected CA file exists.
    /// </summary>
    public string? CaBundlePath { get; set; }

    /// <summary>
    /// Namespace used when an execution job does not specify one through its spec
    /// parameters. When unset, the projected in-cluster namespace is used when
    /// available; otherwise falls back to <c>default</c>.
    /// </summary>
    public string? DefaultNamespace { get; set; }

    /// <summary>
    /// Fallback container image used when a job spec does not resolve to one.
    /// </summary>
    public string? DefaultImage { get; set; }

    /// <summary>
    /// Fallback image pull policy (<c>Always</c>, <c>IfNotPresent</c>, <c>Never</c>).
    /// </summary>
    public string? DefaultImagePullPolicy { get; set; }

    /// <summary>
    /// Fallback service account for pods.
    /// </summary>
    public string? DefaultServiceAccount { get; set; }

    /// <summary>
    /// Fallback container CPU request (for example <c>500m</c>).
    /// </summary>
    public string? DefaultCpuRequest { get; set; }

    /// <summary>
    /// Fallback container CPU limit.
    /// </summary>
    public string? DefaultCpuLimit { get; set; }

    /// <summary>
    /// Fallback container memory request (for example <c>4Gi</c>).
    /// </summary>
    public string? DefaultMemoryRequest { get; set; }

    /// <summary>
    /// Fallback container memory limit.
    /// </summary>
    public string? DefaultMemoryLimit { get; set; }

    /// <summary>
    /// Fallback node selector applied to pods when a job spec does not override it.
    /// Useful for steering GDAL-class workloads onto dedicated nodes.
    /// </summary>
    public Dictionary<string, string> DefaultNodeSelector { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Fallback image pull secret names.
    /// </summary>
    public List<string> DefaultImagePullSecrets { get; set; } = [];

    /// <summary>
    /// Fallback pod active deadline in seconds. Overridden by the spec when present
    /// or by the job's <see cref="Honua.Core.Features.ControlPlane.Domain.JobTimeoutPolicy"/>.
    /// </summary>
    public int? DefaultActiveDeadlineSeconds { get; set; }

    /// <summary>
    /// Fallback <c>ttlSecondsAfterFinished</c> used so completed Jobs clean up
    /// automatically without relying on the canonical runtime's retention.
    /// </summary>
    public int? DefaultTtlSecondsAfterFinished { get; set; } = 3600;
}

/// <summary>
/// Configuration model for a stable deploy target.
/// </summary>
internal sealed class DeployTargetOptions
{
    public string TargetId { get; set; } = string.Empty;

    public DeployTargetKind TargetKind { get; set; } = DeployTargetKind.Kubernetes;

    public string Backend { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;

    public string TargetName { get; set; } = string.Empty;

    public string? ArtifactReference { get; set; }

    public string? RuntimeProfile { get; set; }

    public bool RequiresApproval { get; set; }

    public bool RequiresOutOfBandMigrations { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Environment-friendly key/value parameter entries for providers that cannot expose dotted
    /// dictionary keys directly through environment variables.
    /// </summary>
    public List<ConfigurationParameterEntryOptions> ParameterEntries { get; set; } = [];
}

/// <summary>
/// Configuration model for a telemetry query connection used by deploy rollback gates.
/// </summary>
internal sealed class DeployTelemetryConnectionOptions
{
    public string ConnectionId { get; set; } = string.Empty;

    public string Provider { get; set; } = "prometheus";

    public string BaseUrl { get; set; } = string.Empty;

    public string QueryPath { get; set; } = "/api/v1/query";

    public string? AuthHeaderName { get; set; }

    public string? AuthHeaderValue { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Configuration model for a stable execution workload definition.
/// </summary>
internal sealed class ExecutionWorkloadOptions
{
    public string WorkloadId { get; set; } = string.Empty;

    public BatchComputeTargetKind TargetKind { get; set; } = BatchComputeTargetKind.KubernetesJob;

    public string Backend { get; set; } = string.Empty;

    public ExecutionJobKind Kind { get; set; } = ExecutionJobKind.Geoprocessing;

    public string WorkloadName { get; set; } = string.Empty;

    public string? ArtifactReference { get; set; }

    public string? RuntimeProfile { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Environment-friendly key/value parameter entries for providers that cannot expose dotted
    /// dictionary keys directly through environment variables.
    /// </summary>
    public List<ConfigurationParameterEntryOptions> ParameterEntries { get; set; } = [];
}

internal sealed class ConfigurationParameterEntryOptions
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

internal sealed class ControlPlaneOptionsValidator : OptionsValidator<ControlPlaneOptions>
{
    protected override void ValidateOptions(ControlPlaneOptions options, List<string> failures)
    {
        ValidateKubernetes(options.Kubernetes, failures);

        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.TelemetryConnections.Count; i++)
        {
            var connection = options.TelemetryConnections[i];
            var propertyPrefix = $"ControlPlane:TelemetryConnections:{i}";

            if (string.IsNullOrWhiteSpace(connection.ConnectionId))
            {
                failures.Add($"{propertyPrefix}:ConnectionId cannot be empty");
            }
            else if (!connectionIds.Add(connection.ConnectionId.Trim()))
            {
                failures.Add($"{propertyPrefix}:ConnectionId '{connection.ConnectionId}' must be unique");
            }

            if (string.IsNullOrWhiteSpace(connection.Provider))
            {
                failures.Add($"{propertyPrefix}:Provider cannot be empty");
            }

            var baseUrlValidation = OutboundHttpUrlValidator.ValidateConfiguration(connection.BaseUrl);
            if (!baseUrlValidation.IsValid)
            {
                failures.Add($"{propertyPrefix}:BaseUrl {baseUrlValidation.ErrorMessage ?? "must be a valid HTTPS URL."}");
            }

            if (!ControlPlaneTelemetryConnectionValidation.TryNormalizeQueryPath(
                    connection.QueryPath,
                    out _,
                    out var queryPathError))
            {
                failures.Add($"{propertyPrefix}:QueryPath {queryPathError}");
            }

            if (!ControlPlaneTelemetryConnectionValidation.TryNormalizeAuthHeader(
                    connection.AuthHeaderName,
                    connection.AuthHeaderValue,
                    out _,
                    out _,
                    out var authHeaderError))
            {
                failures.Add($"{propertyPrefix}:{authHeaderError}");
            }

            if (connection.TimeoutSeconds <= 0)
            {
                failures.Add($"{propertyPrefix}:TimeoutSeconds must be greater than 0.");
            }
        }
    }

    private static void ValidateKubernetes(KubernetesExecutionOptions options, List<string> failures)
    {
        const string prefix = "ControlPlane:Kubernetes";

        if (!string.IsNullOrWhiteSpace(options.ApiServerUrl) &&
            !Uri.TryCreate(options.ApiServerUrl, UriKind.Absolute, out _))
        {
            failures.Add($"{prefix}:ApiServerUrl must be an absolute URL (e.g. https://cluster.example).");
        }

        if (!options.InClusterAutoDetect && string.IsNullOrWhiteSpace(options.ApiServerUrl))
        {
            failures.Add($"{prefix}:ApiServerUrl must be configured when InClusterAutoDetect is disabled.");
        }

        if (!string.IsNullOrWhiteSpace(options.CaBundlePath) && !File.Exists(options.CaBundlePath))
        {
            failures.Add($"{prefix}:CaBundlePath '{options.CaBundlePath}' does not exist or is unreadable.");
        }

        if (!string.IsNullOrWhiteSpace(options.BearerTokenPath) && !File.Exists(options.BearerTokenPath))
        {
            failures.Add($"{prefix}:BearerTokenPath '{options.BearerTokenPath}' does not exist or is unreadable.");
        }
    }
}

internal static class ControlPlaneTelemetryConnectionValidation
{
    private static readonly HashSet<string> DisallowedAuthHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Content-Length",
        "Cookie",
        "Forwarded",
        "Host",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Set-Cookie",
        "TE",
        "Transfer-Encoding",
        "Upgrade",
        "Via",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto"
    };

    public static bool TryNormalizeQueryPath(
        string? queryPath,
        out string normalizedPath,
        out string? errorMessage)
    {
        normalizedPath = string.IsNullOrWhiteSpace(queryPath) ? "/api/v1/query" : queryPath.Trim();
        errorMessage = null;

        if (normalizedPath.Contains('\r') || normalizedPath.Contains('\n'))
        {
            errorMessage = "must not contain control characters.";
            return false;
        }

        if (normalizedPath.StartsWith("//", StringComparison.Ordinal))
        {
            errorMessage = "must not start with '//'.";
            return false;
        }

        if (normalizedPath.Contains("://", StringComparison.Ordinal))
        {
            errorMessage = "must be a relative path, not a full URL.";
            return false;
        }

        if (!normalizedPath.StartsWith('/'))
        {
            errorMessage = "must start with '/'.";
            return false;
        }

        if (normalizedPath.Contains('?') || normalizedPath.Contains('#'))
        {
            errorMessage = "must not include a query string or fragment.";
            return false;
        }

        return true;
    }

    public static bool TryNormalizeAuthHeader(
        string? headerName,
        string? headerValue,
        out string? normalizedName,
        out string? normalizedValue,
        out string? errorMessage)
    {
        normalizedName = null;
        normalizedValue = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(headerName) && string.IsNullOrWhiteSpace(headerValue))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(headerValue))
        {
            errorMessage = "AuthHeaderName and AuthHeaderValue must be configured together.";
            return false;
        }

        normalizedName = headerName.Trim();
        normalizedValue = headerValue.Trim();

        if (!IsValidHttpHeaderName(normalizedName))
        {
            errorMessage = "AuthHeaderName must be a valid HTTP header name.";
            return false;
        }

        if (DisallowedAuthHeaderNames.Contains(normalizedName))
        {
            errorMessage = $"AuthHeaderName header '{normalizedName}' is not allowed.";
            return false;
        }

        if (ContainsInvalidHeaderValueCharacter(normalizedValue))
        {
            errorMessage = "AuthHeaderValue contains invalid control characters.";
            return false;
        }

        return true;
    }

    private static bool IsValidHttpHeaderName(string value)
    {
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) ||
                  character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsInvalidHeaderValueCharacter(string value)
    {
        foreach (var character in value)
        {
            if ((character < 0x20 && character != '\t') || character == 0x7f)
            {
                return true;
            }
        }

        return false;
    }
}
