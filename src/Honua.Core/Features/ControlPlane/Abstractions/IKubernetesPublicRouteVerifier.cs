// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>Observes the registered public DNS record and its actual cloud load balancer.</summary>
public interface IKubernetesPublicRouteVerifier
{
    /// <summary>
    /// Verifies that all serving DNS records for the registered public name select the
    /// exact load balancer freshly observed in the bound Kubernetes Service or Ingress.
    /// Inputs must come from the revalidated registered target, never request overrides.
    /// </summary>
    Task<bool> VerifyAsync(IReadOnlyDictionary<string, string> parameters, string observedHostname,
        CancellationToken cancellationToken = default);
}
