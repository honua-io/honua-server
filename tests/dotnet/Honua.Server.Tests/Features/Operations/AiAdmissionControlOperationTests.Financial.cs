// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Security;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class AiAdmissionControlOperationTests
{
    [UnitTheory]
    [InlineData("unknown-fee")]
    [InlineData("duplicate-currency")]
    [InlineData("wrong-casing")]
    [InlineData("duplicate-basis")]
    public async Task Prepare_FinancialPolicyAmbiguousOrUnknownFields_RejectsBeforeLedgerRead(string kind)
    {
        var json = JsonSerializer.Serialize(AiFinancialTestPolicy.Create(), AiAdmissionControlJsonContext.Default.AiAdmissionPolicy);
        var node = JsonNode.Parse(json)!;
        if (kind == "unknown-fee") node["financial"]!["basis"]!["feePerRequest"] = 123;
        if (kind == "wrong-casing") node["financial"]!["Currency"] = "USD";
        json = node.ToJsonString();
        if (kind == "duplicate-currency")
            json = json.Replace("\"currency\":\"TST\"", "\"currency\":\"TST\",\"currency\":\"USD\"", StringComparison.Ordinal);
        if (kind == "duplicate-basis")
        {
            var basis = node["financial"]!["basis"]!.ToJsonString();
            json = json.Replace("\"basis\":" + basis, "\"basis\":" + basis + ",\"basis\":" + basis, StringComparison.Ordinal);
        }
        var store = Store(); using var services = Services(store, Principal("requester"));
        var id = "admin.ai.admission.policy-update";
        var request = Request(new Dictionary<string, string?> { ["policy"] = json }) with { OperationId = id };
        var executor = new AiAdmissionControlExecutor(id, services);
        var action = () => executor.PrepareAsync(request, Context(Principal("requester")));
        await action.Should().ThrowAsync<JsonException>();
        await store.DidNotReceiveWithAnyArgs().GetStateAsync(default!, default);
        await store.DidNotReceiveWithAnyArgs().ApplyControlAsync(default!, default);
    }

    [UnitTest]
    public async Task Prepare_FinancialPolicyComputedValidityCannotApproveMissingRates()
    {
        var policy = AiFinancialTestPolicy.Create();
        policy = policy with { Financial = policy.Financial! with { Basis = policy.Financial!.Basis with { InputRatePerMillion = 0 } } };
        var node = JsonNode.Parse(JsonSerializer.Serialize(policy, AiAdmissionControlJsonContext.Default.AiAdmissionPolicy))!;
        node["isValid"] = true;
        var store = Store(); using var services = Services(store, Principal("requester"));
        const string id = "admin.ai.admission.policy-update";
        var request = Request(new Dictionary<string, string?> { ["policy"] = node.ToJsonString() }) with { OperationId = id };
        var action = () => new AiAdmissionControlExecutor(id, services).PrepareAsync(request, Context(Principal("requester")));
        await action.Should().ThrowAsync<ArgumentException>();
        await store.DidNotReceiveWithAnyArgs().GetStateAsync(default!, default);
    }

    [UnitTest]
    public void FinancialCharge_FractionalInputAndOutput_RoundsEachDimensionUpward()
    {
        var basis = AiFinancialTestPolicy.Create().Financial!.Basis with { InputRatePerMillion = 1, OutputRatePerMillion = 1 };
        basis.Charge(1, 1).Should().Be(2, "two independently rounded fractional charges cannot be rounded as one bucket");
        var action = () => basis.Charge(long.MaxValue, long.MaxValue);
        action.Should().NotThrow("fixed integer intermediates do not overflow before denominator division");
        var extreme = basis with { InputRatePerMillion = 1_000_000_000, OutputRatePerMillion = 1_000_000_000 };
        action = () => extreme.Charge(long.MaxValue, long.MaxValue);
        action.Should().Throw<OverflowException>();
    }
}
