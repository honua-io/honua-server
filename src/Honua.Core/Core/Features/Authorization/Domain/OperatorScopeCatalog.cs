using System.Linq;
using System.Security.Claims;

namespace Honua.Core.Features.Authorization.Domain;

public static class OperatorScopeCatalog
{
    public const string Full = "honua.mcp.full";

    public const string Discover = "honua.mcp.discover";

    public const string Read = "honua.mcp.read";

    public const string Create = "honua.mcp.create";

    public const string Update = "honua.mcp.update";

    public const string Delete = "honua.mcp.delete";

    public const string Execute = "honua.mcp.execute";

    public const string ExecuteMutating = "honua.mcp.execute.mutating";

    public const string ExecuteCustomCode = "honua.mcp.execute.customcode";

    public const string Promote = "honua.mcp.promote";

    public const string Publish = "honua.mcp.publish";

    public const string Rollback = "honua.mcp.rollback";

    private static readonly Dictionary<string, OperatorOperation[]> ScopeOperations =
        new(StringComparer.Ordinal)
        {
            [Full] = Enum.GetValues<OperatorOperation>(),
            [Discover] = [OperatorOperation.Discover],
            [Read] = [OperatorOperation.Read, OperatorOperation.Discover],
            [Create] = [OperatorOperation.Create],
            [Update] = [OperatorOperation.Update],
            [Delete] = [OperatorOperation.Delete],
            [Execute] = [OperatorOperation.Execute],
            [ExecuteMutating] = [OperatorOperation.ExecuteMutatingProcess, OperatorOperation.Execute],
            [ExecuteCustomCode] = [OperatorOperation.ExecuteCustomCode, OperatorOperation.Execute],
            [Promote] = [OperatorOperation.Promote],
            [Publish] = [OperatorOperation.Publish],
            [Rollback] = [OperatorOperation.Rollback],
        };

    public const string ScopeGovernedClaimType = "honua:oauth_scope_governed";

    public const string ScopeGovernedClaimValue = "true";

    public const string ScopeClaimType = "scope";

    public const string ScpClaimType = "scp";

    public const string ScopeClaimUri = "http://schemas.microsoft.com/identity/claims/scope";

    public static IReadOnlyList<string> SupportedScopes { get; } =
    [
        Full,
        Discover,
        Read,
        Create,
        Update,
        Delete,
        Execute,
        ExecuteMutating,
        ExecuteCustomCode,
        Promote,
        Publish,
        Rollback,
    ];

    public static bool IsScopeGoverned(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.Claims.Any(claim =>
            claim.Type == ScopeGovernedClaimType
            || claim.Type == ScopeClaimType
            || claim.Type == ScpClaimType
            || claim.Type == ScopeClaimUri);
    }

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

    public static bool PermitsOperation(IReadOnlySet<string> recognizedScopes, OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(recognizedScopes);

        return recognizedScopes.Any(scope =>
            ScopeOperations.TryGetValue(scope, out var operations)
            && Array.IndexOf(operations, operation) >= 0);
    }
}
