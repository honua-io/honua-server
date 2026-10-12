// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Stateless MCP sessions (honua-server#5629): on a host that runs as several
/// instances with no shared session store (AWS Lambda with Redis off), the
/// session id carries the negotiated state under a signature every instance can
/// check, so a session minted on one instance answers the negotiated view on
/// any other, and an id that fails validation still answers HTTP 404.
/// </summary>
public sealed class McpStatelessSessionTests
{
    private const string Secret = "Host=db;Database=honua;Username=honua;Password=shared-by-every-instance";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 11, 42, 0, TimeSpan.Zero);

    // ---- token codec --------------------------------------------------------

    [UnitTest]
    public void Token_RoundTripsTheNegotiatedState()
    {
        var time = new MutableTimeProvider(Start);
        var tokens = new McpStatelessSessionTokens(Secret, Lifetime, time);

        var id = tokens.Mint("sub:alice", elicitationSupported: true, "setup");

        tokens.TryRead(id, out var claims, out var binding).Should().BeTrue();
        claims.WorkflowView.Should().Be("setup");
        claims.ElicitationSupported.Should().BeTrue();
        claims.CreatedUtc.Should().Be(Start);
        tokens.IsBoundTo(binding, "sub:alice").Should().BeTrue();
        tokens.IsBoundTo(binding, "sub:mallory").Should().BeFalse();
        tokens.IsBoundTo(binding, McpSessionManager.AnonymousPrincipalKey).Should().BeFalse();
        id.Should().NotContain("alice", "the principal key is never written into the id");
    }

    [UnitTest]
    public void Token_WithoutAView_RoundTripsAsNull()
    {
        var tokens = new McpStatelessSessionTokens(Secret, Lifetime, new MutableTimeProvider(Start));

        var id = tokens.Mint(McpSessionManager.AnonymousPrincipalKey, elicitationSupported: false, workflowView: null);

        tokens.TryRead(id, out var claims, out var binding).Should().BeTrue();
        claims.WorkflowView.Should().BeNull();
        claims.ElicitationSupported.Should().BeFalse();
        tokens.IsBoundTo(binding, McpSessionManager.AnonymousPrincipalKey).Should().BeTrue();
    }

    [UnitTest]
    public void Token_IsAWellFormedTransportSessionId_EvenForTheLongestView()
    {
        var tokens = new McpStatelessSessionTokens(Secret, Lifetime, new MutableTimeProvider(Start));

        var id = tokens.Mint("sub:alice", true, new string('v', McpStatelessSessionTokens.MaxViewBytes));

        id.Length.Should().BeLessThanOrEqualTo(172);
        McpEndpointExtensions.IsWellFormedSessionId(id).Should().BeTrue("ids must be visible ASCII per the transport");
        tokens.Invoking(t => t.Mint("sub:alice", false, new string('v', McpStatelessSessionTokens.MaxViewBytes + 1)))
            .Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Token_EachMintIsUnique()
    {
        var tokens = new McpStatelessSessionTokens(Secret, Lifetime, new MutableTimeProvider(Start));

        tokens.Mint("sub:alice", false, "setup").Should().NotBe(tokens.Mint("sub:alice", false, "setup"));
    }

    [UnitTest]
    public void Token_Tampered_IsRejected()
    {
        var tokens = new McpStatelessSessionTokens(Secret, Lifetime, new MutableTimeProvider(Start));
        var id = tokens.Mint("sub:alice", false, "setup");
        var forgedView = tokens.Mint("sub:alice", false, "operate");

        foreach (var index in new[] { McpStatelessSessionTokens.Prefix.Length, id.Length / 2, id.Length - 1 })
        {
            var chars = id.ToCharArray();
            chars[index] = chars[index] == 'A' ? 'B' : 'A';
            tokens.TryRead(new string(chars), out _, out _).Should().BeFalse($"a flipped character at {index} must fail the tag");
        }

        // Splice the payload of one token onto the tag of another.
        var spliced = forgedView[..^22] + id[^22..];
        tokens.TryRead(spliced, out _, out _).Should().BeFalse();
        tokens.TryRead(id[..^1], out _, out _).Should().BeFalse("a truncated token is rejected");
        tokens.TryRead(new string('f', 64), out _, out _).Should().BeFalse("a random in-memory id is not a token");
        tokens.TryRead("hs1.", out _, out _).Should().BeFalse();
        tokens.TryRead("hs1.***", out _, out _).Should().BeFalse();
        tokens.TryRead(string.Empty, out _, out _).Should().BeFalse();
    }

    [UnitTest]
    public void Token_MintedUnderAnotherKey_IsRejected()
    {
        var time = new MutableTimeProvider(Start);
        var mine = new McpStatelessSessionTokens(Secret, Lifetime, time);
        var theirs = new McpStatelessSessionTokens("another deployment", Lifetime, time);

        mine.TryRead(theirs.Mint("sub:alice", false, "setup"), out _, out _).Should().BeFalse();
    }

    [UnitTest]
    public void Token_ExpiresAtTheEndOfItsAbsoluteLifetime()
    {
        var time = new MutableTimeProvider(Start);
        var tokens = new McpStatelessSessionTokens(Secret, Lifetime, time);
        var id = tokens.Mint("sub:alice", false, "setup");

        time.Advance(Lifetime);
        tokens.TryRead(id, out _, out _).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(1));
        tokens.TryRead(id, out _, out _).Should().BeFalse();
    }

    [UnitTest]
    public void Token_MintedFarInTheFuture_IsRejected_ButClockSkewIsTolerated()
    {
        var minter = new MutableTimeProvider(Start);
        var tokens = new McpStatelessSessionTokens(Secret, Lifetime, minter);
        var reader = new MutableTimeProvider(Start - TimeSpan.FromMinutes(4));
        var skewed = new McpStatelessSessionTokens(Secret, Lifetime, reader);

        var id = tokens.Mint("sub:alice", false, "setup");

        skewed.TryRead(id, out _, out _).Should().BeTrue("a few minutes of skew between instances is tolerated");
        reader.Advance(TimeSpan.FromMinutes(-10));
        skewed.TryRead(id, out _, out _).Should().BeFalse();
    }

    // ---- session manager, two instances, no shared store ----------------------

    [UnitTest]
    public async Task SessionMintedOnA_IsHonouredOnAFreshInstanceB_WithTheNegotiatedView()
    {
        var time = new MutableTimeProvider(Start);
        var a = NewStatelessManager(time);
        var b = NewStatelessManager(time);

        a.Mode.Should().Be(McpSessionMode.Stateless);
        a.HasSharedStore.Should().BeFalse();
        var id = await a.TryCreateSessionAsync("sub:alice", elicitationSupported: true, "setup", CancellationToken.None);
        id.Should().StartWith(McpStatelessSessionTokens.Prefix);

        b.GetWorkflowView(id!).Should().BeNull("B has never served the session");
        (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
        b.GetWorkflowView(id!).Should().Be("setup", "the view negotiated on A must be answered by B");
        b.SupportsElicitation(id!).Should().BeTrue();
        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
        a.GetWorkflowView(id!).Should().Be("setup");
    }

    [UnitTest]
    public async Task StatelessSession_IsBoundToItsPrincipal_OnEveryInstance()
    {
        var time = new MutableTimeProvider(Start);
        var a = NewStatelessManager(time);
        var b = NewStatelessManager(time);
        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);

        (await b.ValidateAccessAsync(id!, "sub:mallory", CancellationToken.None))
            .Should().Be(McpSessionValidation.PrincipalMismatch);
        (await b.ValidateAccessAsync(id!, McpSessionManager.AnonymousPrincipalKey, CancellationToken.None))
            .Should().Be(McpSessionValidation.PrincipalMismatch);
        b.GetWorkflowView(id!).Should().BeNull("a mismatched principal never adopts the session");
    }

    [UnitTest]
    public async Task TamperedOrForeignIds_AreUnknown_SoTheTransportAnswers404()
    {
        var time = new MutableTimeProvider(Start);
        var a = NewStatelessManager(time);
        var b = NewStatelessManager(time);
        var otherDeployment = NewStatelessManager(time, secret: "another deployment");
        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        var tampered = id![..^2] + (id[^2] == 'A' ? "B" : "A") + id[^1];

        (await b.ValidateAccessAsync(tampered, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Unknown);
        (await b.ValidateAccessAsync(new string('f', 64), "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Unknown);
        (await otherDeployment.ValidateAccessAsync(id, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Unknown);
    }

    [UnitTest]
    public async Task StatelessSession_ExpiresAfterItsLifetime_EvenOnTheMintingInstance()
    {
        var time = new MutableTimeProvider(Start);
        var a = NewStatelessManager(time);
        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);

        time.Advance(Lifetime + TimeSpan.FromMinutes(1));

        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Unknown);
        a.GetWorkflowView(id!).Should().BeNull();
    }

    [UnitTest]
    public async Task StatelessSession_OutlivesTheLocalIdleWindow_UntilItsLifetimeEnds()
    {
        var time = new MutableTimeProvider(Start);
        var a = NewStatelessManager(time);
        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);

        time.Advance(TimeSpan.FromHours(1));

        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
        a.GetWorkflowView(id!).Should().Be("setup");
    }

    [UnitTest]
    public async Task Delete_OfAStatelessSession_IsNotClaimedAsATermination()
    {
        var time = new MutableTimeProvider(Start);
        var a = NewStatelessManager(time);
        var b = NewStatelessManager(time);
        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        var lifetime = a.GetLifetimeToken(id!);

        (await a.TerminateAsync(id!, CancellationToken.None)).Should().Be(McpSessionTermination.NotRevocable);

        lifetime.IsCancellationRequested.Should().BeTrue("node-local work on the deleting instance ends");
        (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Valid, "no instance can revoke a stateless id, so the server does not claim to");
    }

    // ---- mode selection per topology ------------------------------------------

    [UnitTest]
    public void Resolver_WithASharedStore_IsShared_OnEveryTopology()
    {
        Resolve(hasStore: true, env: Lambda).Mode.Should().Be(McpSessionMode.Shared);
        Resolve(hasStore: true).Mode.Should().Be(McpSessionMode.Shared);
        Resolve(hasStore: true, settings: new() { ["Mcp:SessionMode"] = "Stateless" }).Mode.Should().Be(McpSessionMode.Shared);
    }

    [UnitTest]
    public void Resolver_OnASingleInstanceWithoutAStore_IsInMemory()
    {
        var selection = Resolve();

        selection.Mode.Should().Be(McpSessionMode.InMemory);
        selection.SigningSecret.Should().BeNull();
        selection.Degraded.Should().BeFalse();
        Resolve(settings: new() { ["Deployment:Mode"] = "SingleInstance" }).Mode.Should().Be(McpSessionMode.InMemory);
    }

    [UnitTest]
    public void Resolver_OnLambdaWithoutAStore_IsStateless_KeyedFromTheSharedConnectionString()
    {
        var selection = Resolve(env: Lambda);

        selection.Mode.Should().Be(McpSessionMode.Stateless);
        selection.Reason.Should().Be("AWS_LAMBDA_FUNCTION_NAME");
        selection.SigningSecret.Should().Be(Secret);
    }

    [UnitTest]
    public void Resolver_OnOtherMultiInstanceHosts_IsStateless()
    {
        Resolve(env: new() { ["HONUA_CONTROL_PLANE_LAMBDA_HANDLER"] = "reconcile" }).Mode.Should().Be(McpSessionMode.Stateless);
        Resolve(env: new() { ["K_SERVICE"] = "honua" }).Mode.Should().Be(McpSessionMode.Stateless);
        Resolve(env: new() { ["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated" }).Mode.Should().Be(McpSessionMode.Stateless);
        Resolve(settings: new() { ["Deployment:Mode"] = "MultiNode" }).Mode.Should().Be(McpSessionMode.Stateless);
    }

    [UnitTest]
    public void Resolver_PrefersAnExplicitSigningKey()
    {
        Resolve(env: Lambda, settings: new() { ["Mcp:SessionSigningKey"] = "explicit" })
            .SigningSecret.Should().Be("explicit");
    }

    [UnitTest]
    public void Resolver_HonoursAnExplicitSetting()
    {
        Resolve(env: Lambda, settings: new() { ["Mcp:SessionMode"] = "InMemory" }).Mode.Should().Be(McpSessionMode.InMemory);
        Resolve(settings: new() { ["Mcp:SessionMode"] = "Stateless" }).Mode.Should().Be(McpSessionMode.Stateless);
    }

    [UnitTest]
    public void Resolver_OnAMultiInstanceHostWithoutASecret_StaysInMemory_AndSaysSo()
    {
        var selection = Resolve(env: Lambda, connectionString: null);

        selection.Mode.Should().Be(McpSessionMode.InMemory);
        selection.Degraded.Should().BeTrue();
    }

    [UnitTest]
    public void Composition_SelectsTheSessionModeForTheHost()
    {
        Compose(new() { ["Mcp:SessionMode"] = "Stateless" }).Mode.Should().Be(McpSessionMode.Stateless);
        Compose(new() { ["Mcp:SessionMode"] = "InMemory" }).Mode.Should().Be(McpSessionMode.InMemory);
        Compose(new() { ["Mcp:SessionMode"] = "Stateless" }, services => services.AddSingleton(Substitute.For<IConnectionMultiplexer>()))
            .Mode.Should().Be(McpSessionMode.Shared, "a connected Redis always shares sessions");
        Compose(new() { ["Mcp:SessionMode"] = "Stateless", ["ConnectionStrings:DefaultConnection"] = null })
            .Mode.Should().Be(McpSessionMode.InMemory, "without a signing secret no stateless id can be minted");
    }

    [UnitTest]
    public void SessionModeNames_AreTheWireContract()
    {
        McpDataAccessSurface.SessionModeName(McpSessionMode.Shared).Should().Be("shared");
        McpDataAccessSurface.SessionModeName(McpSessionMode.Stateless).Should().Be("stateless");
        McpDataAccessSurface.SessionModeName(McpSessionMode.InMemory).Should().Be("in-memory");
    }

    private static Dictionary<string, string?> Lambda => new() { ["AWS_LAMBDA_FUNCTION_NAME"] = "honua-api" };

    private static McpSessionModeSelection Resolve(
        bool hasStore = false,
        Dictionary<string, string?>? env = null,
        Dictionary<string, string?>? settings = null,
        string? connectionString = Secret)
    {
        var values = new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>());
        if (connectionString is not null)
        {
            values.TryAdd("ConnectionStrings:DefaultConnection", connectionString);
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddMcpOptions(configuration);
        var options = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<McpOptions>>().Value;
        var environment = env ?? new Dictionary<string, string?>();
        return McpSessionModeResolver.Resolve(
            options,
            hasStore,
            configuration,
            name => environment.TryGetValue(name, out var value) ? value : null);
    }

    private static McpSessionManager Compose(
        Dictionary<string, string?> settings,
        Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = Secret };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMcpOptions(configuration);
        configure?.Invoke(services);
        services.AddMcpSessionManager();
        return services.BuildServiceProvider().GetRequiredService<McpSessionManager>();
    }

    private static McpSessionManager NewStatelessManager(TimeProvider time, string secret = Secret) =>
        new(
            idleTimeout: TimeSpan.FromMinutes(30),
            timeProvider: time,
            statelessTokens: new McpStatelessSessionTokens(secret, Lifetime, time));

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
