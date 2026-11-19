// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Authorization.Domain;

// The one canonical digest every seal over a stored submitter snapshot signs: the binding (the record
// and the work it authorizes) and each snapshot field, every part length-prefixed so no two different
// inputs encode the same. It names its fields explicitly, so adding a property does not change the
// digest of a stored snapshot.
public static class JobSecurityContextDigest
{
    public static string Binding(params string?[] parts) => Encode(parts);

    public static string Compute(string binding, JobSecurityContext snapshot)
    {
        var fields = new List<string?>
        {
            binding,
            snapshot.PrincipalId,
            snapshot.TenantId,
            snapshot.RoleClaimType,
            snapshot.OwnerActorId,
            snapshot.WorkspaceOwnerId,
        };
        foreach (var claim in snapshot.Claims)
        {
            fields.Add(claim.Type);
            fields.Add(claim.Value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(fields))));
    }

    private static string Encode(IEnumerable<string?> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            builder.Append(part is null ? "-" : $"{part.Length}:{part}").Append('\n');
        }

        return builder.ToString();
    }
}
