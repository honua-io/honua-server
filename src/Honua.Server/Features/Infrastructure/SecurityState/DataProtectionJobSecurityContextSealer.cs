// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.SecurityState;

// Seals with the shared key ring, which is stored wrapped by the key-ring protector, so database
// access alone cannot produce a seal. The protector is resolved on first use, as for job secrets,
// because the key ring is stored through the job store.
internal sealed class DataProtectionJobSecurityContextSealer : IJobSecurityContextSealer
{
    private const string Marker = "dp1:";
    private const string Purpose = "Honua.JobSecurityContext.Seal.v1";

    private readonly Lazy<IDataProtector> _lazyProtector;

    public DataProtectionJobSecurityContextSealer(IServiceProvider services)
        => _lazyProtector = new(() => services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose));

    internal DataProtectionJobSecurityContextSealer(IDataProtectionProvider dataProtection)
        => _lazyProtector = new(() => dataProtection.CreateProtector(Purpose));

    private IDataProtector Protector => _lazyProtector.Value;

    public string Seal(string binding, JobSecurityContext context)
        => Marker + Protector.Protect(JobSecurityContextDigest.Compute(binding, context));

    public bool Verify(string binding, JobSecurityContext context, string? seal)
    {
        if (seal is null || !seal.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Protector.Unprotect(seal[Marker.Length..])),
                Encoding.UTF8.GetBytes(JobSecurityContextDigest.Compute(binding, context)));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

}
