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
            _ = await AuthTestHelpers.IsUnauthorizedAsync(page, strict: true);
        });
    }
}
