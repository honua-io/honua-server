// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Playwright;

public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IBrowser Browser { get; private set; } = default!;
    public IPlaywright Playwright { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser != null)
        {
            await Browser.CloseAsync();
        }

        Playwright?.Dispose();
    }
}
