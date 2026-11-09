// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

internal sealed class HumanApprovalHttpClient(HttpClient client, string actorId) : IDisposable
{
    public HttpClient Client { get; } = client;
    public string ActorId { get; } = actorId;

    public static WebAppFixture Configure(
        WebAppFixture fixture, AdminAuthEndpointsTests.StubHttpClientFactory provider) =>
        AdminAuthEndpointsTests.ConfigureGenericOidcFixture(fixture, provider)
            .ConfigureWebHost(builder =>
            {
                if (string.IsNullOrWhiteSpace(builder.GetSetting("Public:BaseUrl")))
                    builder.UseSetting("Public:BaseUrl", "http://localhost");
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new IsolatedApprovalClientAddress());
                services.RemoveAll<IHttpClientFactory>();
                services.AddHttpClient(AdminAuthEndpoints.AdminAuthHttpClient)
                    .ConfigurePrimaryHttpMessageHandler(provider.CreateMessageHandler);
            });

    public static async Task<HumanApprovalHttpClient> AuthenticateAsync(
        WebAppFixture fixture, AdminAuthEndpointsTests.StubHttpClientFactory provider, string subject = "human-approver", string? tenantId = null)
    {
        provider.IdTokenOverride = AdminAuthEndpointsTests.CreateIdTokenForSubject(subject, subject, ["admin"], tenantId);
        var client = fixture.CreateClient();
        try
        {
            using var authorize = await client.PostAsJsonAsync("/api/v1/admin/auth/providers/oidc/authorize-url", new { });
            authorize.StatusCode.Should().Be(HttpStatusCode.OK);
            var authorization = await authorize.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
            var state = QueryHelpers.ParseQuery(new Uri(authorization!.AuthorizeUrl).Query)["state"].ToString();
            using var token = await client.PostAsJsonAsync("/api/v1/admin/auth/providers/oidc/token", new
            {
                grantType = "authorization_code",
                code = "authorization-code",
                state,
            });
            token.StatusCode.Should().Be(HttpStatusCode.NoContent);
            using var sessionResponse = await client.GetAsync("/api/v1/admin/auth/session");
            sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var session = await sessionResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
            session!.IsAuthenticated.Should().BeTrue();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(session.Claims.Select(
                claim => new Claim(claim.Type, claim.Value)), OidcAuthenticationExtensions.AdminSessionScheme));
            var actor = CanonicalSecurityActor.Resolve(principal);
            actor.Should().NotBeNull();
            return new HumanApprovalHttpClient(client, actor!.ActorId);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<OperationHumanApprovalReview> OpenReviewAsync(string proposalId)
    {
        using var response = await Client.GetAsync($"/api/v1/admin/proposals/{proposalId}/approval-review");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var snapshot = await response.Content.ReadFromJsonAsync(ProposalHumanReviewJsonContext.Default.ProposalHumanReviewResponse);
        snapshot!.Proposal.Version.Should().Be(snapshot.Review.ProposalVersion);
        snapshot.Proposal.PlanHash.Should().Be(snapshot.Review.PlanHash);
        return snapshot.Review;
    }

    public async Task<HttpResponseMessage> ApproveAsync(string proposalId) =>
        await SubmitReviewAsync(proposalId, await OpenReviewAsync(proposalId));

    public async Task<HttpResponseMessage> ApproveGateAsync(string workflowId, string gate)
        => await SubmitGateReviewAsync(workflowId, gate, await OpenGateReviewAsync(workflowId, gate));

    public async Task<CoordinatedReleaseHumanReviewResponse> OpenGateReviewAsync(string workflowId, string gate)
    {
        using var response = await Client.GetAsync(
            $"/api/v1/admin/metadata/coordinated-releases/operations/{workflowId}/approval-review/{gate}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var snapshot = await response.Content.ReadFromJsonAsync(
            CoordinatedReleaseJsonContext.Default.CoordinatedReleaseHumanReviewResponse);
        snapshot!.Review.WorkflowId.Should().Be(workflowId);
        return snapshot;
    }

    public async Task<HttpResponseMessage> SubmitGateReviewAsync(
        string workflowId, string gate, CoordinatedReleaseHumanReviewResponse snapshot)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/admin/metadata/coordinated-releases/operations/{workflowId}/approve/{gate}");
        request.Headers.Add(OperationHumanApprovalAuthority.CapabilityHeader, snapshot.Review.Capability);
        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> SubmitReviewAsync(
        string proposalId, OperationHumanApprovalReview review, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/proposals/{proposalId}/approve");
        request.Headers.Add(OperationHumanApprovalAuthority.CapabilityHeader, review.Capability);
        return await Client.SendAsync(request, cancellationToken);
    }

    public void Dispose() => Client.Dispose();
    private sealed class IsolatedApprovalClientAddress : IStartupFilter
    {
        private readonly IPAddress _address = new(Guid.NewGuid().ToByteArray());

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use((HttpContext context, RequestDelegate continueRequest) =>
            {
                context.Connection.RemoteIpAddress = _address;
                return continueRequest(context);
            });
            next(application);
        };
    }

}
