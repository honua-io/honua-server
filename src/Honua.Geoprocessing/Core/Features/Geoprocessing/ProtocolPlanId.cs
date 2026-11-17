// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Geoprocessing;

// The plan id is part of the idempotency request fingerprint. A protocol adapter that names the
// plan itself therefore names a keyed request after its key: a fresh random id would make every
// retry of the same keyed request conflict with the job it should return.
public static class ProtocolPlanId
{
    public static string Create(string prefix, string? idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var suffix = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))[..32];
        return $"{prefix}-{suffix}";
    }
}
