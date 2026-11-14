// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal static class ClientCertificateTrustMutations
{
    public static ClientCertificateTrustProfile Upsert(ClientCertificateTrustProfile? existing, ClientCertificateTrustProfile profile)
        => existing is null ? profile with { Revision = Math.Max(1, profile.Revision) } : profile with
        {
            Revision = existing.Revision + 1,
            PrincipalMappings = profile.PrincipalMappings.Count == 0 ? existing.PrincipalMappings : profile.PrincipalMappings,
            Revocations = profile.Revocations.Count == 0 ? existing.Revocations : profile.Revocations
        };

    public static ClientCertificateTrustProfile UpsertMapping(ClientCertificateTrustProfile profile, ClientCertificatePrincipalMapping mapping)
    {
        var mappings = profile.PrincipalMappings.ToList();
        var index = mappings.FindIndex(existing => Equal(existing.MappingId, mapping.MappingId));
        var next = index >= 0 ? mapping with { Revision = mappings[index].Revision + 1 } : mapping;
        if (index >= 0) { mappings[index] = next; } else { mappings.Add(next); }
        return profile with { PrincipalMappings = mappings.ToArray(), Revision = profile.Revision + 1 };
    }

    public static ClientCertificateTrustProfile DisableMapping(ClientCertificateTrustProfile profile, string mappingId)
    {
        var existing = profile.PrincipalMappings.FirstOrDefault(mapping => Equal(mapping.MappingId, mappingId));
        return existing is null ? profile : UpsertMapping(profile, existing with { Enabled = false });
    }

    private static bool Equal(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
