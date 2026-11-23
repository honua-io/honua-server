// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Guardrails;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Studio;

public sealed partial class StudioPublicationSelfApprovalTests
{
    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    [Endpoint("POST /mcp resources/read honua://proposals/{proposalId}")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task CreateDraft_AfterHumanApprovalAndRestart_ProposerRecoversIdentityAndCurrentGeneration()
    {
        const string subject = "recover-draft-proposer";
        // An agent creates its own draft directly under the built-in policy; an operator override
        // keeps creation behind a human approval so the proposal recovery path runs.
        _fixture.Services.GetRequiredService<IOptions<GuardrailLadderOptions>>().Value
            .Overrides[nameof(OperationClass.StudioDraftMutation)] = nameof(GuardrailTier.RequiresApproval);
        using var proposer = CreateBearerClient(CreateToken(subject));
        var response = await ApprovedStudioDraftCall.RpcAsync(proposer,
            $$$$"""{"jsonrpc":"2.0","id":"create-recoverable-draft","method":"tools/call","params":{"name":"honua_studio_create_draft","arguments":{"packageKey":"recover-draft-{{{{Guid.NewGuid():N}}}}","family":"dashboard","schemaVersion":"1.0"}}}""");
        var created = ApprovedStudioDraftCall.ReadResult(response, "honua_studio_create_draft");
        var pending = created.GetProperty("operation");
        pending.GetProperty("status").GetString().Should().Be("RequiresApproval");
        var proposalId = pending.GetProperty("proposalId").GetString()!;
        (await ReadDraftProposalAsync(proposer, proposalId)).TryGetProperty("resourceIds", out _).Should().BeFalse();
        using (var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider, "recover-draft-human", Tenant))
        using (var approved = await human.ApproveAsync(proposalId))
            approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());

        await _fixture.RestartHostAsync();
        using var resumed = CreateBearerClient(CreateToken(subject));
        var completed = await ReadDraftProposalAsync(resumed, proposalId);
        completed.GetProperty("status").GetString().Should().Be("Succeeded");
        var ids = completed.GetProperty("resourceIds");
        var draftId = Guid.Parse(ids.GetProperty("draftId").GetString()!);
        var itemId = Guid.Parse(ids.GetProperty("itemId").GetString()!);
        using var scope = _fixture.Services.CreateScope();
        var operation = await scope.ServiceProvider.GetRequiredService<IOperationInstanceStore>()
            .GetAsync(pending.GetProperty("operationInstanceId").GetString()!);
        operation!.Status.Should().Be(OperationHandleStatus.Completed);
        operation.ResourceIds["draftId"].Should().Be(draftId.ToString("D"));
        var draft = await CallToolAsync(resumed, "honua_studio_get_draft", $$"""{"draftId":"{{draftId:D}}"}""");
        draft.GetProperty("draftId").GetGuid().Should().Be(draftId);
        draft.GetProperty("itemId").GetGuid().Should().Be(itemId);
        draft.GetProperty("generation").GetInt64().Should().Be(1);
    }

    private static async Task<JsonElement> ReadDraftProposalAsync(HttpClient client, string proposalId)
    {
        var response = await ApprovedStudioDraftCall.RpcAsync(client,
            $$$"""{"jsonrpc":"2.0","id":"recover-draft","method":"resources/read","params":{"uri":"honua://proposals/{{{proposalId}}}"}}""");
        response.TryGetProperty("error", out _).Should().BeFalse(response.GetRawText());
        using var content = JsonDocument.Parse(response.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!);
        return content.RootElement.Clone();
    }
}
