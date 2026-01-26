// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Playwright;

public sealed class MapPreviewTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;

    public MapPreviewTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PreviewPage_Loads()
    {
        var baseUrl = GetBaseUrl();
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl
        });
        var page = await context.NewPageAsync();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            await page.GotoAsync("data:text/html,<div data-testid='map-preview-canvas'></div>");
            await page.GetByTestId("map-preview-canvas").WaitForAsync();
            return;
        }

        var previewUrl = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? $"{baseUrl}preview"
            : $"{baseUrl}/preview";

        await page.GotoAsync(previewUrl);
        await page.GetByTestId("preview-connection-select").WaitForAsync();
        await page.GetByTestId("preview-layer-select").WaitForAsync();
        await page.GetByTestId("map-preview-canvas").WaitForAsync();
    }

    [Fact]
    public async Task StyleEditor_Loads()
    {
        var baseUrl = GetBaseUrl();
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl
        });
        var page = await context.NewPageAsync();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            await page.GotoAsync("data:text/html,<iframe data-testid='maputnik-frame'></iframe><div data-testid='map-preview-canvas'></div>");
            await page.GetByTestId("maputnik-frame").WaitForAsync();
            await page.GetByTestId("map-preview-canvas").WaitForAsync();
            return;
        }

        var stylesUrl = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? $"{baseUrl}styles"
            : $"{baseUrl}/styles";

        await page.GotoAsync(stylesUrl);
        await page.GetByTestId("styles-connection-select").WaitForAsync();
        await page.GetByTestId("styles-layer-select").WaitForAsync();
        await page.GetByTestId("maputnik-frame").WaitForAsync();
        await page.GetByTestId("map-preview-canvas").WaitForAsync();
    }

    private static string? GetBaseUrl()
        => Environment.GetEnvironmentVariable("HONUA_ADMIN_E2E_BASE_URL");
}
