// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.TestKit.Helpers;

public sealed record ExpiringCredential(string Token, DateTimeOffset ExpiresAt)
{
    public static ExpiringCredential From(PortalTokenIssuance issuance) => new(issuance.Token, issuance.ExpiresAt);
}

public sealed record CredentialSetup<TResult>(ExpiringCredential Credential, TResult Result);

// An expiry test waits out a credential's real lifetime, so the first lifetime is short. A slow runner can
// spend it on the setup that needs the credential valid; that setup then runs again with the next lifetime,
// so no test asserts on a credential that ended before its setup finished.
public static class ExpiringCredentials
{
    public static IReadOnlyList<TimeSpan> Expiring { get; } =
        [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public static IReadOnlyList<TimeSpan> Durable { get; } = [TimeSpan.FromMinutes(2)];

    public static async Task<CredentialSetup<TResult>> IssueForSetupAsync<TResult>(
        IReadOnlyList<TimeSpan> lifetimes,
        Func<TimeSpan, Task<ExpiringCredential>> issue,
        Func<ExpiringCredential, Task<TResult>> setup)
        where TResult : class, IDisposable
    {
        for (var attempt = 0; attempt < lifetimes.Count - 1; attempt++)
        {
            var credential = await issue(lifetimes[attempt]);
            var result = await SetupOrDefaultWhenExpiredAsync(credential, setup);
            if (result is not null && DateTimeOffset.UtcNow < credential.ExpiresAt)
            {
                return new CredentialSetup<TResult>(credential, result);
            }

            result?.Dispose();
        }

        var last = await issue(lifetimes[^1]);
        return new CredentialSetup<TResult>(last, await setup(last));
    }

    public static async Task WaitPastExpiryAsync(ExpiringCredential credential, CancellationToken cancellationToken)
    {
        var remaining = credential.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static async Task<TResult?> SetupOrDefaultWhenExpiredAsync<TResult>(
        ExpiringCredential credential, Func<ExpiringCredential, Task<TResult>> setup)
        where TResult : class, IDisposable
    {
        try
        {
            return await setup(credential);
        }
        catch (Exception) when (DateTimeOffset.UtcNow >= credential.ExpiresAt)
        {
            return null;
        }
    }
}
