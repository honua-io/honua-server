// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Geoprocessing.CustomCode;

/// <summary>
/// Pure submit-time validation for a <c>custom-code</c> geoprocessing job. Validates
/// the <c>customcode.*</c> parameters the caller supplied — runtime, repository URL
/// (HTTPS + allowlist policy), the git ref (full 40-hex SHA only, never a branch or
/// tag), the entrypoint shape, and the declared resource scope (parsed from JSON).
/// </summary>
/// <remarks>
/// This type carries no I/O. The <em>scope ⊆ owner</em> check is performed by the
/// caller through <see cref="ScopedJobAttenuation.IsWithinOwner"/> against the live
/// submitting principal; this validator only parses and structurally validates the
/// declared scope so the same JSON contract is enforced in one place.
/// </remarks>
internal static partial class CustomCodeSubmitValidator
{
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullShaRegex();

    // PYTHON entrypoint: module.path:function — dotted module path, a single ':'
    // separator, then a python identifier. Deliberately strict so a
    // shell-injection-shaped entrypoint is rejected at the door.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*:[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PythonEntrypointRegex();

    // .NET entrypoint: Assembly::Namespace.Type — an assembly simple name, a '::'
    // separator, then a dotted CLR type name. The assembly and each type segment are
    // CLR-style identifiers (letters/digits/underscore, may start with '_'); the
    // strictness keeps a shell-injection-shaped entrypoint out just like the python
    // form. The harness resolves "MyAsm::My.Namespace.MyTool" to a built assembly +
    // type implementing IGeoprocessingTool.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*::[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetEntrypointRegex();

    /// <summary>
    /// Returns <see langword="true"/> when the submission carries the custom-code
    /// runtime marker (so the submit path branches into the custom-code gate). A
    /// submission without it is an ordinary geoprocessing job and is unaffected.
    /// </summary>
    public static bool IsCustomCodeSubmission(IReadOnlyDictionary<string, string>? parameters)
        => parameters is not null
           && parameters.TryGetValue(CustomCodeJobContract.RuntimeParam, out var runtime)
           && !string.IsNullOrWhiteSpace(runtime);

    /// <summary>
    /// Validates the caller-supplied custom-code parameters and parses the declared
    /// scope. Returns the parsed declared scope on success; on failure sets
    /// <paramref name="rejection"/> and returns <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// This is the <em>pure</em>, I/O-free half of the submit gate: runtime, SHA-pin,
    /// repo URL + org/per-tenant allowlist, entrypoint, manifest, and declared scope.
    /// The <see cref="CustomCodeRepoPolicy.SignedOnly"/> posture additionally requires
    /// an out-of-band commit-signature check; that check is asynchronous (it may call
    /// the git provider) and is performed by the caller against
    /// <see cref="ICustomCodeCommitSignatureVerifier"/>. <paramref name="requiresSignatureVerification"/>
    /// reports whether the caller must run it; the validated URL and SHA are returned
    /// so the caller need not re-parse them.
    /// </remarks>
    /// <param name="parameters">The submission parameters (the <c>customcode.*</c> keys).</param>
    /// <param name="options">The configured repository-allowlist/signing policy.</param>
    /// <param name="tenantId">The submitting principal's tenant id, for the per-tenant allowlist (may be null).</param>
    /// <param name="declaredScope">The parsed declared scope when validation succeeds.</param>
    /// <param name="repoUri">The validated repository URI when validation succeeds.</param>
    /// <param name="commitSha">The validated full commit SHA when validation succeeds.</param>
    /// <param name="requiresSignatureVerification"><see langword="true"/> when the caller must run the commit-signature verifier (signed-only).</param>
    /// <param name="rejection">A human-readable rejection reason when validation fails.</param>
    /// <returns><see langword="true"/> when every custom-code parameter is valid.</returns>
    public static bool TryValidate(
        IReadOnlyDictionary<string, string> parameters,
        CustomCodeOptions options,
        string? tenantId,
        out IReadOnlyList<JobResourceScopeEntry> declaredScope,
        out Uri? repoUri,
        out string? commitSha,
        out bool requiresSignatureVerification,
        out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(options);
        declaredScope = [];
        repoUri = null;
        commitSha = null;
        requiresSignatureVerification = false;
        rejection = null;

        // runtime — 'python' (Phase 1) or 'dotnet' (Phase 2). The runtime selects the
        // per-job image; the controls below and the routing fence are runtime-agnostic.
        var runtime = Get(parameters, CustomCodeJobContract.RuntimeParam);
        if (runtime is null || !CustomCodeJobContract.SupportedRuntimes.Contains(runtime))
        {
            rejection =
                $"Custom-code runtime must be '{CustomCodeJobContract.PythonRuntime}' or " +
                $"'{CustomCodeJobContract.DotnetRuntime}' (got '{runtime ?? "<none>"}').";
            return false;
        }

        // git_ref — full 40-hex commit SHA only. A branch or tag is rejected so the
        // executed code is pinned to an immutable commit and cannot be moved under us.
        var gitRef = Get(parameters, CustomCodeJobContract.GitRefParam);
        if (string.IsNullOrEmpty(gitRef) || !FullShaRegex().IsMatch(gitRef))
        {
            rejection = "Custom-code git_ref must be a full 40-character commit SHA (branches and tags are not allowed).";
            return false;
        }

        commitSha = gitRef;

        // repo_url — HTTPS + org/per-tenant allowlist + signing policy.
        var repoUrl = Get(parameters, CustomCodeJobContract.RepoUrlParam);
        if (!TryValidateRepoUrl(repoUrl, options, tenantId, out repoUri, out requiresSignatureVerification, out rejection))
        {
            return false;
        }

        // entrypoint — shape depends on the runtime: python wants 'module.path:function';
        // .NET wants 'Assembly::Namespace.Type' (assembly::CLR-type).
        var entrypoint = Get(parameters, CustomCodeJobContract.EntrypointParam);
        if (!TryValidateEntrypoint(entrypoint, runtime, out rejection))
        {
            return false;
        }

        // deps_manifest — a relative path (no absolute paths, no traversal). For
        // python this is a requirements file; for .NET it is the user's .csproj (both
        // are repo-relative and resolved by the runtime's harness).
        var depsManifest = Get(parameters, CustomCodeJobContract.DepsManifestParam);
        if (string.IsNullOrEmpty(depsManifest) || !IsSafeRelativePath(depsManifest))
        {
            rejection = string.Equals(runtime, CustomCodeJobContract.DotnetRuntime, StringComparison.Ordinal)
                ? "Custom-code deps_manifest must be a relative path within the repository (e.g. 'tool/MyTool.csproj')."
                : "Custom-code deps_manifest must be a relative path within the repository (e.g. 'requirements.txt').";
            return false;
        }

        // declared_scope — JSON [{serviceId, layerId?, access}].
        var rawScope = Get(parameters, CustomCodeJobContract.DeclaredScopeParam);
        if (!TryParseDeclaredScope(rawScope, out declaredScope, out rejection))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateEntrypoint(string? entrypoint, string runtime, out string? rejection)
    {
        rejection = null;

        if (string.Equals(runtime, CustomCodeJobContract.DotnetRuntime, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(entrypoint) || !DotnetEntrypointRegex().IsMatch(entrypoint))
            {
                rejection = "Custom-code entrypoint for the 'dotnet' runtime must be of the form 'Assembly::Namespace.Type'.";
                return false;
            }

            return true;
        }

        // Default to the python form.
        if (string.IsNullOrEmpty(entrypoint) || !PythonEntrypointRegex().IsMatch(entrypoint))
        {
            rejection = "Custom-code entrypoint for the 'python' runtime must be of the form 'module.path:function'.";
            return false;
        }

        return true;
    }

    private static bool TryValidateRepoUrl(
        string? repoUrl,
        CustomCodeOptions options,
        string? tenantId,
        out Uri? repoUri,
        out bool requiresSignatureVerification,
        out string? rejection)
    {
        rejection = null;
        repoUri = null;
        requiresSignatureVerification = false;

        if (string.IsNullOrWhiteSpace(repoUrl) ||
            !Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            rejection = "Custom-code repo_url must be an absolute https URL.";
            return false;
        }

        repoUri = uri;

        switch (options.RepoPolicy)
        {
            case CustomCodeRepoPolicy.Disabled:
                rejection = "Custom-code submission is disabled (no repository allowlist is configured).";
                return false;

            case CustomCodeRepoPolicy.Open:
                // Open accepts any host and does not consult the per-tenant allowlist;
                // it is the trusted-single-tenant escape hatch.
                return true;

            case CustomCodeRepoPolicy.OrgAllowlist:
                return TryEnforceAllowlists(uri, options, tenantId, out rejection);

            case CustomCodeRepoPolicy.SignedOnly:
                // Signed-only is org-allowlist PLUS a commit-signature requirement. The
                // allowlist gate runs here (pure); the caller runs the async signature
                // check against the verifier when this flag is set.
                if (!TryEnforceAllowlists(uri, options, tenantId, out rejection))
                {
                    return false;
                }

                requiresSignatureVerification = true;
                return true;

            default:
                rejection = "Custom-code repository policy is misconfigured.";
                return false;
        }
    }

    /// <summary>
    /// Enforces the org-wide allowlist and, when the tenant has a per-tenant list, that
    /// list <em>in addition</em> (both must pass). A tenant absent from the per-tenant
    /// map is constrained by the org list alone.
    /// </summary>
    private static bool TryEnforceAllowlists(Uri uri, CustomCodeOptions options, string? tenantId, out string? rejection)
    {
        rejection = null;

        if (!IsAllowlisted(uri, options.RepoAllowlist))
        {
            rejection = $"Custom-code repo_url host '{uri.Host}' is not on the configured repository allowlist.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tenantId) &&
            options.TenantRepoAllowlist is { Count: > 0 } &&
            options.TenantRepoAllowlist.TryGetValue(tenantId, out var tenantList) &&
            tenantList is { Count: > 0 } &&
            !IsAllowlisted(uri, tenantList))
        {
            rejection = $"Custom-code repo_url host '{uri.Host}' is not on tenant '{tenantId}''s repository allowlist.";
            return false;
        }

        return true;
    }

    private static bool IsAllowlisted(Uri uri, List<string> allowlist)
    {
        if (allowlist is null || allowlist.Count == 0)
        {
            return false;
        }

        // The first path segment (the org/owner) is used when an allowlist entry is
        // of the form "host/org"; otherwise only the host is matched.
        var firstSegment = uri.AbsolutePath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
            ? parts[0]
            : string.Empty;

        foreach (var rawEntry in allowlist)
        {
            if (string.IsNullOrWhiteSpace(rawEntry))
            {
                continue;
            }

            var entry = rawEntry.Trim();
            var sep = entry.IndexOf('/');
            if (sep < 0)
            {
                if (string.Equals(entry, uri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            var host = entry[..sep].Trim();
            var org = entry[(sep + 1)..].Trim();
            if (string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(org, firstSegment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (path.Contains('\0') || path.StartsWith('/') || path.StartsWith('\\'))
        {
            return false;
        }

        // Reject Windows drive-absolute (e.g. C:\) and any parent-traversal segment.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return false;
        }

        var segments = path.Split('/', '\\');
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseDeclaredScope(
        string? raw,
        out IReadOnlyList<JobResourceScopeEntry> declaredScope,
        out string? rejection)
    {
        declaredScope = [];
        rejection = null;

        // A declared scope is optional: a custom-code job that needs no callback
        // access declares none and gets a read-nothing token.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        List<DeclaredScopeDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize(raw, CustomCodeScopeJsonContext.Default.ListDeclaredScopeDto);
        }
        catch (JsonException)
        {
            rejection = "Custom-code declared_scope must be a JSON array of {serviceId, layerId?, access} entries.";
            return false;
        }

        if (dtos is null)
        {
            return true;
        }

        var entries = new List<JobResourceScopeEntry>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.ServiceId))
            {
                rejection = "Each custom-code declared_scope entry requires a non-empty serviceId.";
                return false;
            }

            var access = string.Equals(dto.Access, "write", StringComparison.OrdinalIgnoreCase)
                ? JobResourceAccess.Write
                : JobResourceAccess.Read;
            var layer = string.IsNullOrWhiteSpace(dto.LayerId) ? null : dto.LayerId!.Trim();

            entries.Add(new JobResourceScopeEntry(dto.ServiceId!.Trim(), layer, access));
        }

        declaredScope = entries;
        return true;
    }

    private static string? Get(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}
