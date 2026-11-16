// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Honua.Infrastructure.Authentication;

// A route changes server state on any method other than GET, HEAD, OPTIONS and TRACE. Admin
// and Console routes, and every route that requires administrator authority, are governed: an
// agent makes such a change through the operations API or MCP, which run it or file a proposal
// for one human approval as the operation policy decides, and a direct call is refused. A route
// that applies its own agent rule carries RouteAgentRuleMetadata, with the reason, and is listed
// in the committed agent route rule list.
internal static partial class AgentChangeRoutes
{
    private static readonly HashSet<string> _administratorPolicies = new(StringComparer.Ordinal)
    {
        AuthenticationExtensions.AdminPolicy,
        AuthenticationExtensions.AdminPolicyAlias,
        AuthenticationExtensions.AdminApprovePolicy,
        AuthenticationExtensions.AdminCredentialManagementPolicy,
        AuthenticationExtensions.OpsReadPolicy,
        AuthenticationExtensions.TemporalHistoryReadPolicy,
        AuthenticationExtensions.TemporalDiffReadPolicy,
        AuthenticationExtensions.TemporalRollbackExecutePolicy,
        AuthenticationExtensions.ConformanceMutatePolicy,
    };

    public static bool IsChange(string method)
        => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
            && !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method);

    public static bool IsGoverned(RouteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return (endpoint.RoutePattern.RawText is { } route && GovernedRoute().IsMatch(route))
            || RequiresAdministratorAuthority(endpoint);
    }

    public static bool HasRouteAgentRule(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Metadata.GetMetadata<RouteAgentRuleMetadata>() is not null;
    }

    // Marks a governed route that applies its own agent rule, or changes nothing.
    public static TBuilder WithRouteAgentRule<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return builder.WithMetadata(new RouteAgentRuleMetadata(reason));
    }

    // Marks the endpoints of a service whose route template ends with the suffix, for one gRPC
    // method of a mapped service.
    public static TBuilder WithRouteAgentRule<TBuilder>(this TBuilder builder, string routeSuffix, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeSuffix);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        builder.Add(endpoint =>
        {
            if (endpoint is RouteEndpointBuilder { RoutePattern.RawText: { } route }
                && route.EndsWith(routeSuffix, StringComparison.Ordinal))
            {
                endpoint.Metadata.Add(new RouteAgentRuleMetadata(reason));
            }
        });
        return builder;
    }

    private static bool RequiresAdministratorAuthority(Endpoint endpoint)
        => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(static data => data.Policy is { } policy && _administratorPolicies.Contains(policy));

    [GeneratedRegex(@"^/?api/v[^/]+/(admin|console)(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GovernedRoute();
}

internal sealed class RouteAgentRuleMetadata(string reason)
{
    public string Reason { get; } = reason;
}
