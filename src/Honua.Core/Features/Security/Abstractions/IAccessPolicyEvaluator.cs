// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Security.Domain;
using DomainAccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Interface for evaluating access policies.
/// </summary>
public interface IAccessPolicyEvaluator
{
    /// <summary>
    /// Evaluates access for a given principal and resource.
    /// </summary>
    /// <param name="principal">The claims principal</param>
    /// <param name="resource">The resource being accessed</param>
    /// <param name="action">The action being performed</param>
    /// <returns>The access decision</returns>
    Task<DomainAccessDecision> EvaluateAsync(ClaimsPrincipal principal, string resource, string action);

    /// <summary>
    /// Evaluates access synchronously for a given principal and resource.
    /// </summary>
    /// <param name="principal">The claims principal</param>
    /// <param name="resource">The resource being accessed</param>
    /// <param name="action">The action being performed</param>
    /// <returns>The access decision</returns>
    DomainAccessDecision Evaluate(ClaimsPrincipal principal, string resource, string action);

    /// <summary>
    /// Evaluates access for a layer and service policy combination.
    /// </summary>
    /// <param name="principal">The claims principal.</param>
    /// <param name="layerPolicy">The optional layer policy.</param>
    /// <param name="servicePolicy">The optional service policy.</param>
    /// <param name="scope">The requested scope object.</param>
    /// <returns>The access decision.</returns>
    Task<DomainAccessDecision> EvaluateAsync(
        ClaimsPrincipal principal,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        object? scope = null);

    /// <summary>
    /// Evaluates access synchronously for a layer and service policy combination.
    /// </summary>
    /// <param name="principal">The claims principal.</param>
    /// <param name="layerPolicy">The optional layer policy.</param>
    /// <param name="servicePolicy">The optional service policy.</param>
    /// <param name="scope">The requested scope object.</param>
    /// <returns>The access decision.</returns>
    DomainAccessDecision Evaluate(
        ClaimsPrincipal principal,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        object? scope = null);
}
