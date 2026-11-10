// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing.CustomCode;

public static class CustomCodeJobContract
{
    public const string RuntimeProfile = "custom-code";

    // A server must not submit a job to a worker whose contract version is below this one.
    public const int ContractVersion = 1;

    // A missing contract-version variable is treated as version 1 so older workers still parse.
    public const string ContractVersionEnvName = "HONUA_CONTRACT_VERSION";

    public const string PythonRuntime = "python";

    public const string DotnetRuntime = "dotnet";

    public static readonly IReadOnlySet<string> SupportedRuntimes =
        new HashSet<string>(StringComparer.Ordinal) { PythonRuntime, DotnetRuntime };

    // --- caller-supplied customcode.* parameters --------------------------------

    public const string RuntimeParam = "customcode.runtime";

    public const string RepoUrlParam = "customcode.repo_url";

    public const string GitRefParam = "customcode.git_ref";

    public const string EntrypointParam = "customcode.entrypoint";

    public const string DepsManifestParam = "customcode.deps_manifest";

    public const string ParamsJsonParam = "customcode.params_json";

    public const string DeclaredScopeParam = "customcode.declared_scope";

    // --- server-set customcode.* parameters (never caller-supplied) -------------

    // The output prefix is assigned by the server, and a caller-supplied value is refused, so user code cannot write outside the job prefix.
    public const string OutputPrefixParam = "customcode.output_prefix";

    // --- injected env.* pass-through (batch backend container environment) ---

    public const string BaseUrlEnvParam = "env.HONUA_BASE_URL";

    public const string JobTokenEnvParam = "env.HONUA_JOB_TOKEN";

    public const string BaseUrlEnvName = "HONUA_BASE_URL";

    public const string JobTokenEnvName = "HONUA_JOB_TOKEN";

    // --- projected CUSTOMCODE_* container env var names (the harness reads these) --
    //
    // The user-code harness (docker/worker-customcode-python/harness/jobspec.py) reads
    // its job definition from either a CUSTOMCODE_JOB_SPEC file or these discrete
    // CUSTOMCODE_* environment variables. The server projects each present
    // customcode.* spec parameter to the matching env.CUSTOMCODE_* spec key so
    // batch backends surface it to the container under these exact names. These names are the
    // SERVER half of a cross-piece contract and must match the harness 1:1 (#2191).

    public const string EnvParamPrefix = "env.";

    public const string RuntimeEnvName = "CUSTOMCODE_RUNTIME";

    public const string RepoUrlEnvName = "CUSTOMCODE_REPO_URL";

    public const string GitRefEnvName = "CUSTOMCODE_GIT_REF";

    public const string EntrypointEnvName = "CUSTOMCODE_ENTRYPOINT";

    public const string DepsManifestEnvName = "CUSTOMCODE_DEPS_MANIFEST";

    public const string ParamsJsonEnvName = "CUSTOMCODE_PARAMS_JSON";

    public const string OutputPrefixEnvName = "CUSTOMCODE_OUTPUT_PREFIX";

    public const string DeclaredScopeEnvName = "CUSTOMCODE_DECLARED_SCOPE";

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

    public static IReadOnlyDictionary<string, string> ParameterToEnv => ParameterToEnvName;

    public static string ToEnvParamKey(string envName) => EnvParamPrefix + envName;
}
