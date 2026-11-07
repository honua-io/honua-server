// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

internal sealed class HumanApprovalHttpClient(HttpClient client) : IDisposable
{
    public HttpClient Client { get; } = client;

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
                services.RemoveAll<IHttpClientFactory>();
                services.AddHttpClient(AdminAuthEndpoints.AdminAuthHttpClient)
                    .ConfigurePrimaryHttpMessageHandler(provider.CreateMessageHandler);
            });

    public static async Task<HumanApprovalHttpClient> AuthenticateAsync(
        WebAppFixture fixture, AdminAuthEndpointsTests.StubHttpClientFactory provider, string subject = "human-approver")
    {
        provider.IdTokenOverride = AdminAuthEndpointsTests.CreateIdTokenForSubject(subject, subject, "admin");
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
            return new HumanApprovalHttpClient(client);
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

    public async Task<HttpResponseMessage> ApproveAsync(string proposalId)
    {
        var review = await OpenReviewAsync(proposalId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/proposals/{proposalId}/approve");
        request.Headers.Add(OperationHumanApprovalAuthority.CapabilityHeader, review.Capability);
        return await Client.SendAsync(request);
    }

    public void Dispose() => Client.Dispose();
}
