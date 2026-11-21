// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

// The endpoint-filter form of the credential-management policy, for routes where agents are sent
// to human approval: registered after the agent approval filter, an agent still gets the
// approval-required answer that says how to ask, an approved replay passes on its exact
// approved-operation grant, and any other caller without the authority gets 403.
public static class CredentialManagementAuthorityFilter
{
    internal const string Refusal = "This change requires credential-management authority.";

    public static TBuilder RequireCredentialManagementAuthority<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (invocation, next) =>
        {
            var request = invocation.HttpContext.Request;
            return AdminApiKeyPermission.HasCredentialManagementAuthority(invocation.HttpContext.User, request.Method, request.Path.Value)
                ? await next(invocation).ConfigureAwait(false)
                : ProblemDetailsHelpers.CreateAdminProblem(invocation.HttpContext, StatusCodes.Status403Forbidden, Refusal);
        });
}
