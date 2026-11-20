// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Identity;

public sealed class SamlSessionIndexTests
{
    private static readonly SamlSessionReference _reference = new("index-user@example.com", "_idx1");

    private readonly ISecurityTokenStore _tokens = ForwardingSecurityTokenStore.Create(new InMemorySecurityTokenStore());
    private readonly SamlSessionIndex _index;

    public SamlSessionIndexTests()
        => _index = new SamlSessionIndex(
            _tokens,
            new EphemeralDataProtectionProvider(),
            Options.Create(new SamlAuthenticationOptions { IdpEntityId = "https://idp.example.com/metadata" }));

    [UnitTest]
    public async Task RecordAsync_LaterSessionWithEarlierExpiry_KeepsTheEntryForTheLongerSession()
    {
        var now = DateTimeOffset.UtcNow;
        await _index.RecordAsync(new SamlIndexedSession(_reference, "session-long", now.AddHours(1)), CancellationToken.None);
        await _index.RecordAsync(new SamlIndexedSession(_reference, "session-short", now.AddMinutes(5)), CancellationToken.None);

        var lastLifetime = (TimeSpan)_tokens.ReceivedCalls()
            .Last(call => call.GetMethodInfo().Name == nameof(ISecurityTokenStore.StoreAsync))
            .GetArguments()[2]!;
        Assert.True(lastLifetime > TimeSpan.FromMinutes(55));
        Assert.Equal(new[] { "session-long", "session-short" }, await _index.FindAsync(_reference, CancellationToken.None));
    }

    [UnitTest]
    public async Task FindAsync_DifferentSubjectWithTheSameSessionIndex_FindsNothing()
    {
        await _index.RecordAsync(
            new SamlIndexedSession(_reference, "session-a", DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);

        Assert.Empty(await _index.FindAsync(
            new SamlSessionReference("other-user@example.com", _reference.SessionIndex), CancellationToken.None));
    }

    [UnitTest]
    public async Task ForgetAsync_RemovesTheEntry()
    {
        await _index.RecordAsync(
            new SamlIndexedSession(_reference, "session-a", DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);

        await _index.ForgetAsync(_reference, CancellationToken.None);

        Assert.Empty(await _index.FindAsync(_reference, CancellationToken.None));
    }
}
