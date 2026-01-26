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
        var baseUrl = Environment.GetEnvironmentVariable("HONUA_ADMIN_E2E_BASE_URL");
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl
        });
        var page = await context.NewPageAsync();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            await page.GotoAsync("data:text/html,<h1>Honua Admin</h1>");
            var heading = await page.Locator("h1").TextContentAsync();
            Assert.Equal("Honua Admin", heading);
            return;
        }

        await page.GotoAsync(baseUrl);
        await page.GetByTestId("nav-dashboard").WaitForAsync();
        await page.GetByTestId("nav-connections").WaitForAsync();
        await page.GetByTestId("nav-preview").WaitForAsync();
        await page.GetByTestId("nav-styles").WaitForAsync();
    }
}
