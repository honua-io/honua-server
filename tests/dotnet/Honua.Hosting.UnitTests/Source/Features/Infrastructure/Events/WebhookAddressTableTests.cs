// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Tests.OutboundSecurity;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(TestProtocols.TestQuality)]
public sealed class WebhookAddressTableTests
{
    private const string WebhookUrl = "https://hooks.example.com/honua";

    [UnitTheory]
    [Operation(Operations.TestInfrastructure)]
    [MemberData(nameof(OutboundAddressCases.Disallowed), MemberType = typeof(OutboundAddressCases))]
    public async Task ValidateAsync_HostResolvingToDisallowedRange_Fails(string address, string range)
    {
        var result = await FeatureChangeWebhookUrlValidation.ValidateAsync(WebhookUrl, Resolve(address));

        result.IsValid.Should().BeFalse(range);
    }

    [UnitTheory]
    [Operation(Operations.TestInfrastructure)]
    [MemberData(nameof(OutboundAddressCases.Allowed), MemberType = typeof(OutboundAddressCases))]
    public async Task ValidateAsync_HostResolvingToPublicAddress_Succeeds(string address, string range)
    {
        var result = await FeatureChangeWebhookUrlValidation.ValidateAsync(WebhookUrl, Resolve(address));

        result.IsValid.Should().BeTrue(range);
    }

    [UnitTheory]
    [Operation(Operations.TestInfrastructure)]
    [MemberData(nameof(OutboundAddressCases.Disallowed), MemberType = typeof(OutboundAddressCases))]
    public async Task Delivery_HostResolvingToDisallowedRange_IsRefusedBeforeConnecting(string address, string range)
    {
        using var client = new HttpClient(WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler(Resolve(address)));

        var send = () => client.PostAsync(WebhookUrl, new StringContent("{}"));

        var refusal = (await send.Should().ThrowAsync<HttpRequestException>(range)).Which;
        $"{refusal.Message} {refusal.InnerException?.Message}".Should().Contain(
            FeatureChangeWebhookUrlValidation.DisallowedAddressMessage, range);
    }

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolve(string address)
        => (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) });
}
