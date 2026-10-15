// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Geoprocessing.CustomCode;
using Microsoft.Extensions.Logging;

namespace Honua.Geoprocessing.ProcessRelease;

/// <summary>
/// Admission-time release pinning (honua-server#6221). A custom-code submission that names a
/// logical process (<c>customcode.process</c>) is resolved against the process's live binding
/// once, here, and the resolved identity is stamped into the durable spec parameters: the
/// source commit, entrypoint, runtime and dependency manifest from the lock, the pinned worker
/// image for a Kubernetes Jobs target, the pinned job-definition revision for an AWS Batch
/// target, and the release identity under <c>honua.release.*</c>. Retries, requeues and
/// workflow steps read the persisted spec, so they execute the release the job was admitted
/// with; a later binding change affects subsequent admissions only.
/// </summary>
public sealed partial class GpProcessReleaseAdmission
{
    /// <summary>Request key naming the logical process whose live binding admits the job.</summary>
    public const string ProcessParam = "customcode.process";

    /// <summary>Stamped: the logical process name.</summary>
    public const string ReleaseProcessParam = "honua.release.process";

    /// <summary>Stamped: the release's semantic version.</summary>
    public const string ReleaseVersionParam = "honua.release.version";

    /// <summary>Stamped: the lock digest the job executes.</summary>
    public const string ReleaseLockDigestParam = "honua.release.lock_digest";

    /// <summary>Stamped: the binding version that admitted the job.</summary>
    public const string ReleaseBindingVersionParam = "honua.release.binding_version";

    /// <summary>Stamped: the pinned worker image for Kubernetes Jobs (read by that backend).</summary>
    public const string KubernetesImageParam = "k8s.image";

    /// <summary>Stamped: the pinned job definition revision for AWS Batch (read by that backend).</summary>
    public const string AwsBatchJobDefinitionParam = "batch.job_definition_arn";

    /// <summary>Stamped: the immutable worker image selected by the release.</summary>
    public const string WorkerImageParam = "honua.release.worker_image";

    private readonly IGpProcessReleaseStore _store;
    private readonly ILogger<GpProcessReleaseAdmission> _logger;

    /// <summary>Creates the admission resolver.</summary>
    public GpProcessReleaseAdmission(IGpProcessReleaseStore store, ILogger<GpProcessReleaseAdmission> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolves the binding named by <see cref="ProcessParam"/> and stamps the release identity
    /// into <paramref name="specParams"/>. A submission without the key is left untouched.
    /// Provider identities are stamped for every target the binding carries; each backend reads
    /// only its own key, so the job runs the pinned release on whichever target it routes to.
    /// </summary>
    /// <param name="specParams">The durable parameter bag being built for the job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The binding that admitted the job, or null when no process was named.</returns>
    /// <exception cref="GeoprocessingValidationException">
    /// The process has no live binding, the bound lock is not published, or the caller's
    /// explicit custom-code values contradict the release.
    /// </exception>
    public async Task<GpProcessBinding?> ApplyAsync(Dictionary<string, string> specParams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specParams);
        if (!specParams.TryGetValue(ProcessParam, out var process))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(process))
        {
            throw new GeoprocessingValidationException("customcode.process must name a process release binding.");
        }

        process = process.Trim();
        var binding = await _store.GetBindingAsync(process, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingValidationException(
                $"Process '{process}' has no active release binding on this server; activate a release before submitting against it.");

        var release = await _store.GetReleaseAsync(binding.LockDigest, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingValidationException(
                $"Process '{process}' is bound to release {binding.LockDigest}, but that lock is not published on this server.");
        var lockDocument = release.Lock;

        // The release decides what runs. A caller may repeat the same values but never different
        // ones: a submission that names a release cannot also point at other code.
        Stamp(specParams, CustomCodeJobContract.RuntimeParam, lockDocument.Process.Runtime, process);
        Stamp(specParams, CustomCodeJobContract.RepoUrlParam, RepositoryUrl(lockDocument.Source.Repository), process);
        Stamp(specParams, CustomCodeJobContract.GitRefParam, lockDocument.Source.Sha, process);
        Stamp(specParams, CustomCodeJobContract.EntrypointParam, lockDocument.Process.Entrypoint, process);
        Stamp(specParams, CustomCodeJobContract.DepsManifestParam, lockDocument.DependencyLock.Path, process);

        specParams[ProcessParam] = process;
        specParams[ReleaseProcessParam] = process;
        specParams[ReleaseVersionParam] = lockDocument.Process.Version;
        specParams[ReleaseLockDigestParam] = binding.LockDigest;
        specParams[ReleaseBindingVersionParam] = binding.BindingVersion.ToString(CultureInfo.InvariantCulture);

        foreach (var target in binding.Targets)
        {
            if (string.Equals(target.Kind, GpProcessReleaseActivationPolicy.KubernetesJobTarget, StringComparison.Ordinal))
            {
                specParams[KubernetesImageParam] = target.Image;
            }
            else if (string.Equals(target.Kind, GpProcessReleaseActivationPolicy.AwsBatchTarget, StringComparison.Ordinal) && target.JobDefinitionArn is not null)
            {
                specParams[AwsBatchJobDefinitionParam] = target.JobDefinitionArn;
                specParams[WorkerImageParam] = target.Image;
            }
        }

        Log.ReleasePinned(_logger, process, lockDocument.Process.Version, binding.BindingVersion, binding.Targets.Count);
        return binding;
    }

    /// <summary>The HTTPS clone URL for an <c>owner/name</c> repository.</summary>
    public static string RepositoryUrl(string repository) => "https://github.com/" + repository;

    private static void Stamp(Dictionary<string, string> specParams, string key, string value, string process)
    {
        if (specParams.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing)
            && !string.Equals(existing.Trim(), value, StringComparison.Ordinal))
        {
            throw new GeoprocessingValidationException(
                $"'{key}' contradicts the active process release; omit it or submit against the release.");
        }

        specParams[key] = value;
    }

    /// <summary>Prevents workload defaults from replacing a server-resolved release identity.</summary>
    internal static bool IsPinnedParameter(string key, IReadOnlyDictionary<string, string> parameters)
        => parameters.ContainsKey(ReleaseLockDigestParam)
            && (key.StartsWith("honua.release.", StringComparison.Ordinal)
                || key is ProcessParam or CustomCodeJobContract.RuntimeParam
                    or CustomCodeJobContract.RepoUrlParam or CustomCodeJobContract.GitRefParam
                    or CustomCodeJobContract.EntrypointParam or CustomCodeJobContract.DepsManifestParam
                    or KubernetesImageParam or AwsBatchJobDefinitionParam
                || key.StartsWith(AwsBatchJobDefinitionParam + ".", StringComparison.Ordinal));

    private static partial class Log
    {
        [LoggerMessage(9310, LogLevel.Information, "Admitted process {Process} release {Version} under binding {BindingVersion} with {Targets} pinned target(s)")]
        public static partial void ReleasePinned(ILogger logger, string process, string version, int bindingVersion, int targets);
    }
}
