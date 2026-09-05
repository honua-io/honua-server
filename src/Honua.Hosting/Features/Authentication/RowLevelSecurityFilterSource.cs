// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Request-scoped <see cref="IRowLevelSecurityFilterSource"/> (#502). Resolves the
/// current principal's roles/claims and the layer's <see cref="RlsPolicy"/> set into a
/// single parameterized SQL predicate. The predicate is built as a filter-expression
/// AST (<c>attribute IN (claim values)</c>) and translated through the shared
/// <see cref="IFilterExpressionService"/>, so the layer attribute is validated against
/// the resource schema and every claim value is bound as a query parameter — the
/// predicate can never inject SQL regardless of claim contents.
/// </summary>
/// <remarks>
/// Returning <see langword="null"/> means "no RLS policy applies" (query is
/// unconstrained by RLS). When a matching policy exists but the principal carries no
/// value for the referenced claim, the translated predicate is <c>FALSE</c> — RLS is
/// fail-secure, so a missing claim hides every row rather than revealing them.
/// </remarks>
internal sealed partial class RowLevelSecurityFilterSource : IRowLevelSecurityFilterSource
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IRlsPolicyStore _policyStore;
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IFilterExpressionService _filterExpressionService;
    private readonly RbacOptions _rbacOptions;
    private readonly ILogger<RowLevelSecurityFilterSource> _logger;

    public RowLevelSecurityFilterSource(
        IHttpContextAccessor httpContextAccessor,
        IRlsPolicyStore policyStore,
        IMetadataV2GraphProvider graphProvider,
        IFilterExpressionService filterExpressionService,
        IOptions<RbacOptions> rbacOptions,
        ILogger<RowLevelSecurityFilterSource> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _policyStore = policyStore;
        _graphProvider = graphProvider;
        _filterExpressionService = filterExpressionService;
        _rbacOptions = rbacOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SqlFragment?> ResolveAsync(MetadataV2Resource resource, CancellationToken cancellationToken = default)
    {
        var predicates = await ResolvePredicatesAsync(resource, cancellationToken).ConfigureAwait(false);
        SqlFragment? combined = null;
        foreach (var (_, predicate) in predicates)
        {
            combined = combined is null ? predicate : AndFragments(combined, predicate);
        }

        return combined;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FilterExpression>> ResolveExpressionsAsync(
        MetadataV2Resource resource, CancellationToken cancellationToken = default)
        => (await ResolvePredicatesAsync(resource, cancellationToken).ConfigureAwait(false))
            .Select(static predicate => predicate.Expression).ToArray();

    private async Task<IReadOnlyList<(FilterExpression Expression, SqlFragment Sql)>> ResolvePredicatesAsync(
        MetadataV2Resource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var principal = ResolvePrincipal();
        if (principal is null)
        {
            // No request context and no job-security scope (e.g. tile seeding, an import
            // worker, a scheduler) — there is no caller to constrain, so RLS has nothing to
            // enforce here. The metadata permanent filter still applies independently.
            // Geoprocessing reads never land here: JobSecurityScope is active for the whole
            // job dispatch, so they either resolve the submitter or fail closed below.
            return [];
        }

        var roles = RbacRoleClaims.Enumerate(
            principal,
            _rbacOptions,
            _httpContextAccessor.HttpContext?.RequestServices);

        var layerName = resource.Metadata.Name;
        if (string.IsNullOrWhiteSpace(layerName))
        {
            return [];
        }

        // A resource can be published through several services; collect the candidate
        // service names so a service-scoped policy matches regardless of which
        // protocol surface served the request. "*" service policies match all.
        var serviceNames = await ResolveServiceNamesAsync(resource, cancellationToken).ConfigureAwait(false);

        var policies = await CollectPoliciesAsync(roles, serviceNames, layerName, cancellationToken).ConfigureAwait(false);
        if (policies.Count == 0)
        {
            return [];
        }

        return policies.Select(policy => BuildPredicate(policy, principal, resource)).ToArray();
    }

    private async Task<IReadOnlyList<RlsPolicy>> CollectPoliciesAsync(
        IReadOnlyList<string> roles,
        IReadOnlyCollection<string> serviceNames,
        string layerName,
        CancellationToken cancellationToken)
    {
        // De-duplicate across services by policy id; the same "*"-service policy
        // would otherwise be returned once per candidate service.
        var byId = new Dictionary<Guid, RlsPolicy>();

        // Always evaluate with the wildcard service so "*"-service policies apply
        // even when the resource has no resolvable publication (e.g. direct layer-id
        // access), plus each concrete service name the resource is published under.
        var lookupServices = new HashSet<string>(serviceNames, StringComparer.OrdinalIgnoreCase) { "*" };

        foreach (var service in lookupServices)
        {
            var policies = await _policyStore
                .GetEffectivePoliciesAsync(roles, service, layerName, cancellationToken)
                .ConfigureAwait(false);
            foreach (var policy in policies)
            {
                byId[policy.PolicyId] = policy;
            }
        }

        return byId.Values.ToList();
    }

    private async Task<IReadOnlyCollection<string>> ResolveServiceNamesAsync(
        MetadataV2Resource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var index = snapshot.Index;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var publication in index.PublicationsById.Values)
            {
                if (!string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (index.ServicesById.TryGetValue(publication.ServiceId, out var service) &&
                    !string.IsNullOrWhiteSpace(service.Metadata.Name))
                {
                    names.Add(service.Metadata.Name);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional: metadata resolution is best-effort for service scoping;
            // "*"-service policies still apply via the wildcard lookup. Never fail the query.
            RlsLog.ServiceResolutionFailed(_logger, resource.Metadata.Name, ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Builds the parameterized predicate for a single policy by comparing the
    /// policy attribute against the principal's claim value(s). Returns both the
    /// normalized event predicate and SQL translation; translation failures throw.
    /// An empty claim set yields a FALSE predicate (fail-secure, no rows).
    /// </summary>
    private (FilterExpression Expression, SqlFragment Sql) BuildPredicate(RlsPolicy policy, ClaimsPrincipal principal, MetadataV2Resource resource)
    {
        var values = principal
            .FindAll(policy.ClaimType)
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var attribute = resource.SchemaFields.FirstOrDefault(field =>
            string.Equals(field.Name, policy.Attribute, StringComparison.OrdinalIgnoreCase))?.Name ?? policy.Attribute;
        var expression = BuildExpression(policy, values, attribute);

        var normalized = _filterExpressionService.Normalize(expression, resource);
        var translation = _filterExpressionService.Translate(normalized, resource);
        if (!translation.IsSuccess || translation.SqlFilter is null)
        {
            // A configured policy that cannot translate (e.g. attribute not in the
            // schema) must never be silently dropped — that would leak hidden rows.
            throw new InvalidOperationException(
                $"RLS policy '{policy.PolicyId}' for layer '{resource.Metadata.Name}' could not be translated: {translation.ErrorMessage ?? "unknown error"}.");
        }

        return (normalized, translation.SqlFilter);
    }

    private static BinaryExpression BuildExpression(RlsPolicy policy, List<string> values, string attribute)
    {
        var property = new PropertyReference(attribute);

        if (values.Count == 0)
        {
            // No claim value -> deny all rows. An empty IN list translates to FALSE
            // in every provider, which is the fail-secure outcome we want.
            return new BinaryExpression(property, BinaryOperator.In, new ValueList(Array.Empty<FilterExpression>()));
        }

        if (policy.Comparison == RlsComparison.Equals)
        {
            return new BinaryExpression(property, BinaryOperator.Equal, ToLiteral(values[0]));
        }

        var literals = values.Select(ToLiteral).Cast<FilterExpression>().ToList();
        return new BinaryExpression(property, BinaryOperator.In, new ValueList(literals));
    }

    private static Literal ToLiteral(string value) => new(value, LiteralType.Text);

    /// <summary>
    /// AND-combines two already-parameterized fragments, renumbering the right-hand
    /// fragment's <c>@pN</c> placeholders so the merged parameter list stays
    /// positionally consistent for the provider that re-numbers to <c>$N</c>.
    /// </summary>
    private static SqlFragment AndFragments(SqlFragment left, SqlFragment right)
    {
        var offset = left.Parameters.Count;
        var rightSql = ShiftParameterPlaceholders(right.Sql, offset);
        var parameters = new List<object?>(left.Parameters);
        parameters.AddRange(right.Parameters);
        return new SqlFragment($"({left.Sql}) AND ({rightSql})", parameters);
    }

    private static string ShiftParameterPlaceholders(string sql, int offset)
    {
        if (offset == 0)
        {
            return sql;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"@p(\d+)",
            match =>
            {
                var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return $"@p{index + offset}";
            },
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static partial class RlsLog
    {
        [LoggerMessage(EventId = 4560, Level = LogLevel.Warning,
            Message = "RLS service-name resolution failed for layer '{Layer}'; only wildcard-service policies will apply")]
        public static partial void ServiceResolutionFailed(ILogger logger, string layer, Exception exception);
    }

    /// <summary>
    /// Resolves the principal RLS is evaluated against: the live request user when one exists,
    /// otherwise the submitter snapshot pinned on the executing background job
    /// (honua-server#3068). Returns <see langword="null"/> only when there is neither — a
    /// server-internal read with no caller to constrain.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when a job-execution scope is active but carries no submitter snapshot. The read
    /// cannot be constrained to the submitting caller, so it is refused rather than silently
    /// returning every row.
    /// </exception>
    private ClaimsPrincipal? ResolvePrincipal()
    {
        var requestPrincipal = _httpContextAccessor.HttpContext?.User;
        if (requestPrincipal is not null)
        {
            return requestPrincipal;
        }

        var scope = JobSecurityScope.Current;
        if (scope is null)
        {
            return null;
        }

        if (scope.Submitter is null)
        {
            throw new UnauthorizedAccessException(
                "This job carries no submitter security context, so row-level security cannot be enforced on its reads.");
        }

        return JobSecurityContextCapture.Restore(scope.Submitter);
    }
}
