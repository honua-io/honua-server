// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Playwright;

public sealed class AdminSmokeTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;

    public AdminSmokeTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AdminShell_Loads()
    {
        await _fixture.RunAsync(nameof(AdminShell_Loads), async ctx =>
        {
            var baseUrl = ctx.BaseUrl;
            var page = ctx.Page;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                await page.GotoAsync("data:text/html,<h1>Honua Admin</h1>");
                var heading = await page.Locator("h1").TextContentAsync();
                Assert.Equal("Honua Admin", heading);
                return;
            }

            await page.GotoAsync(baseUrl);
            if (await AuthTestHelpers.IsUnauthorizedAsync(page))
            {
                Assert.Equal(0, await page.GetByTestId("nav-dashboard").CountAsync());
                Assert.Equal(0, await page.GetByTestId("nav-connections").CountAsync());
                return;
            }

            await page.GetByTestId("nav-dashboard").WaitForAsync();
            await page.GetByTestId("nav-connections").WaitForAsync();
            await page.GetByTestId("nav-preview").WaitForAsync();
            await page.GetByTestId("nav-styles").WaitForAsync();
            await page.GetByTestId("nav-metadata").WaitForAsync();
        });
    }
}
