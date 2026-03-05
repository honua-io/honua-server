// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Playwright;

internal static class AuthTestHelpers
{
    private static readonly TimeSpan _authUiRenderTimeout = TimeSpan.FromSeconds(45);

    public static async Task<bool> IsUnauthorizedAsync(IPage page, bool strict = true)
    {
        var signIn = page.GetByTestId("user-signin");
        var signOut = page.GetByTestId("user-signout");
        var userMenu = page.GetByTestId("user-menu");
        var signInRequired = page.GetByRole(AriaRole.Heading, new() { Name = "Sign in required" });

        var authUiReady = await WaitForConditionAsync(async () =>
            await signIn.IsVisibleAsync() ||
            await userMenu.IsVisibleAsync() ||
            await signOut.IsVisibleAsync() ||
            await signInRequired.IsVisibleAsync(),
            _authUiRenderTimeout);

        // A one-time reload smooths over occasional startup races in CI where auth
        // controls render late after initial WASM boot.
        if (!authUiReady)
        {
            try
            {
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            catch
            {
                // Ignore reload failures and fall through to the strict/lenient handling below.
            }

            authUiReady = await WaitForConditionAsync(async () =>
                await signIn.IsVisibleAsync() ||
                await userMenu.IsVisibleAsync() ||
                await signOut.IsVisibleAsync() ||
                await signInRequired.IsVisibleAsync(),
                TimeSpan.FromSeconds(20));
        }

        if (!authUiReady)
        {
            if (strict)
            {
                throw new TimeoutException("Auth state did not render sign-in or sign-out UI.");
            }

            return false;
        }

        return await signInRequired.IsVisibleAsync() || await signIn.IsVisibleAsync();
    }

    private static async Task<bool> WaitForConditionAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }
}
