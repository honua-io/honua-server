using System.Linq;
using System.Security.Claims;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Canonical OAuth 2.1 scope taxonomy for the MCP resource server (honua-server#2851).
/// Maps each named <c>honua.mcp.*</c> scope to the set of <see cref="OperatorOperation"/>
/// values it authorizes, resource-agnostically, so a bearer token's scopes can be
/// intersected with the caller's operator grants.
/// </summary>
/// <remarks>
/// <para>
/// This is the resource-server side of scope handling: an external identity provider mints
/// the token and stamps its <c>scope</c>/<c>scp</c> claim; Honua only reads those scopes and
/// narrows what the already-authorized principal may do. Scopes are defined at
/// operation-family granularity across every <see cref="OperatorResourceType"/>, which is the
/// granularity issue #2851 targets (per-tool scopes are an explicit non-goal). A scope can only
/// ever <em>narrow</em> the grant model — it is intersected with the grant decision and never
/// widens it.
/// </para>
/// <para>
/// The higher-tier execute scopes intentionally imply baseline
/// <see cref="OperatorOperation.Execute"/>, and <see cref="OperatorOperation.Read"/> implies
/// <see cref="OperatorOperation.Discover"/>, so a token granted a stronger scope is not forced
/// to also enumerate the weaker one it obviously subsumes.
/// </para>
/// </remarks>
public static class OperatorScopeCatalog
{
    /// <summary>
    /// Unrestricted MCP scope. A token carrying it is bounded only by the principal's operator
    /// grants — it never narrows them. Lets an operator mint a full-authority agent token when
    /// least-privilege delegation is not wanted, and restores the pre-scope "token authority ==
    /// grant authority" behaviour explicitly rather than by accident.
    /// </summary>
    public const string Full = "honua.mcp.full";

    /// <summary>Scope authorizing catalog/capability discovery (<see cref="OperatorOperation.Discover"/>).</summary>
    public const string Discover = "honua.mcp.discover";

    /// <summary>Scope authorizing read access to resource state and results (implies <see cref="Discover"/>).</summary>
    public const string Read = "honua.mcp.read";

    /// <summary>Scope authorizing creation of new resources or artifacts (<see cref="OperatorOperation.Create"/>).</summary>
    public const string Create = "honua.mcp.create";

    /// <summary>Scope authorizing execution of built-in analytic tools/jobs (<see cref="OperatorOperation.Execute"/>).</summary>
    public const string Execute = "honua.mcp.execute";

    /// <summary>
    /// Scope authorizing mutating built-in geoprocessing
    /// (<see cref="OperatorOperation.ExecuteMutatingProcess"/>; implies baseline
    /// <see cref="OperatorOperation.Execute"/>).
    /// </summary>
    public const string ExecuteMutating = "honua.mcp.execute.mutating";

    /// <summary>
    /// Scope authorizing operator-supplied custom-code geoprocessing
    /// (<see cref="OperatorOperation.ExecuteCustomCode"/>; implies baseline
    /// <see cref="OperatorOperation.Execute"/>).
    /// </summary>
    public const string ExecuteCustomCode = "honua.mcp.execute.customcode";

    /// <summary>Scope authorizing promotion of workspace artifacts (<see cref="OperatorOperation.Promote"/>).</summary>
    public const string Promote = "honua.mcp.promote";

    /// <summary>Scope authorizing publishing/deployment (<see cref="OperatorOperation.Publish"/>).</summary>
    public const string Publish = "honua.mcp.publish";

    private static readonly Dictionary<string, OperatorOperation[]> ScopeOperations =
        new(StringComparer.Ordinal)
        {
            [Full] = Enum.GetValues<OperatorOperation>(),
            [Discover] = [OperatorOperation.Discover],
            [Read] = [OperatorOperation.Read, OperatorOperation.Discover],
            [Create] = [OperatorOperation.Create],
            [Execute] = [OperatorOperation.Execute],
            [ExecuteMutating] = [OperatorOperation.ExecuteMutatingProcess, OperatorOperation.Execute],
            [ExecuteCustomCode] = [OperatorOperation.ExecuteCustomCode, OperatorOperation.Execute],
            [Promote] = [OperatorOperation.Promote],
            [Publish] = [OperatorOperation.Publish],
        };

    /// <summary>
    /// Claim type marking a principal as governed by OAuth access-token scopes. Stamped on
    /// every bearer-token-authenticated principal (JwtBearer <c>OnTokenValidated</c>) so the
    /// scope authorizer can tell an OAuth caller — for which a missing scope claim is
    /// fail-closed — apart from an X-API-Key or interactive principal, which scopes never touch.
    /// </summary>
    public const string ScopeGovernedClaimType = "honua:oauth_scope_governed";

    /// <summary>Value carried by the <see cref="ScopeGovernedClaimType"/> marker claim.</summary>
    public const string ScopeGovernedClaimValue = "true";

    /// <summary>Standard OAuth 2.0 <c>scope</c> claim (RFC 9068, space-delimited).</summary>
    public const string ScopeClaimType = "scope";

    /// <summary>Azure AD <c>scp</c> scope claim (space-delimited).</summary>
    public const string ScpClaimType = "scp";

    /// <summary>SAML-style scope claim URI emitted by some identity providers.</summary>
    public const string ScopeClaimUri = "http://schemas.microsoft.com/identity/claims/scope";

    /// <summary>The full set of scope names this resource server recognizes and advertises.</summary>
    public static IReadOnlyList<string> SupportedScopes { get; } =
    [
        Full,
        Discover,
        Read,
        Create,
        Execute,
        ExecuteMutating,
        ExecuteCustomCode,
        Promote,
        Publish,
    ];

    /// <summary>
    /// Returns whether the principal is governed by OAuth scopes — i.e. it was authenticated
    /// from a bearer access token (marked at token validation) or presents a scope claim.
    /// Principals that are not scope-governed (X-API-Key, interactive sessions, dev-bypass) are
    /// never narrowed by scope checks.
    /// </summary>
    public static bool IsScopeGoverned(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.Claims.Any(claim =>
            claim.Type == ScopeGovernedClaimType
            || claim.Type == ScopeClaimType
            || claim.Type == ScpClaimType
            || claim.Type == ScopeClaimUri);
    }

    /// <summary>
    /// Collects the recognized <c>honua.mcp.*</c> scopes a principal carries, expanding the
    /// space-delimited <c>scope</c>/<c>scp</c> claim values. Unknown scope tokens are dropped —
    /// they can never grant an operation, so they cannot escalate.
    /// </summary>
    public static IReadOnlySet<string> CollectRecognizedScopes(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.Claims
            .Where(claim =>
                claim.Type == ScopeClaimType
                || claim.Type == ScpClaimType
                || claim.Type == ScopeClaimUri)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(token => ScopeOperations.ContainsKey(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns whether any of the supplied recognized scopes authorizes the given operation.
    /// </summary>
    public static bool PermitsOperation(IReadOnlySet<string> recognizedScopes, OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(recognizedScopes);

        return recognizedScopes.Any(scope =>
            ScopeOperations.TryGetValue(scope, out var operations)
            && Array.IndexOf(operations, operation) >= 0);
    }
}
