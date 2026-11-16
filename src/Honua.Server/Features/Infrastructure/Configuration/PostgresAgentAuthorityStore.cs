// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed class PostgresAgentAuthorityStore(PostgresConfigurationDocuments documents) : IAgentAuthorityStore
{
    private readonly ConfigurationCollectionStore<AgentAuthorityCeiling> _store = new(documents,
        new("agent-authority-ceiling", SharedConfigurationJsonContext.Default.AgentAuthorityCeiling));

    public async Task<AgentAuthorityCeiling?> GetAsync(string? tenantId, string principalId, CancellationToken cancellationToken = default)
    {
        var key = Key(tenantId, principalId);
        // Actuation reads the generation and document together, including changes since authentication.
        var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Documents.FirstOrDefault(document => document.Key == key)?.Value;
    }

    public async Task SetAsync(AgentAuthorityCeiling ceiling, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ceiling);
        if (!ceiling.IsValid())
            throw new ArgumentException("Invalid agent authority ceiling.", nameof(ceiling));
        await _store.MutateAsync(Key(ceiling.TenantId, ceiling.PrincipalId), _ => ceiling, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string? tenantId, string principalId, CancellationToken cancellationToken = default)
        => _store.DeleteAsync(Key(tenantId, principalId), cancellationToken);

    private static string Key(string? tenantId, string principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var identity = JsonSerializer.Serialize(new[] { tenantId, principalId }, SharedConfigurationJsonContext.Default.StringArray);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
