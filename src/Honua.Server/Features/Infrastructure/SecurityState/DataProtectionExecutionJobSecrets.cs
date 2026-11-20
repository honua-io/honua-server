// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.ControlPlane;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.Server.Features.Infrastructure.SecurityState;

// Stores job capabilities protected with the shared key ring. A value without the marker is a
// record written before protection and is read as is; it is protected on its next write.
internal sealed class DataProtectionExecutionJobSecrets : IExecutionJobSecretProtector
{
    internal const string Marker = "dp1:";

    private readonly Lazy<IDataProtector> _protector;
    private readonly Lazy<IDataProtector> _sealer;

    public DataProtectionExecutionJobSecrets(IDataProtectionProvider dataProtection)
    {
        _protector = new(() => dataProtection.CreateProtector(Purpose));
        _sealer = new(() => dataProtection.CreateProtector(SealPurpose));
    }

    // The shared key ring is stored through JobStoreDatabase, which holds this protector, so the
    // provider is resolved on first use rather than while JobStoreDatabase is being built.
    public DataProtectionExecutionJobSecrets(IServiceProvider services)
    {
        _protector = new(() => services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose));
        _sealer = new(() => services.GetRequiredService<IDataProtectionProvider>().CreateProtector(SealPurpose));
    }

    private const string Purpose = "Honua.ExecutionJobs.Secrets";
    private const string SealPurpose = "Honua.ExecutionJobs.SubmitterSeal.v1";

    public string Protect(string value)
        => value.StartsWith(Marker, StringComparison.Ordinal) ? value : Marker + _protector.Value.Protect(value);

    public string Unprotect(string stored)
    {
        if (!stored.StartsWith(Marker, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            return _protector.Value.Unprotect(stored[Marker.Length..]);
        }
        catch (CryptographicException)
        {
            // A key outside this node's ring: the protected value never matches a presented
            // capability, so the job fails closed, and it is kept for a node that holds the ring.
            return stored;
        }
    }

    public string Seal(string digest) => Marker + _sealer.Value.Protect(digest);

    public bool VerifySeal(string digest, string seal)
    {
        if (!seal.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var sealedDigest = _sealer.Value.Unprotect(seal[Marker.Length..]);
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(sealedDigest),
                System.Text.Encoding.UTF8.GetBytes(digest));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
