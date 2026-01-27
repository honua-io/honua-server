// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Admin.Playwright;

public sealed class ConnectionsPageTests : IClassFixture<PlaywrightFixture>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaywrightFixture _fixture;

    public ConnectionsPageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConnectionsPage_NewConnectionValidationShowsErrors()
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl
        });
        var page = await context.NewPageAsync();

        var now = DateTimeOffset.UtcNow;

        await page.RouteAsync("**/connections", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = Array.Empty<object>()
            });
        });

        await page.GotoAsync(BuildConnectionsUrl(baseUrl));

        await page.GetByTestId("connections-add").ClickAsync();
        await page.GetByTestId("connection-save").ClickAsync();

        await page.GetByText("Password is required.").WaitForAsync();
    }

    [Fact]
    public async Task ConnectionsPage_CreateEditDeleteConnection()
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl
        });
        var page = await context.NewPageAsync();

        var now = DateTimeOffset.UtcNow;
        var existingId = Guid.NewGuid();
        var createdId = Guid.NewGuid();

        var createdName = "analytics-db";
        var createdDescription = "Analytics warehouse";
        var createdHost = "analytics.internal";
        const int createdPort = 5432;
        var createdDatabase = "analytics";
        var createdUsername = "reporter";

        var updatedDescription = "Analytics warehouse - updated";
        var updatedHost = "analytics-updated.internal";

        var created = false;
        var deleted = false;

        await page.RouteAsync("**/connections", async route =>
        {
            if (!route.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                !route.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 405 });
                return;
            }

            if (route.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                created = true;
                await FulfillJsonAsync(route, new
                {
                    success = true,
                    message = "created",
                    timestamp = now,
                    data = BuildSummary(
                        createdId,
                        createdName,
                        createdDescription,
                        createdHost,
                        createdPort,
                        createdDatabase,
                        createdUsername,
                        healthStatus: "Unknown")
                });
                return;
            }

            var list = new List<object>
            {
                BuildSummary(
                    existingId,
                    "primary-db",
                    "Primary connection",
                    "db.internal",
                    5432,
                    "honua",
                    "admin",
                    healthStatus: "Healthy")
            };

            if (created && !deleted)
            {
                list.Add(BuildSummary(
                    createdId,
                    createdName,
                    createdDescription,
                    createdHost,
                    createdPort,
                    createdDatabase,
                    createdUsername,
                    healthStatus: "Unknown"));
            }

            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = list
            });
        });

        await page.RouteAsync("**/connections/*", async route =>
        {
            var connectionId = ExtractConnectionId(route.Request.Url);
            if (connectionId == Guid.Empty)
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
                return;
            }

            if (route.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var detail = connectionId == existingId
                    ? BuildDetail(
                        existingId,
                        "primary-db",
                        "Primary connection",
                        "db.internal",
                        5432,
                        "honua",
                        "admin",
                        now.AddHours(-2))
                    : BuildDetail(
                        createdId,
                        createdName,
                        createdDescription,
                        createdHost,
                        createdPort,
                        createdDatabase,
                        createdUsername,
                        now.AddMinutes(-5));

                await FulfillJsonAsync(route, new
                {
                    success = true,
                    message = "ok",
                    timestamp = now,
                    data = detail
                });
                return;
            }

            if (route.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                if (connectionId == createdId)
                {
                    createdDescription = updatedDescription;
                    createdHost = updatedHost;
                }

                await FulfillJsonAsync(route, new
                {
                    success = true,
                    message = "updated",
                    timestamp = now,
                    data = BuildSummary(
                        connectionId,
                        connectionId == createdId ? createdName : "primary-db",
                        connectionId == createdId ? createdDescription : "Primary connection",
                        connectionId == createdId ? createdHost : "db.internal",
                        connectionId == createdId ? createdPort : 5432,
                        connectionId == createdId ? createdDatabase : "honua",
                        connectionId == createdId ? createdUsername : "admin",
                        healthStatus: "Healthy")
                });
                return;
            }

            if (route.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                if (connectionId == createdId)
                {
                    deleted = true;
                }

                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 204
                });
                return;
            }

            await route.FulfillAsync(new RouteFulfillOptions { Status = 405 });
        });

        await page.GotoAsync(BuildConnectionsUrl(baseUrl));

        await page.GetByTestId("connections-add").ClickAsync();
        await page.GetByLabel("Connection name").FillAsync(createdName);
        await page.GetByLabel("Host").FillAsync(createdHost);
        await page.GetByLabel("Database").FillAsync(createdDatabase);
        await page.GetByLabel("Username").FillAsync(createdUsername);
        await page.GetByLabel("Password").FillAsync("super-secret");
        await page.GetByTestId("connection-save").ClickAsync();

        await WaitForTextAsync(page, createdName);

        await page.GetByTestId($"connection-edit-{createdId}").ClickAsync();
        await page.GetByLabel("Description").FillAsync(updatedDescription);
        await page.GetByLabel("Host").FillAsync(updatedHost);
        await page.GetByTestId("connection-save").ClickAsync();

        await WaitForTextAsync(page, updatedHost);

        await page.GetByTestId($"connection-delete-{createdId}").ClickAsync();
        var dialog = page.Locator("div[role='dialog']");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();

        await WaitForConditionAsync(async () =>
            await page.GetByTestId($"connection-edit-{createdId}").CountAsync() == 0,
            TimeSpan.FromSeconds(5),
            "Connection row did not disappear after delete.");
    }
    [Fact]
    public async Task ConnectionsPage_TestConnectionUpdatesStatus()
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl
        });
        var page = await context.NewPageAsync();

        var connectionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await page.RouteAsync("**/connections", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new[]
                {
                    new
                    {
                        connectionId,
                        name = "primary-db",
                        description = "Primary connection",
                        host = "db.internal",
                        port = 5432,
                        databaseName = "honua",
                        username = "admin",
                        sslRequired = true,
                        sslMode = "Require",
                        storageType = "managed",
                        isActive = true,
                        healthStatus = "Unknown",
                        lastHealthCheck = (DateTimeOffset?)null,
                        createdAt = now.AddDays(-1),
                        createdBy = "tester"
                    }
                }
            });
        });

        await page.RouteAsync("**/connections/*/test", async route =>
        {
            await FulfillJsonAsync(route, new
            {
                success = true,
                message = "ok",
                timestamp = now,
                data = new
                {
                    connectionId,
                    connectionName = "primary-db",
                    isHealthy = true,
                    testedAt = now,
                    message = "Connection healthy"
                }
            });
        });

        await page.GotoAsync(BuildConnectionsUrl(baseUrl));

        await page.GetByText("primary-db").WaitForAsync();
        await page.GetByTestId($"connection-test-{connectionId}").ClickAsync();

        await WaitForTextAsync(page, "Healthy");
    }

    private static string? GetBaseUrl()
        => Environment.GetEnvironmentVariable("HONUA_ADMIN_E2E_BASE_URL");

    private static string BuildConnectionsUrl(string baseUrl)
        => baseUrl.TrimEnd('/') + "/connections";

    private static async Task FulfillJsonAsync(IRoute route, object payload, int status = 200)
    {
        var body = JsonSerializer.Serialize(payload, _jsonOptions);
        await route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = body
        });
    }

    private static async Task WaitForTextAsync(IPage page, string expected, int timeoutMs = 10_000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.GetByText(expected).IsVisibleAsync())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for text '{expected}'.");
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

    private static Guid ExtractConnectionId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Guid.Empty;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("connections", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(segments[i + 1], out var connectionId))
            {
                return connectionId;
            }
        }

        return Guid.Empty;
    }

    private static object BuildSummary(
        Guid connectionId,
        string name,
        string? description,
        string host,
        int port,
        string databaseName,
        string username,
        string healthStatus)
        => new
        {
            connectionId,
            name,
            description,
            host,
            port,
            databaseName,
            username,
            sslRequired = true,
            sslMode = "Require",
            storageType = "managed",
            isActive = true,
            healthStatus,
            lastHealthCheck = (DateTimeOffset?)null,
            createdAt = DateTimeOffset.UtcNow.AddDays(-1),
            createdBy = "tester"
        };

    private static object BuildDetail(
        Guid connectionId,
        string name,
        string? description,
        string host,
        int port,
        string databaseName,
        string username,
        DateTimeOffset updatedAt)
        => new
        {
            connectionId,
            name,
            description,
            host,
            port,
            databaseName,
            username,
            sslRequired = true,
            sslMode = "Require",
            storageType = "managed",
            credentialReference = (string?)null,
            encryptionVersion = 1,
            isActive = true,
            healthStatus = "Healthy",
            lastHealthCheck = (DateTimeOffset?)null,
            createdAt = DateTimeOffset.UtcNow.AddDays(-1),
            updatedAt,
            createdBy = "tester"
        };
}
