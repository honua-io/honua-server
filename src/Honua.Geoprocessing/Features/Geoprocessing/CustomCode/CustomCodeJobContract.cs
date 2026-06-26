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
    public const string RuntimeProfile = "custom-code";

    /// <summary>The only runtime supported by the MVP.</summary>
    public const string PythonRuntime = "python";

    // --- caller-supplied customcode.* parameters --------------------------------

    /// <summary>Runtime selector; MVP accepts only <see cref="PythonRuntime"/>.</summary>
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

    // --- injected env.* pass-through (AwsBatchComputeBackend.BuildEnvironmentOverrides) ---

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

    // --- customcode.* -> env.CUSTOMCODE_* job-input pass-through ----------------
    //
    // The harness (docker/worker-customcode-python/harness, built on the
    // feat/customcode-python-image branch) reads each job input from a discrete
    // CUSTOMCODE_<UPPER> environment variable (the spec key uppercased). The
    // server therefore re-emits every caller/server-set customcode.* parameter as
    // an env.CUSTOMCODE_<UPPER> key so AwsBatchComputeBackend.BuildEnvironmentOverrides
    // surfaces it to the Batch container under the exact name the harness reads.
    // The auth spine (HONUA_BASE_URL/HONUA_JOB_TOKEN) is injected separately and is
    // intentionally NOT part of this body map — it follows the standard secret path.
    //
    // CONTRACT GUARD: CustomCodeJobContractDriftTests pins ParameterToEnv against the
    // harness's checked-in jobspec field map so this seam cannot silently drift.

    /// <summary>Container env var carrying <see cref="RuntimeParam"/> (<c>customcode.runtime</c>).</summary>
    public const string RuntimeEnvName = "CUSTOMCODE_RUNTIME";

    /// <summary>Container env var carrying <see cref="RepoUrlParam"/> (<c>customcode.repo_url</c>).</summary>
    public const string RepoUrlEnvName = "CUSTOMCODE_REPO_URL";

    /// <summary>Container env var carrying <see cref="GitRefParam"/> (<c>customcode.git_ref</c>).</summary>
    public const string GitRefEnvName = "CUSTOMCODE_GIT_REF";

    /// <summary>Container env var carrying <see cref="EntrypointParam"/> (<c>customcode.entrypoint</c>).</summary>
    public const string EntrypointEnvName = "CUSTOMCODE_ENTRYPOINT";

    /// <summary>Container env var carrying <see cref="DepsManifestParam"/> (<c>customcode.deps_manifest</c>).</summary>
    public const string DepsManifestEnvName = "CUSTOMCODE_DEPS_MANIFEST";

    /// <summary>Container env var carrying <see cref="ParamsJsonParam"/> (<c>customcode.params_json</c>).</summary>
    public const string ParamsJsonEnvName = "CUSTOMCODE_PARAMS_JSON";

    /// <summary>Container env var carrying <see cref="OutputPrefixParam"/> (<c>customcode.output_prefix</c>).</summary>
    public const string OutputPrefixEnvName = "CUSTOMCODE_OUTPUT_PREFIX";

    /// <summary>Container env var carrying <see cref="DeclaredScopeParam"/> (<c>customcode.declared_scope</c>).</summary>
    public const string DeclaredScopeEnvName = "CUSTOMCODE_DECLARED_SCOPE";

    /// <summary>
    /// The pinned, ordered <c>customcode.*</c> spec-parameter to container
    /// environment-variable name map. Every entry here is re-emitted as an
    /// <c>env.&lt;value&gt;</c> spec parameter at submit so the Batch pass-through
    /// surfaces it to the harness. Keep this in lockstep with the harness's
    /// jobspec field mapping; the drift test enforces it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParameterToEnv { get; } =
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
}
