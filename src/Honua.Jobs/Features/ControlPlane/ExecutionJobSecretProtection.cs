// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// Protects credential-bearing job parameters in the stored execution record, so a database reader
// or backup holder does not see a live job capability. A host without a protector keeps stored
// values unchanged, which preserves a protected value it cannot read when it rewrites the record.
internal interface IExecutionJobSecretProtector
{
    string Protect(string value);

    // Returns the stored value unchanged when it cannot be read, so no caller sees a usable capability.
    string Unprotect(string stored);

    string Seal(string digest);

    // False unless the seal was made for this digest with a key this node holds.
    bool VerifySeal(string digest, string seal);
}

internal sealed class UnprotectedExecutionJobSecrets : IExecutionJobSecretProtector
{
    public static readonly UnprotectedExecutionJobSecrets Instance = new();

    public string Protect(string value) => value;

    public string Unprotect(string stored) => stored;

    public string Seal(string digest) => digest;

    public bool VerifySeal(string digest, string seal) => string.Equals(digest, seal, StringComparison.Ordinal);
}

internal static class ExecutionJobSecretParameters
{
    // The scoped job capability handed to custom-code jobs.
    public const string JobToken = "env.HONUA_JOB_TOKEN";

    // The single-use code a custom-code job exchanges for its capability at start.
    public const string RedemptionCode = "env.HONUA_JOB_REDEMPTION_CODE";

    private static readonly string[] SecretKeys = [JobToken, RedemptionCode];

    public static ExecutionJobRecord Protect(ExecutionJobRecord job, IExecutionJobSecretProtector protector)
        => Transform(job, protector, static (p, value) => p.Protect(value));

    public static ExecutionJobRecord Unprotect(ExecutionJobRecord job, IExecutionJobSecretProtector protector)
        => Transform(job, protector, static (p, value) => p.Unprotect(value));

    private static ExecutionJobRecord Transform(
        ExecutionJobRecord job,
        IExecutionJobSecretProtector protector,
        Func<IExecutionJobSecretProtector, string, string> transform)
    {
        if (ReferenceEquals(protector, UnprotectedExecutionJobSecrets.Instance) ||
            !SecretKeys.Any(key => job.Spec.Parameters.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)))
        {
            return job;
        }

        var parameters = new Dictionary<string, string>(job.Spec.Parameters, StringComparer.Ordinal);
        foreach (var key in SecretKeys)
        {
            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                parameters[key] = transform(protector, value);
            }
        }

        return job with { Spec = job.Spec with { Parameters = parameters } };
    }
}
