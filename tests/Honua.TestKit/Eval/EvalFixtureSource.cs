// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Eval;

/// <summary>
/// Resolves the fixture corpus backing a scenario run. Two implementations are
/// supported: the shared geospatial-mcp corpus (pointed to by
/// <c>HONUA_EVAL_CORPUS_PATH</c>) and the in-repo <c>tests/seed/seed.yaml</c>
/// baseline applied through <see cref="PostgresFixture"/>.
/// </summary>
public interface IEvalFixtureSource
{
    /// <summary>Identifier: <c>shared</c> or <c>local-seed</c>.</summary>
    string Id { get; }

    /// <summary>Corpus version string used in the report envelope.</summary>
    string CorpusVersion { get; }

    /// <summary>Resolved corpus path when applicable (shared corpus only).</summary>
    string? CorpusPath { get; }
}

/// <summary>
/// Binds scenarios to the shared geospatial-mcp fixture corpus mounted at
/// <c>HONUA_EVAL_CORPUS_PATH</c>. Not yet populated in CI; in-repo seed is used
/// as the fallback when the env var is unset.
/// </summary>
public sealed class SharedCorpusFixtureSource : IEvalFixtureSource
{
    /// <summary>Name of the env var used to locate the shared corpus.</summary>
    public const string CorpusPathEnvVar = "HONUA_EVAL_CORPUS_PATH";

    /// <summary>Name of the env var used to report the shared corpus version.</summary>
    public const string CorpusVersionEnvVar = "HONUA_EVAL_CORPUS_VERSION";

    private SharedCorpusFixtureSource(string corpusPath, string corpusVersion)
    {
        CorpusPath = corpusPath;
        CorpusVersion = corpusVersion;
    }

    /// <inheritdoc />
    public string Id => "shared";

    /// <inheritdoc />
    public string CorpusVersion { get; }

    /// <inheritdoc />
    public string? CorpusPath { get; }

    /// <summary>
    /// Attempts to bind to the shared corpus via <see cref="CorpusPathEnvVar"/>. Returns
    /// <c>null</c> when the env var is unset or the directory does not exist.
    /// </summary>
    public static SharedCorpusFixtureSource? TryCreate()
    {
        var path = Environment.GetEnvironmentVariable(CorpusPathEnvVar);
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        var version = Environment.GetEnvironmentVariable(CorpusVersionEnvVar);
        if (string.IsNullOrWhiteSpace(version))
        {
            version = "shared-unversioned";
        }

        return new SharedCorpusFixtureSource(path, version);
    }
}

/// <summary>
/// Falls back to the in-repo <c>tests/seed/seed.yaml</c> baseline so the harness
/// runs locally without external fixture mounts. Satisfies the contract's
/// "consume shared fixtures instead of ad hoc local-only test data" AC through the
/// same <c>SeedRunner</c> used by other integration tests.
/// </summary>
public sealed class LocalSeedFixtureSource : IEvalFixtureSource
{
    /// <inheritdoc />
    public string Id => "local-seed";

    /// <inheritdoc />
    public string CorpusVersion => "seed.yaml@v1";

    /// <inheritdoc />
    public string? CorpusPath => null;
}
