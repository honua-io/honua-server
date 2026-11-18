// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Content;

// Every public link expires: a link minted without an expiry gets the default lifetime, no link may
// outlive the maximum, and an older link stored without an expiry ends at the maximum after its creation.
// A person renews a link by minting a new one.
public static class PublicLinkLifetime
{
    public static readonly TimeSpan Default = TimeSpan.FromDays(30);

    public static readonly TimeSpan Maximum = TimeSpan.FromDays(365);

    public const string FutureMessage = "expiresAt must be in the future.";

    public const string MaximumMessage = "expiresAt must be at most 365 days from now.";

    // Links minted before every link carried an expiry end at the maximum lifetime after they were created.
    public static bool HasEnded(DateTimeOffset? expiresAt, DateTimeOffset createdAt, DateTimeOffset now)
        => now >= (expiresAt ?? createdAt + Maximum);

    // The effective expiry, or null with a refusal message when the requested expiry is not allowed.
    public static (DateTimeOffset? ExpiresAt, string? Error) Resolve(DateTimeOffset? requested, DateTimeOffset now)
    {
        if (requested is null)
        {
            return (now + Default, null);
        }

        if (requested <= now)
        {
            return (null, FutureMessage);
        }

        return requested > now + Maximum ? (null, MaximumMessage) : (requested, null);
    }
}
