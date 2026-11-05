// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.ControlPlane;

internal sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";

    public List<DeployTargetOptions> DeployTargets { get; set; } = [];

    public List<ExecutionWorkloadOptions> ExecutionWorkloads { get; set; } = [];

    public List<DeployTelemetryConnectionOptions> TelemetryConnections { get; set; } = [];

    public KubernetesExecutionOptions Kubernetes { get; set; } = new();

    public AzureBatchExecutionOptions AzureBatch { get; set; } = new();

    public PlatformReleaseOptions PlatformRelease { get; set; } = new();

    // Single-host batch backends fail closed on serverless, or on multi-node without a shared work directory.
    public SubstrateOptions Substrate { get; set; } = new();
}

internal sealed class SubstrateOptions
{
    public BatchComputeSubstrateProfile Profile { get; set; } = BatchComputeSubstrateProfile.SingleHost;

    public bool SharedWorkDir { get; set; }

    public bool AutoDetectServerless { get; set; } = true;
}

internal sealed class PlatformReleaseOptions
{
    public string Version { get; set; } = string.Empty;

    public string? ServingArtifactReference { get; set; }

    public List<PlatformReleaseWorkerImageOptions> Workers { get; set; } = [];

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Version) &&
        string.IsNullOrWhiteSpace(ServingArtifactReference) &&
        Workers.Count == 0;

    public PlatformReleaseDefinition? ToDefinition()
    {
        if (IsEmpty)
        {
            return null;
        }

        return new PlatformReleaseDefinition
        {
            Version = Version.Trim(),
            ServingArtifactReference = string.IsNullOrWhiteSpace(ServingArtifactReference)
                ? null
                : ServingArtifactReference.Trim(),
            Workers = Workers
                .Select(worker => new PlatformReleaseWorkerImage
                {
                    RuntimeProfile = string.IsNullOrWhiteSpace(worker.RuntimeProfile)
                        ? null
                        : worker.RuntimeProfile.Trim(),
                    ArtifactReference = worker.ArtifactReference?.Trim() ?? string.Empty
                })
                .ToArray()
        };
    }
}

internal sealed class PlatformReleaseWorkerImageOptions
{
    public string? RuntimeProfile { get; set; }

    public string ArtifactReference { get; set; } = string.Empty;
}

internal sealed class KubernetesExecutionOptions
{
    public bool InClusterAutoDetect { get; set; } = true;

    // The API server URL is required out of cluster or when auto-detection is disabled.
    public string? ApiServerUrl { get; set; }

    public string? BearerTokenPath { get; set; }

    // A literal token is the fallback; BearerTokenPath is preferred so rotation is picked up without a restart.
    public string? BearerToken { get; set; }

    // A private CA bundle is required for an out-of-cluster API server that does not chain to the OS trust store, and it is ignored only when in-cluster detection uses the projected bundle.
    public string? CaBundlePath { get; set; }

    public string? DefaultNamespace { get; set; }

    public string? DefaultImage { get; set; }

    // The default contract is v1 so a rollout cannot send a newer spec to an older worker unless the deployment attests that image.
    public int DefaultImageMaxSupportedContractVersion { get; set; } = 1;

    // Image identities are dictionary values because tags contain configuration-path delimiters, and an unlisted override is a v1 worker.
    public List<WorkerImageContractOptions> ImageContracts { get; set; } = [];

    public string? DefaultImagePullPolicy { get; set; }

    public string? DefaultServiceAccount { get; set; }

    public string? DefaultCpuRequest { get; set; }

    public string? DefaultCpuLimit { get; set; }

    public string? DefaultMemoryRequest { get; set; }

    public string? DefaultMemoryLimit { get; set; }

    public Dictionary<string, string> DefaultNodeSelector { get; set; } = new(StringComparer.Ordinal);

    public List<string> DefaultImagePullSecrets { get; set; } = [];

    // These secrets are imported into every job this backend launches, and only the secret name reaches the server.
    public List<string> DefaultEnvFromSecrets { get; set; } = [];

    public List<string> DefaultEnvFromConfigMaps { get; set; } = [];

    public int? DefaultActiveDeadlineSeconds { get; set; }

    public int? DefaultTtlSecondsAfterFinished { get; set; } = 3600;
}

internal sealed class AzureBatchExecutionOptions
{
    // Unlisted images are treated as v1 workers, and image identities are dictionary values because tags contain configuration-path delimiters.
    public List<WorkerImageContractOptions> ImageContracts { get; set; } = [];
}

internal sealed class WorkerImageContractOptions
{
    public string Image { get; set; } = string.Empty;

    public int MaxSupportedContractVersion { get; set; } = 1;
}

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

    public List<ConfigurationParameterEntryOptions> ParameterEntries { get; set; } = [];
}

internal sealed class DeployTelemetryConnectionOptions
{
    public string ConnectionId { get; set; } = string.Empty;

    public string Provider { get; set; } = "prometheus";

    public string BaseUrl { get; set; } = string.Empty;

    public string QueryPath { get; set; } = "/api/v1/query";

    public string? AuthHeaderName { get; set; }

    public string? AuthHeaderValue { get; set; }

    public string? Region { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public bool AllowPrivateNetworks { get; set; }
}

internal sealed class ExecutionWorkloadOptions
{
    public string WorkloadId { get; set; } = string.Empty;

    public BatchComputeTargetKind TargetKind { get; set; } = BatchComputeTargetKind.KubernetesJob;

    public string Backend { get; set; } = string.Empty;

    public ExecutionJobKind Kind { get; set; } = ExecutionJobKind.Geoprocessing;

    public string WorkloadName { get; set; } = string.Empty;

    public string? ArtifactReference { get; set; }

    public string? RuntimeProfile { get; set; }

    // The contract is raised only when the payload needs a newer worker, so the dispatcher can refuse a backend whose workers cannot run it.
    public int ContractVersion { get; set; } = 1;

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);

    public List<ConfigurationParameterEntryOptions> ParameterEntries { get; set; } = [];
}

internal sealed class ConfigurationParameterEntryOptions
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

internal sealed class ControlPlaneOptionsValidator : OptionsValidator<ControlPlaneOptions>
{
    // This literal stays in lockstep with the geoprocessing runtime-profile constant so the validator does not reference that internal type.
    private const string CustomCodeRuntimeProfile = "custom-code";

    protected override void ValidateOptions(ControlPlaneOptions options, List<string> failures)
    {
        ValidateCustomCodeWorkloadsAreBatchOnly(options.ExecutionWorkloads, failures);

        ValidateKubernetes(options.Kubernetes, failures);
        ValidateKubernetesWorkloadParameters(options.ExecutionWorkloads, failures);

        PlatformReleaseValidation.Validate(
            options.PlatformRelease.ToDefinition(),
            $"{ControlPlaneOptions.SectionName}:PlatformRelease",
            failures);

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

            var baseUrlValidation = OutboundHttpUrlValidator.ValidateConfiguration(
                connection.BaseUrl,
                connection.AllowPrivateNetworks);
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

    // Custom geoprocessing code may target only the AWS Batch backend family, and any other substrate fails startup.
    private static void ValidateCustomCodeWorkloadsAreBatchOnly(
        List<ExecutionWorkloadOptions> workloads,
        List<string> failures)
    {
        for (var i = 0; i < workloads.Count; i++)
        {
            var workload = workloads[i];
            if (!string.Equals(workload.RuntimeProfile?.Trim(), CustomCodeRuntimeProfile, StringComparison.Ordinal))
            {
                continue;
            }

            if (workload.TargetKind != BatchComputeTargetKind.AwsBatch)
            {
                failures.Add(
                    $"ControlPlane:ExecutionWorkloads:{i} (WorkloadId '{workload.WorkloadId}') declares the "
                    + $"'{CustomCodeRuntimeProfile}' runtime profile with TargetKind '{workload.TargetKind}', but "
                    + "custom-code (custom geoprocessing tool) execution is AWS-Batch-only: untrusted operator "
                    + "code must run only in an isolated cloud-managed AWS Batch container, never on-host with "
                    + "honua-server. Set TargetKind='AwsBatch' (Backend='honua-aws-batch') or remove the workload. "
                    + "See ADR-0063 (custom-code execution is AWS-Batch-only).");
            }
        }
    }

    private static void ValidateKubernetes(KubernetesExecutionOptions options, List<string> failures)
    {
        const string prefix = "ControlPlane:Kubernetes";

        if (!string.IsNullOrWhiteSpace(options.ApiServerUrl))
        {
            // The Kubernetes API receives a bearer token via the Authorization header on every
            // CreateJob/GetJob/DeleteJob request; a non-HTTPS endpoint would ship those credentials
            // and the job payload in clear text. Reject at startup so misconfiguration cannot
            // silently land on a production deployment.
            if (!Uri.TryCreate(options.ApiServerUrl, UriKind.Absolute, out var parsed))
            {
                failures.Add($"{prefix}:ApiServerUrl must be an absolute URL (e.g. https://cluster.example).");
            }
            else if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{prefix}:ApiServerUrl must use the https scheme (scheme '{parsed.Scheme}' is not allowed).");
            }
        }

        if (!options.InClusterAutoDetect && string.IsNullOrWhiteSpace(options.ApiServerUrl))
        {
            failures.Add($"{prefix}:ApiServerUrl must be configured when InClusterAutoDetect is disabled.");
        }

        if (!string.IsNullOrWhiteSpace(options.CaBundlePath))
        {
            if (!File.Exists(options.CaBundlePath))
            {
                failures.Add($"{prefix}:CaBundlePath '{options.CaBundlePath}' does not exist or is unreadable.");
            }
            else
            {
                ValidateCaBundleContents(options.CaBundlePath!, prefix, failures);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.BearerTokenPath) && !File.Exists(options.BearerTokenPath))
        {
            failures.Add($"{prefix}:BearerTokenPath '{options.BearerTokenPath}' does not exist or is unreadable.");
        }

        foreach (var failure in KubernetesJobEnvironmentBuilder.ValidateDefaults(options))
        {
            failures.Add(failure.Message);
        }
    }

    // Refuse malformed Secret/ConfigMap references on Kubernetes Job workloads at startup instead of
    // failing every job submitted to them at runtime. Messages name keys only, never values.
    private static void ValidateKubernetesWorkloadParameters(
        List<ExecutionWorkloadOptions> workloads,
        List<string> failures)
    {
        for (var i = 0; i < workloads.Count; i++)
        {
            var workload = workloads[i];
            if (!string.Equals(
                    workload.Backend?.Trim(),
                    KubernetesJobBatchComputeBackend.BackendId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = ConfigurationOperationCatalogHelpers.BuildParameters(
                workload.Parameters,
                workload.ParameterEntries);
            foreach (var failure in KubernetesJobEnvironmentBuilder.ValidateParameters(parameters))
            {
                failures.Add(
                    $"ControlPlane:ExecutionWorkloads:{i} (WorkloadId '{workload.WorkloadId}'): {failure.Message}");
            }
        }
    }

    // Empty/malformed PEM files otherwise pass existence-only validation and then
    // silently fall back to the OS trust store at runtime
    // (KubernetesJobClient.CreatePrimaryHandler swallows import exceptions), which
    // masks a misconfiguration as TLS failures against private-CA clusters. Fail
    // startup so the operator is told upfront.
    private static void ValidateCaBundleContents(string path, string prefix, List<string> failures)
    {
        try
        {
            var collection = new X509Certificate2Collection();
            collection.ImportFromPemFile(path);
            if (collection.Count == 0)
            {
                failures.Add(
                    $"{prefix}:CaBundlePath '{path}' does not contain any PEM-encoded certificates.");
            }
        }
        catch (CryptographicException ex)
        {
            failures.Add(
                $"{prefix}:CaBundlePath '{path}' is not a valid PEM certificate bundle: {ex.Message}");
        }
        catch (IOException ex)
        {
            failures.Add($"{prefix}:CaBundlePath '{path}' could not be read: {ex.Message}");
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
        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');
    }

    private static bool ContainsInvalidHeaderValueCharacter(string value)
    {
        return value.Any(character => (character < 0x20 && character != '\t') || character == 0x7f);
    }
}
