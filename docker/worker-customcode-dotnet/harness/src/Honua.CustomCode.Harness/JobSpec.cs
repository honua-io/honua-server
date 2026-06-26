// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Honua.CustomCode.Harness;

/// <summary>Raised when the job inputs are missing or malformed.</summary>
public sealed class JobSpecException(string message) : Exception(message);

/// <summary>
/// Validated, fully-resolved job inputs (the harness half of the contract). Mirrors
/// the Python harness's <c>JobSpec</c> exactly — the same env vars and spec-file
/// precedence — so a single contract serves both runtimes.
/// </summary>
/// <remarks>
/// THE CONTRACT (image/harness half). The Batch container receives the job parameters
/// in one of two interchangeable ways, resolved in this order:
/// <list type="number">
/// <item>A mounted job-spec file — if <c>CUSTOMCODE_JOB_SPEC</c> points at a readable
/// JSON file, every field is read from it (preferred for large params/deps).</item>
/// <item>Discrete <c>CUSTOMCODE_*</c> environment variables otherwise.</item>
/// </list>
/// In BOTH modes the auth spine is always env-only and never in the spec file:
/// <c>HONUA_BASE_URL</c> and <c>HONUA_JOB_TOKEN</c> are read from the environment so the
/// scoped token follows the standard Batch secret-injection path and is strippable.
/// <para>
/// Field mapping (env var -&gt; spec key):
/// <c>CUSTOMCODE_RUNTIME</c> -&gt; runtime (e.g. "dotnet");
/// <c>CUSTOMCODE_REPO_URL</c> -&gt; repo_url;
/// <c>CUSTOMCODE_GIT_REF</c> -&gt; git_ref (40-hex SHA, validated);
/// <c>CUSTOMCODE_ENTRYPOINT</c> -&gt; entrypoint ("Assembly::Namespace.Type");
/// <c>CUSTOMCODE_DEPS_MANIFEST</c> -&gt; deps_manifest (the user .csproj, repo-relative);
/// <c>CUSTOMCODE_PARAMS_JSON</c> -&gt; params_json (inline JSON OR @path);
/// <c>CUSTOMCODE_OUTPUT_PREFIX</c> -&gt; output_prefix (s3://bucket/prefix);
/// <c>CUSTOMCODE_DECLARED_SCOPE</c> -&gt; declared_scope (advisory; comma/space list);
/// <c>CUSTOMCODE_OUTPUT_MAX_BYTES</c> -&gt; output_max_bytes (int; default 1 GiB).
/// </para>
/// </remarks>
public sealed class JobSpec
{
    /// <summary>The default output cap when the job declares none (1 GiB).</summary>
    public const long DefaultOutputMaxBytes = 1L * 1024 * 1024 * 1024;

    // A git_ref MUST be a fully-pinned 40-hex commit SHA. The server resolves to a
    // pinned SHA before dispatch; the harness re-validates as defense-in-depth.
    private static readonly Regex GitShaRegex = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // .NET entrypoint: Assembly::Namespace.Type. Matches the server-side validator.
    private static readonly Regex EntrypointRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_]*::[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private JobSpec(
        string runtime,
        string repoUrl,
        string gitRef,
        string entrypoint,
        string depsManifest,
        JsonElement parameters,
        string outputPrefix,
        IReadOnlyList<string> declaredScope,
        long outputMaxBytes,
        string baseUrl,
        string jobToken)
    {
        Runtime = runtime;
        RepoUrl = repoUrl;
        GitRef = gitRef;
        Entrypoint = entrypoint;
        DepsManifest = depsManifest;
        Params = parameters;
        OutputPrefix = outputPrefix;
        DeclaredScope = declaredScope;
        OutputMaxBytes = outputMaxBytes;
        BaseUrl = baseUrl;
        JobToken = jobToken;
    }

    /// <summary>The runtime selector ("dotnet").</summary>
    public string Runtime { get; }

    /// <summary>The HTTPS git repository URL holding the user code.</summary>
    public string RepoUrl { get; }

    /// <summary>The pinned 40-hex commit SHA to check out.</summary>
    public string GitRef { get; }

    /// <summary>The entrypoint, "Assembly::Namespace.Type".</summary>
    public string Entrypoint { get; }

    /// <summary>The repo-relative path to the user's <c>.csproj</c>.</summary>
    public string DepsManifest { get; }

    /// <summary>The parsed <c>params_json</c> (tool-defined schema).</summary>
    public JsonElement Params { get; }

    /// <summary>The server-set per-job S3 output prefix.</summary>
    public string OutputPrefix { get; }

    /// <summary>The advisory declared scope (informational on the harness side).</summary>
    public IReadOnlyList<string> DeclaredScope { get; }

    /// <summary>The per-job output-size cap in bytes.</summary>
    public long OutputMaxBytes { get; }

    /// <summary>The Honua API base URL (from <c>HONUA_BASE_URL</c>).</summary>
    public string BaseUrl { get; }

    /// <summary>The scoped, job-bound bearer token (from <c>HONUA_JOB_TOKEN</c>).</summary>
    public string JobToken { get; }

    /// <summary>The assembly simple name from the entrypoint ("Asm" in "Asm::Type").</summary>
    public string EntrypointAssembly => Entrypoint.Split("::", 2)[0];

    /// <summary>The CLR type name from the entrypoint ("Type" in "Asm::Type").</summary>
    public string EntrypointType => Entrypoint.Split("::", 2)[1];

    /// <summary>Returns true iff <paramref name="value"/> is a fully-pinned 40-hex SHA.</summary>
    /// <param name="value">The candidate ref.</param>
    public static bool IsValidGitSha(string? value) => !string.IsNullOrEmpty(value) && GitShaRegex.IsMatch(value);

    /// <summary>Resolve and validate the job inputs from a spec file or env vars.</summary>
    /// <param name="env">The environment to read (defaults to the process environment).</param>
    /// <returns>The validated job spec.</returns>
    /// <exception cref="JobSpecException">When any required input is missing or malformed.</exception>
    public static JobSpec Load(IReadOnlyDictionary<string, string?>? env = null)
    {
        env ??= ReadProcessEnv();

        Func<string, string?> field;
        if (TryGet(env, "CUSTOMCODE_JOB_SPEC") is { } specPath)
        {
            var source = LoadSpecFile(specPath);
            field = key => source.TryGetValue(key, out var v) ? v : null;
        }
        else
        {
            // Spec keys map 1:1 to CUSTOMCODE_<UPPER> env vars.
            field = key => TryGet(env, "CUSTOMCODE_" + key.ToUpperInvariant());
        }

        var runtime = field("runtime") ?? "dotnet";
        var repoUrl = field("repo_url");
        var gitRef = field("git_ref");
        var entrypoint = field("entrypoint");
        var depsManifest = field("deps_manifest");
        var outputPrefix = field("output_prefix");

        // The auth spine is ALWAYS env-only — never sourced from the spec file.
        var baseUrl = TryGet(env, "HONUA_BASE_URL");
        var jobToken = TryGet(env, "HONUA_JOB_TOKEN");

        Require(repoUrl, "repo_url / CUSTOMCODE_REPO_URL");
        Require(gitRef, "git_ref / CUSTOMCODE_GIT_REF");
        Require(entrypoint, "entrypoint / CUSTOMCODE_ENTRYPOINT");
        Require(depsManifest, "deps_manifest / CUSTOMCODE_DEPS_MANIFEST");
        Require(outputPrefix, "output_prefix / CUSTOMCODE_OUTPUT_PREFIX");
        Require(baseUrl, "HONUA_BASE_URL");
        Require(jobToken, "HONUA_JOB_TOKEN");

        if (!IsValidGitSha(gitRef))
        {
            throw new JobSpecException(
                $"git_ref must be a 40-character hex commit SHA (got '{gitRef}'). " +
                "Branches, tags, and abbreviated SHAs are rejected.");
        }

        if (!EntrypointRegex.IsMatch(entrypoint!))
        {
            throw new JobSpecException(
                $"entrypoint must be 'Assembly::Namespace.Type' (got '{entrypoint}').");
        }

        if (!IsSafeRelativePath(depsManifest!))
        {
            throw new JobSpecException(
                $"deps_manifest must be a relative path within the repository (got '{depsManifest}').");
        }

        if (!StartsWithAny(repoUrl!, "https://", "git@", "ssh://", "http://"))
        {
            throw new JobSpecException($"repo_url has an unsupported scheme: '{repoUrl}'.");
        }

        if (!outputPrefix!.StartsWith("s3://", StringComparison.Ordinal))
        {
            throw new JobSpecException($"output_prefix must be an s3:// URI, got '{outputPrefix}'.");
        }

        return new JobSpec(
            runtime,
            repoUrl!,
            gitRef!,
            entrypoint!,
            depsManifest!,
            CoerceParams(field("params_json")),
            outputPrefix,
            CoerceScope(field("declared_scope")),
            CoerceMaxBytes(field("output_max_bytes")),
            baseUrl!,
            jobToken!);
    }

    private static IReadOnlyDictionary<string, string?> ReadProcessEnv()
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            map[(string)kv.Key] = kv.Value as string;
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string?> LoadSpecFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new JobSpecException($"CUSTOMCODE_JOB_SPEC points at a missing file: {path}");
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new JobSpecException($"Failed to read/parse job spec {path}: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JobSpecException("Job spec file must contain a JSON object.");
        }

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            // params_json may be an embedded object; keep it as raw JSON text so the
            // common coercion path handles it like an inline JSON string.
            map[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText(),
            };
        }

        return map;
    }

    private static JsonElement CoerceParams(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return ParseEmptyObject();
        }

        var text = raw;
        if (text.StartsWith('@'))
        {
            var paramsPath = text[1..];
            if (!File.Exists(paramsPath))
            {
                throw new JobSpecException($"params_json @file is missing: {paramsPath}");
            }

            text = File.ReadAllText(paramsPath);
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JobSpecException("params_json must decode to a JSON object.");
            }

            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new JobSpecException($"params_json is not valid JSON: {ex.Message}");
        }
    }

    private static JsonElement ParseEmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<string> CoerceScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static long CoerceMaxBytes(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return DefaultOutputMaxBytes;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new JobSpecException($"output_max_bytes must be an integer, got '{raw}'.");
        }

        if (value <= 0)
        {
            throw new JobSpecException("output_max_bytes must be positive.");
        }

        return value;
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (path.Contains('\0') || path.StartsWith('/') || path.StartsWith('\\'))
        {
            return false;
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return false;
        }

        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWithAny(string value, params string[] prefixes)
        => prefixes.Any(p => value.StartsWith(p, StringComparison.Ordinal));

    private static string? TryGet(IReadOnlyDictionary<string, string?> env, string key)
        => env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static void Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JobSpecException($"Required job input is missing: {label}.");
        }
    }
}
