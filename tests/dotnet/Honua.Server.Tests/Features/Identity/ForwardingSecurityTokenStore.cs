// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using NSubstitute;

namespace Honua.Server.Tests.Features.Identity;

// A security token store substitute that forwards every call to an in-memory store, so a test can
// override single calls (fail one kind, record a lifetime) and keep real behaviour for the rest.
internal static class ForwardingSecurityTokenStore
{
    public static ISecurityTokenStore Create(InMemorySecurityTokenStore inner)
    {
        var tokens = Substitute.For<ISecurityTokenStore>();
        tokens.StoreAsync(Arg.Any<SecurityTokenKey>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => inner.StoreAsync(
                call.ArgAt<SecurityTokenKey>(0), call.ArgAt<ReadOnlyMemory<byte>>(1), call.ArgAt<TimeSpan>(2), call.ArgAt<CancellationToken>(3)));
        tokens.FindAsync(Arg.Any<SecurityTokenKey>(), Arg.Any<CancellationToken>())
            .Returns(call => inner.FindAsync(call.ArgAt<SecurityTokenKey>(0), call.ArgAt<CancellationToken>(1)));
        tokens.ConsumeAsync(Arg.Any<SecurityTokenKey>(), Arg.Any<CancellationToken>())
            .Returns(call => inner.ConsumeAsync(call.ArgAt<SecurityTokenKey>(0), call.ArgAt<CancellationToken>(1)));
        tokens.RestoreAsync(Arg.Any<SecurityTokenKey>(), Arg.Any<StoredSecurityToken>(), Arg.Any<CancellationToken>())
            .Returns(call => inner.RestoreAsync(
                call.ArgAt<SecurityTokenKey>(0), call.ArgAt<StoredSecurityToken>(1), call.ArgAt<CancellationToken>(2)));
        tokens.RevokeAsync(Arg.Any<SecurityTokenKey>(), Arg.Any<CancellationToken>())
            .Returns(call => inner.RevokeAsync(call.ArgAt<SecurityTokenKey>(0), call.ArgAt<CancellationToken>(1)));
        return tokens;
    }

    // Keys of the given kind, for overriding one kind's calls.
    public static SecurityTokenKey OfKind(SecurityTokenKind kind) => Arg.Is<SecurityTokenKey>(key => key.Kind == kind);
}
