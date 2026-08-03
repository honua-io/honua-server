// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.CustomCode;

/// <summary>
/// The wire contract for a <c>custom-code</c> geoprocessing job — the
/// <c>customcode.*</c> parameter keys carried on
/// <see cref="Honua.Core.Features.ControlPlane.Domain.ExecutionJobSpec.Parameters"/>
/// and the <c>env.*</c> keys the server injects into the Batch container at submit.
/// </summary>
/// <remarks>
/// This is the SERVER half of a contract shared with the user-code harness (which
/// runs inside the Batch container, built separately) and the iac (which builds the
/// custom-code Batch job-definition family separately). Both must match these exact
/// names.
/// </remarks>
public static class CustomCodeJobContract
{
    /// <summary>
    /// The <see cref="Honua.Core.Features.ControlPlane.Domain.RuntimeProfiles"/>
    /// value that fences a custom-code job to the custom-code dispatch executor and
    /// the custom-code Batch workload. A job carrying this profile is never claimed
    /// by the lean managed dispatcher or the native GDAL worker.
    /// </summary>
    public const string RuntimeProfile =
        Honua.Core.Features.ControlPlane.Domain.RuntimeProfiles.CustomCode;

    /// <summary>
    /// The current integer version of the serving↔GP-worker job contract (ADR-0060 principle #3b).
    /// Bumped whenever the shape of the job the server submits changes in a way an older worker
    /// cannot run. A vX server must not submit a job to a worker whose
    /// <see cref="Honua.Core.Features.ControlPlane.Domain.BatchComputeBackendCapabilities.MaxSupportedContractVersion"/>
    /// is below this, and the worker harness re-checks the injected
    /// <see cref="ContractVersionEnvName"/> as defense in depth.
    /// </summary>
    public const int ContractVersion = 1;

    /// <summary>
    /// The container environment variable carrying the job-contract version the submitting server
    /// used. Absent is treated as version 1 for back-compat with pre-versioning workers.
    /// </summary>
    public const string ContractVersionEnvName = "HONUA_CONTRACT_VERSION";

    /// <summary>The Python custom-code runtime (Phase 1).</summary>
    public const string PythonRuntime = "python";

    /// <summary>The .NET custom-code runtime (Phase 2).</summary>
    public const string DotnetRuntime = "dotnet";

    /// <summary>
    /// The runtimes the custom-code submit gate accepts. The runtime selects only
    /// the per-job container image (resolved by the iac job-definition family — the
    /// runtime value flows through the spec parameters to the Batch container); every
    /// security control (SHA-only pin, repo allowlist, scope ⊆ owner, scoped-token
    /// mint/inject/revoke) and the routing fence are runtime-agnostic.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedRuntimes =
        new HashSet<string>(StringComparer.Ordinal) { PythonRuntime, DotnetRuntime };

    // --- caller-supplied customcode.* parameters --------------------------------

    /// <summary>
    /// Runtime selector; accepts <see cref="PythonRuntime"/> or
    /// <see cref="DotnetRuntime"/> (see <see cref="SupportedRuntimes"/>).
    /// </summary>
    public const string RuntimeParam = "customcode.runtime";

    /// <summary>HTTPS git repository URL holding the user code (allowlist-checked).</summary>
    public const string RepoUrlParam = "customcode.repo_url";

    /// <summary>Full 40-hex commit SHA to check out (branches/tags are rejected).</summary>
    public const string GitRefParam = "customcode.git_ref";

    /// <summary>Entrypoint in <c>module.path:function</c> form.</summary>
    public const string EntrypointParam = "customcode.entrypoint";

    /// <summary>Relative path to the dependency manifest (e.g. <c>requirements.txt</c>).</summary>
    public const string DepsManifestParam = "customcode.deps_manifest";

    /// <summary>Opaque user parameters, passed through verbatim to the user code.</summary>
    public const string ParamsJsonParam = "customcode.params_json";

    /// <summary>
    /// Declared resource scope as JSON: <c>[{"serviceId":"x","layerId":"y","access":"read|write"}]</c>.
    /// Validated to be ⊆ what the submitter can reach; the scoped token is bound to
    /// the intersection of this and the owner snapshot.
    /// </summary>
    public const string DeclaredScopeParam = "customcode.declared_scope";

    // --- server-set customcode.* parameters (never caller-supplied) -------------

    /// <summary>
    /// The per-job S3 output prefix. SERVER-SET only: a caller-supplied value is
    /// overwritten so user code can never redirect its outputs outside its job's
    /// isolated prefix.
    /// </summary>
    public const string OutputPrefixParam = "customcode.output_prefix";

    // --- injected env.* pass-through (batch backend container environment) ---

    /// <summary>
    /// Spec-parameter key that injects the Honua API base URL into the container as
    /// the <c>HONUA_BASE_URL</c> environment variable.
    /// </summary>
    public const string BaseUrlEnvParam = "env.HONUA_BASE_URL";

    /// <summary>
    /// Spec-parameter key that injects the scoped, job-bound callback token into the
    /// container as the <c>HONUA_JOB_TOKEN</c> environment variable.
    /// </summary>
    public const string JobTokenEnvParam = "env.HONUA_JOB_TOKEN";

    /// <summary>The container environment variable name for the Honua API base URL.</summary>
    public const string BaseUrlEnvName = "HONUA_BASE_URL";

    /// <summary>The container environment variable name for the scoped job token.</summary>
    public const string JobTokenEnvName = "HONUA_JOB_TOKEN";

    // --- projected CUSTOMCODE_* container env var names (the harness reads these) --
    //
    // The user-code harness (docker/worker-customcode-python/harness/jobspec.py) reads
    // its job definition from either a CUSTOMCODE_JOB_SPEC file or these discrete
    // CUSTOMCODE_* environment variables. The server projects each present
    // customcode.* spec parameter to the matching env.CUSTOMCODE_* spec key so
    // batch backends surface it to the container under these exact names. These names are the
    // SERVER half of a cross-piece contract and must match the harness 1:1 (#2191).

    /// <summary>The <c>env.</c> spec-parameter prefix the Batch backend strips to derive a container env var name.</summary>
    public const string EnvParamPrefix = "env.";

    /// <summary>Container env var name carrying the runtime selector.</summary>
    public const string RuntimeEnvName = "CUSTOMCODE_RUNTIME";

    /// <summary>Container env var name carrying the git repository URL.</summary>
    public const string RepoUrlEnvName = "CUSTOMCODE_REPO_URL";

    /// <summary>Container env var name carrying the git commit SHA.</summary>
    public const string GitRefEnvName = "CUSTOMCODE_GIT_REF";

    /// <summary>Container env var name carrying the entrypoint.</summary>
    public const string EntrypointEnvName = "CUSTOMCODE_ENTRYPOINT";

    /// <summary>Container env var name carrying the dependency manifest path.</summary>
    public const string DepsManifestEnvName = "CUSTOMCODE_DEPS_MANIFEST";

    /// <summary>Container env var name carrying the opaque user parameters JSON.</summary>
    public const string ParamsJsonEnvName = "CUSTOMCODE_PARAMS_JSON";

    /// <summary>Container env var name carrying the server-set output prefix.</summary>
    public const string OutputPrefixEnvName = "CUSTOMCODE_OUTPUT_PREFIX";

    /// <summary>Container env var name carrying the advisory declared scope.</summary>
    public const string DeclaredScopeEnvName = "CUSTOMCODE_DECLARED_SCOPE";

    /// <summary>
    /// The canonical <c>customcode.*</c> spec-parameter key to container
    /// <c>CUSTOMCODE_*</c> environment-variable name map. The server projects every
    /// present source parameter through this map to the matching
    /// <c>env.CUSTOMCODE_*</c> spec key; the harness reads the resulting container
    /// env vars by these exact names. This is the pinned param-to-env contract the
    /// #2191 contract test asserts against.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParameterToEnvName { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RuntimeParam] = RuntimeEnvName,
            [RepoUrlParam] = RepoUrlEnvName,
            [GitRefParam] = GitRefEnvName,
            [EntrypointParam] = EntrypointEnvName,
            [DepsManifestParam] = DepsManifestEnvName,
            [ParamsJsonParam] = ParamsJsonEnvName,
            [OutputPrefixParam] = OutputPrefixEnvName,
            [DeclaredScopeParam] = DeclaredScopeEnvName,
        };

    /// <summary>
    /// Alias of <see cref="ParameterToEnvName"/> under the name the harness drift
    /// guard (<c>CustomCodeJobContractDriftTests</c>, from the Round-4 customcode
    /// pass-through work) pins against. The two names refer to the SAME pinned
    /// <c>customcode.*</c> → <c>CUSTOMCODE_*</c> map; keep them in lockstep.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParameterToEnv => ParameterToEnvName;

    /// <summary>
    /// Builds the <c>env.</c>-prefixed spec-parameter key for a container env var
    /// name (e.g. <c>CUSTOMCODE_REPO_URL</c> → <c>env.CUSTOMCODE_REPO_URL</c>) so the
    /// Batch backend surfaces it to the container.
    /// </summary>
    /// <param name="envName">The container environment variable name.</param>
    /// <returns>The <c>env.</c>-prefixed spec-parameter key.</returns>
    public static string ToEnvParamKey(string envName) => EnvParamPrefix + envName;
}
