// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>Captures and resolves secret-bearing approval parameters without serializing values.</summary>
internal static class OperationSecretParameters
{
    private static readonly TimeSpan ApprovalSecretTtl = TimeSpan.FromDays(30);

    internal static (Dictionary<string, string?> Parameters,
        Dictionary<string, OperationSecretReference> SecretParameters) Capture(
        OperationRequest request,
        OperationPolicyContext context,
        IOperationSecretStore? store)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        var references = new Dictionary<string, OperationSecretReference>(StringComparer.Ordinal);
        foreach (var pair in request.Parameters)
        {
            if (!IsSecretInput(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                parameters[pair.Key] = pair.Value;
                continue;
            }

            if (store is null)
            {
                throw new InvalidOperationException("Secret-bearing approval requires the operation secret channel.");
            }

            var operationInstanceId = context.OperationInstanceId
                ?? throw new InvalidOperationException("Secret-bearing approval requires a canonical operation identity.");
            references[pair.Key] = store.Store(
                operationInstanceId,
                request.OperationId,
                context.PrincipalId,
                context.TenantId,
                pair.Key,
                pair.Value,
                ApprovalSecretTtl);
        }

        return (parameters, references);
    }

    internal static OperationRequest Resolve(
        OperationRequest request,
        OperationPolicyContext context,
        IOperationSecretStore? store)
    {
        if (request.SecretParameters.Count == 0)
        {
            return request;
        }

        if (store is null)
        {
            throw new InvalidOperationException("Approved operation secret channel is unavailable.");
        }

        var parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal);
        foreach (var pair in request.SecretParameters)
        {
            var value = store.Consume(
                pair.Value,
                context.OperationInstanceId
                    ?? throw new InvalidOperationException("Approved replay is missing its operation identity."),
                request.OperationId,
                context.PrincipalId,
                context.TenantId);
            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Approved operation secret '{pair.Key}' is unavailable or was already consumed.");
            }

            parameters[pair.Key] = value;
        }

        return request with { Parameters = parameters, SecretParameters = new Dictionary<string, OperationSecretReference>() };
    }

    internal static bool IsSecretInput(string name)
        => string.Equals(name, "clientSecret", StringComparison.OrdinalIgnoreCase);
}
