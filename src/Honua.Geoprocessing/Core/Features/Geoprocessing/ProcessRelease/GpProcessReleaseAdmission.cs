// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Geoprocessing.CustomCode;
using Microsoft.Extensions.Logging;

namespace Honua.Geoprocessing.ProcessRelease;

public sealed partial class GpProcessReleaseAdmission
{
    public const string ProcessParam = "customcode.process";

    public const string ReleaseProcessParam = "honua.release.process";

    public const string ReleaseVersionParam = "honua.release.version";

    public const string ReleaseLockDigestParam = "honua.release.lock_digest";

    public const string ReleaseBindingVersionParam = "honua.release.binding_version";

    public const string KubernetesImageParam = "k8s.image";

    public const string AwsBatchJobDefinitionParam = "batch.job_definition_arn";

    public const string WorkerImageParam = "honua.release.worker_image";

    public const string DependencyDigestParam = "honua.release.dependency_digest";

    public const string WorkflowVersionParam = "honua.release.workflow_version";

    private readonly IGpProcessReleaseStore _store;
    private readonly ILogger<GpProcessReleaseAdmission> _logger;

    public GpProcessReleaseAdmission(IGpProcessReleaseStore store, ILogger<GpProcessReleaseAdmission> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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

        // The Python harness invokes pip install -r, not uv/Poetry/Pipenv. Require
        // the requirements-file naming contract before stamping or admitting a job.
        if (lockDocument.Process.Runtime == CustomCodeJobContract.PythonRuntime
            && !lockDocument.DependencyLock.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFileName(lockDocument.DependencyLock.Path), "requirements.lock", StringComparison.OrdinalIgnoreCase))
        {
            throw new GeoprocessingValidationException(
                "A Python process release requires a pip-compatible requirements file named *.txt or requirements.lock; export uv/Poetry/Pipenv locks before building the release.");
        }

        // The release decides what runs. A caller may repeat the same values but never different
        // ones: a submission that names a release cannot also point at other code.
        Stamp(specParams, CustomCodeJobContract.RuntimeParam, lockDocument.Process.Runtime, process);
        Stamp(specParams, CustomCodeJobContract.RepoUrlParam, RepositoryUrl(lockDocument.Source.Repository), process);
        Stamp(specParams, CustomCodeJobContract.GitRefParam, lockDocument.Source.Sha, process);
        Stamp(specParams, CustomCodeJobContract.EntrypointParam, lockDocument.Process.Entrypoint, process);
        if (lockDocument.Process.Runtime == CustomCodeJobContract.PythonRuntime)
        {
            Stamp(specParams, CustomCodeJobContract.DepsManifestParam, lockDocument.DependencyLock.Path, process);
        }

        specParams[ProcessParam] = process;
        specParams[ReleaseProcessParam] = process;
        specParams[ReleaseVersionParam] = lockDocument.Process.Version;
        specParams[DependencyDigestParam] = lockDocument.DependencyLock.Sha256;
        specParams[WorkflowVersionParam] = lockDocument.WorkflowVersion;
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

    // The v1 lock records a dependency lock, not a .NET build project. Resolve the
    // project from the matching registered workload and persist it before token issuance;
    // passing packages.lock.json to dotnet build would fail every .NET release job.
    internal static void ApplyWorkloadManifest(
        Dictionary<string, string> specParams,
        IReadOnlyDictionary<string, string> workloadParameters)
    {
        if (specParams.GetValueOrDefault(CustomCodeJobContract.RuntimeParam) != CustomCodeJobContract.DotnetRuntime)
        {
            return;
        }

        if (!workloadParameters.TryGetValue(CustomCodeJobContract.DepsManifestParam, out var project)
            || string.IsNullOrWhiteSpace(project)
            || !project.Trim().EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new GeoprocessingValidationException("A .NET process release requires customcode.deps_manifest naming its .csproj on the matching registered workload.");
        }

        Stamp(specParams, CustomCodeJobContract.DepsManifestParam, project.Trim(), specParams[ProcessParam]);
    }

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
