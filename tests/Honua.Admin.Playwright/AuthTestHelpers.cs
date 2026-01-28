// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Playwright;

internal static class AuthTestHelpers
{
    public static async Task<bool> IsUnauthorizedAsync(IPage page)
    {
        var signIn = page.GetByTestId("user-signin");
        var signOut = page.GetByTestId("user-signout");
        var userMenu = page.GetByTestId("user-menu");
        var signInRequired = page.GetByRole(AriaRole.Heading, new() { Name = "Sign in required" });

        await WaitForConditionAsync(async () =>
            await signIn.IsVisibleAsync() ||
            await userMenu.IsVisibleAsync() ||
            await signOut.IsVisibleAsync() ||
            await signInRequired.IsVisibleAsync(),
            TimeSpan.FromSeconds(20),
            "Auth state did not render sign-in or sign-out UI.");

        return await signInRequired.IsVisibleAsync() || await signIn.IsVisibleAsync();
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
