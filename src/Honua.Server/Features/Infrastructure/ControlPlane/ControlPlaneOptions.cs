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
