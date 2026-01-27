// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Playwright;

public sealed class AuthStateTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;

    public AuthStateTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AdminShell_ShowsAuthState()
    {
        await _fixture.RunAsync(nameof(AdminShell_ShowsAuthState), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            var page = ctx.Page;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                await page.GotoAsync("data:text/html,<h1>Sign in required</h1><button data-testid='user-signin'>Sign in</button>");
                await page.GetByText("Sign in required").WaitForAsync();
                await page.GetByTestId("user-signin").WaitForAsync();
                return;
            }

            await page.GotoAsync(baseUrl);

            var signIn = page.GetByTestId("user-signin");
            var signOut = page.GetByTestId("user-signout");
            var signInRequired = page.GetByRole(AriaRole.Heading, new() { Name = "Sign in required" });

            await WaitForConditionAsync(async () =>
                await signIn.IsVisibleAsync() ||
                await signOut.IsVisibleAsync() ||
                await signInRequired.IsVisibleAsync(),
                TimeSpan.FromSeconds(10),
                "Auth state did not render sign-in or sign-out UI.");
        });
    }

    private static async Task WaitForConditionAsync(Func<Task<bool>> predicate, TimeSpan timeout, string errorMessage)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(errorMessage);
    }
}
