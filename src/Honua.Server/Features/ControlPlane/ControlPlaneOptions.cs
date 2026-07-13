// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.ControlPlane;

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

    /// <summary>
    /// Optional versioned platform release that co-versions the serving and geoprocessing planes
    /// (ADR-0060 WS2). When declared, both catalogs project their artifact from it: deploy targets
    /// without an explicit artifact inherit <see cref="PlatformReleaseOptions.ServingArtifactReference"/>
    /// and execution workloads without an explicit artifact inherit the matching worker image, so an
    /// upgrade is a single diff bumping both planes together.
    /// </summary>
    public PlatformReleaseOptions PlatformRelease { get; set; } = new();

    /// <summary>
    /// Deployment substrate profile used to fail closed when the single-host local batch-compute
    /// backends are registered on a substrate they cannot work on (serverless, or multi-node without
    /// a shared work directory). Defaults to the single-host on-prem profile so existing deployments
    /// are unaffected.
    /// </summary>
    public SubstrateOptions Substrate { get; set; } = new();
}

/// <summary>
/// Configuration for the deployment substrate profile (<c>ControlPlane:Substrate</c>).
/// </summary>
internal sealed class SubstrateOptions
{
    /// <summary>
    /// The declared deployment substrate profile. Left at
    /// <see cref="BatchComputeSubstrateProfile.SingleHost"/> for the on-prem/air-gapped default;
    /// set to <c>MultiNode</c> or <c>Serverless</c> to declare a scale-out/ephemeral substrate.
    /// </summary>
    public BatchComputeSubstrateProfile Profile { get; set; } = BatchComputeSubstrateProfile.SingleHost;

    /// <summary>
    /// Operator assertion that a shared/persistent work directory reachable from every node is
    /// configured, which is what makes the local process-pool backend safe on a multi-node substrate.
    /// Ignored for the single-host and serverless profiles.
    /// </summary>
    public bool SharedWorkDir { get; set; }

    /// <summary>
    /// Whether to auto-escalate the effective profile to <see cref="BatchComputeSubstrateProfile.Serverless"/>
    /// when a well-known serverless runtime is detected from the environment (AWS Lambda, Azure
    /// Functions, Cloud Run). Defaults to <c>true</c>; the explicit <see cref="Profile"/> always wins
    /// when it declares a more restrictive substrate.
    /// </summary>
    public bool AutoDetectServerless { get; set; } = true;
}

/// <summary>
/// Configuration model for the versioned platform release desired state.
/// </summary>
internal sealed class PlatformReleaseOptions
{
    /// <summary>
    /// Stable release version that co-versions both planes (for example <c>2026.07.0</c>).
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Serving-plane artifact/image reference projected onto deploy targets that pin no explicit artifact.
    /// </summary>
    public string? ServingArtifactReference { get; set; }

    /// <summary>
    /// Geoprocessing worker images declared by this release, keyed by runtime profile.
    /// </summary>
    public List<PlatformReleaseWorkerImageOptions> Workers { get; set; } = [];

    /// <summary>
    /// Whether this section is at its declared-nothing default and should be treated as "no release".
    /// </summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Version) &&
        string.IsNullOrWhiteSpace(ServingArtifactReference) &&
        Workers.Count == 0;

    /// <summary>
    /// Maps this configuration section into the shared <see cref="PlatformReleaseDefinition"/> domain
    /// model, or returns null when no platform release is declared.
    /// </summary>
    /// <returns>The mapped release definition, or null when the section is empty.</returns>
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

/// <summary>
/// Configuration model for a single platform-release geoprocessing worker image.
/// </summary>
internal sealed class PlatformReleaseWorkerImageOptions
{
    /// <summary>
    /// Runtime profile (workload family) this image serves; empty declares the default image.
    /// </summary>
    public string? RuntimeProfile { get; set; }

    /// <summary>
    /// Worker container image / artifact reference for this runtime profile.
    /// </summary>
    public string ArtifactReference { get; set; } = string.Empty;
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

    /// <summary>
    /// Metrics backend that executes this connection's deploy-gate queries. Supported values:
    /// <c>prometheus</c> (default) and <c>cloudwatch</c>. An unsupported value disables
    /// auto-rollback signals for the connection and is logged at startup-evaluation time.
    /// </summary>
    public string Provider { get; set; } = "prometheus";

    /// <summary>
    /// For <c>prometheus</c>, the HTTPS base URL of the query API. For cloud providers such as
    /// <c>cloudwatch</c>, the regional endpoint (for example
    /// <c>https://monitoring.us-east-1.amazonaws.com</c>) used only to infer the region when
    /// <see cref="Region"/> is not set.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string QueryPath { get; set; } = "/api/v1/query";

    public string? AuthHeaderName { get; set; }

    public string? AuthHeaderValue { get; set; }

    /// <summary>
    /// Optional AWS region (for example <c>us-east-1</c>) for the <c>cloudwatch</c> provider.
    /// When unset the region is inferred from <see cref="BaseUrl"/>; failing that the AWS SDK's
    /// default region resolution applies. Ignored by the <c>prometheus</c> provider.
    /// </summary>
    public string? Region { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Explicit operator opt-in that relaxes the outbound SSRF guard for this telemetry connection so
    /// a private-network or loopback metrics backend can be used — for example an on-prem/air-gapped
    /// Prometheus at <c>http://localhost:9090</c> or a <c>10.x</c>/<c>192.168.x</c> address. When
    /// <see langword="true"/>, <see cref="BaseUrl"/> may use the <c>http</c> scheme and resolve to a
    /// private, loopback, or reserved address. Defaults to <see langword="false"/>, keeping the strict
    /// HTTPS-only, no-private-destination posture. Only enable for a trusted, operator-controlled
    /// endpoint inside your own network.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
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

    /// <summary>
    /// The serving↔worker job-contract version jobs built for this workload require
    /// (ADR-0060 principle #3b). Defaults to 1 (the initial contract); raise it only when the
    /// workload's payload needs a newer worker, so the dispatcher can refuse to submit to a
    /// backend whose workers cannot run it mid-upgrade.
    /// </summary>
    public int ContractVersion { get; set; } = 1;

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
    /// <summary>
    /// The runtime-profile discriminator carried by a custom-code execution workload.
    /// Kept as a literal (mirroring <c>ExecutionWorkloadGate</c>'s literal contract
    /// keys) so this validator takes no dependency on the internal
    /// <c>Honua.Geoprocessing</c> <c>CustomCodeJobContract.RuntimeProfile</c> constant,
    /// whose value this must stay in lockstep with.
    /// </summary>
    private const string CustomCodeRuntimeProfile = "custom-code";

    protected override void ValidateOptions(ControlPlaneOptions options, List<string> failures)
    {
        ValidateCustomCodeWorkloadsAreBatchOnly(options.ExecutionWorkloads, failures);

        ValidateKubernetes(options.Kubernetes, failures);

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

    /// <summary>
    /// Enforces the custom-code-is-AWS-Batch-only policy (ADR-0063) at startup: a
    /// configuration-declared execution workload that carries the
    /// <see cref="CustomCodeRuntimeProfile"/> runtime profile may target ONLY the AWS
    /// Batch backend family (<see cref="BatchComputeTargetKind.AwsBatch"/>).
    /// </summary>
    /// <remarks>
    /// Custom (operator-supplied, untrusted) geoprocessing code must run only inside an
    /// isolated, cloud-managed AWS Batch container, never in-process/on-host with
    /// honua-server. On-host batch backends exist for ordinary trusted workloads
    /// (<c>LocalBatchComputeBackend</c> = <c>local</c>/KubernetesJob family,
    /// <c>LocalProcessPoolBatchComputeBackend</c> = <c>honua-local-process</c>/LocalProcess
    /// family), and other cloud families (Azure Batch, Kubernetes Job) are not sanctioned
    /// for custom code. Pointing a <c>custom-code</c> workload at any of them would route
    /// untrusted code onto a non-AWS-Batch substrate, so we fail startup here rather than
    /// let it silently land. The in-process claim fence
    /// (<c>CustomCodeDispatchJobExecutor</c>) is the runtime backstop; this is the
    /// configuration-time gate. Local-process execution was evaluated in the closed
    /// honua-server#2672 and rejected — reintroducing it must trip this explicit gate.
    /// </remarks>
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
