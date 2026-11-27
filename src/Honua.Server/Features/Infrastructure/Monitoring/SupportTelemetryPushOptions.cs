// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;

namespace Honua.Server.Features.Infrastructure.Monitoring;

// Configuration for opt-in outbound telemetry snapshots pushed from honua-server to
// honua-support (POST /api/v1/telemetry/ingest). Disabled by default so no deployment
// sends outbound traffic unless explicitly configured.
internal sealed class SupportTelemetryPushOptions
{
    public const string SectionName = "SupportTelemetry";
    public const int MinIntervalSeconds = 10;
    public const int MaxIntervalSeconds = 3600;
    public const int MinRequestTimeoutSeconds = 2;
    public const int MaxRequestTimeoutSeconds = 60;
    public const int MaxRecentErrorsLimit = 100;

    public bool Enabled { get; set; }

    public string? EndpointUrl { get; set; }

    public bool AllowHttp { get; set; }

    public string? ApiKey { get; set; }

    public string? InstanceId { get; set; }

    public string? InstanceUrl { get; set; }

    public string? ArtifactDigest { get; set; }

    public string? PlatformLockDigest { get; set; }

    public int IntervalSeconds { get; set; } = 60;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int MaxRecentErrors { get; set; } = 25;

    public bool IncludeOpsFindings { get; set; } = true;

    public TimeSpan EffectiveInterval =>
        TimeSpan.FromSeconds(Math.Clamp(IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds));

    public TimeSpan EffectiveRequestTimeout =>
        TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, MinRequestTimeoutSeconds, MaxRequestTimeoutSeconds));

    public int EffectiveMaxRecentErrors =>
        Math.Clamp(MaxRecentErrors, 0, MaxRecentErrorsLimit);

    public static void ValidateEnabled(SupportTelemetryPushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.EndpointUrl) ||
            !Uri.TryCreate(options.EndpointUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "SupportTelemetry:EndpointUrl must be an absolute URL when SupportTelemetry:Enabled is true.");
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !(isHttp && options.AllowHttp))
        {
            throw new InvalidOperationException(
                "SupportTelemetry:EndpointUrl must use https unless SupportTelemetry:AllowHttp is true.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "SupportTelemetry:EndpointUrl must not contain embedded credentials.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "SupportTelemetry:ApiKey is required when SupportTelemetry:Enabled is true.");
        }

        var trimmedKey = options.ApiKey.Trim();
        foreach (var ch in trimmedKey)
        {
            if (ch < 0x21 || ch > 0x7E)
            {
                throw new InvalidOperationException(
                    "SupportTelemetry:ApiKey must contain only printable non-whitespace ASCII characters.");
            }
        }
    }
}

// Resolves build/artifact identity metadata shared across admin version reporting and outbound
// support telemetry pushes.
internal static class ServerArtifactMetadata
{
    public const string ArtifactDigestEnvVar = "HONUA_ARTIFACT_DIGEST";
    public const string PlatformLockDigestEnvVar = "HONUA_PLATFORM_LOCK_DIGEST";

    public static string GetServerVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? assembly.GetName().Version?.ToString() ?? "0.0.0" : info;
    }

    public static string? ResolveArtifactDigest(IConfiguration? configuration, string? configuredOverride = null) =>
        FirstNonEmpty(
            configuredOverride,
            configuration?[$"{SupportTelemetryPushOptions.SectionName}:ArtifactDigest"],
            configuration?[ArtifactDigestEnvVar],
            Environment.GetEnvironmentVariable(ArtifactDigestEnvVar));

    public static string? ResolvePlatformLockDigest(IConfiguration? configuration, string? configuredOverride = null) =>
        FirstNonEmpty(
            configuredOverride,
            configuration?[$"{SupportTelemetryPushOptions.SectionName}:PlatformLockDigest"],
            configuration?[PlatformLockDigestEnvVar],
            Environment.GetEnvironmentVariable(PlatformLockDigestEnvVar));

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
