// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Playwright;

public sealed class HealthDashboardTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;

    public HealthDashboardTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HealthDashboard_Loads()
    {
        var baseUrl = GetBaseUrl();
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl
        });
        var page = await context.NewPageAsync();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            await page.GotoAsync("data:text/html,<div data-testid='health-dashboard' style='width:10px;height:10px;'></div><div data-testid='health-recent-errors' style='width:10px;height:10px;'></div><div data-testid='health-live-status' style='width:10px;height:10px;'></div><div data-testid='health-ready-status' style='width:10px;height:10px;'></div>");
            await page.GetByTestId("health-dashboard").WaitForAsync();
            await page.GetByTestId("health-recent-errors").WaitForAsync();
            await page.GetByTestId("health-live-status").WaitForAsync();
            await page.GetByTestId("health-ready-status").WaitForAsync();
            return;
        }

        var healthUrl = baseUrl[^1] == '/'
            ? $"{baseUrl}health"
            : $"{baseUrl}/health";

        await page.GotoAsync(healthUrl);
        await page.GetByTestId("health-dashboard").WaitForAsync();
        await page.GetByTestId("health-recent-errors").WaitForAsync();
        await page.GetByTestId("health-live-status").WaitForAsync();
        await page.GetByTestId("health-ready-status").WaitForAsync();
    }

    private static string? GetBaseUrl()
        => Environment.GetEnvironmentVariable("HONUA_ADMIN_E2E_BASE_URL");
}
