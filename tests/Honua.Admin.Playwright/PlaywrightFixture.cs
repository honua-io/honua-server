// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Npgsql;

namespace Honua.Admin.Playwright;

public sealed class PlaywrightFixture : IAsyncLifetime
{
    private const string BaseUrlEnv = "HONUA_ADMIN_E2E_BASE_URL";
    private const string TestDbUrlEnv = "HONUA_ADMIN_E2E_DB_URL";
    private const string SchemaHeaderName = "X-Honua-Test-Schema";
    private const string SolutionFileName = "Honua.sln";
    private string? _baseUrl;
    private string? _testSchema;
    private string? _artifactsRoot;
    private string? _skipReason;
    private NpgsqlDataSource? _dataSource;

    public IBrowser Browser { get; private set; } = default!;
    public IPlaywright Playwright { get; private set; } = default!;
    public string? BaseUrl => _baseUrl;
    public string? TestSchema => _testSchema;

    public async Task InitializeAsync()
    {
        _baseUrl = NormalizeBaseUrl(Environment.GetEnvironmentVariable(BaseUrlEnv));
        _artifactsRoot = ResolveArtifactsRoot();
        Directory.CreateDirectory(_artifactsRoot);

        await InitializeDatabaseAsync();

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        try
        {
            Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
        }
        catch (PlaywrightException ex)
        {
            Playwright.Dispose();
            _skipReason = BuildPlaywrightSkipMessage(ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser != null)
        {
            await Browser.CloseAsync();
        }

        Playwright?.Dispose();
        await DisposeDatabaseAsync();
    }

    public async Task RunAsync(string testName, Func<PlaywrightTestContext, Task> testBody)
    {
        ArgumentNullException.ThrowIfNull(testBody);
        if (!string.IsNullOrWhiteSpace(_skipReason))
        {
            throw new InvalidOperationException(_skipReason);
        }

        var contextOptions = new BrowserNewContextOptions
        {
            BaseURL = string.IsNullOrWhiteSpace(_baseUrl) ? null : _baseUrl
        };

        if (!string.IsNullOrWhiteSpace(_testSchema))
        {
            contextOptions.ExtraHTTPHeaders = new Dictionary<string, string>
            {
                [SchemaHeaderName] = _testSchema
            };
        }

        await using var context = await Browser.NewContextAsync(contextOptions);
        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });
        var page = await context.NewPageAsync();

        var testContext = new PlaywrightTestContext(page, context, _baseUrl, _testSchema);

        try
        {
            await testBody(testContext);
            await context.Tracing.StopAsync();
        }
        catch (Exception ex)
        {
            await CaptureArtifactsAsync(context, page, testName, ex);
            throw;
        }
    }

    private async Task InitializeDatabaseAsync()
    {
        var dbUrl = Environment.GetEnvironmentVariable(TestDbUrlEnv);
        if (string.IsNullOrWhiteSpace(dbUrl))
        {
            return;
        }

        _dataSource = NpgsqlDataSource.Create(dbUrl);
        _testSchema = $"e2e_admin_{Guid.NewGuid():N}";

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA {_testSchema};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task DisposeDatabaseAsync()
    {
        if (_dataSource is null || string.IsNullOrWhiteSpace(_testSchema))
        {
            return;
        }

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS {_testSchema} CASCADE;";
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await _dataSource.DisposeAsync();
        }
    }

    private async Task CaptureArtifactsAsync(
        IBrowserContext context,
        IPage page,
        string testName,
        Exception exception)
    {
        if (string.IsNullOrWhiteSpace(_artifactsRoot))
        {
            return;
        }

        var safeName = SanitizeFileSegment(string.IsNullOrWhiteSpace(testName) ? "playwright-test" : testName);
        var testDir = Path.Combine(_artifactsRoot, safeName);
        Directory.CreateDirectory(testDir);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var screenshotPath = Path.Combine(testDir, $"failure_{timestamp}.png");
        var tracePath = Path.Combine(testDir, $"trace_{timestamp}.zip");
        var errorPath = Path.Combine(testDir, $"error_{timestamp}.txt");

        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true
            });
        }
        catch
        {
            // Ignore screenshot failures so the original test failure surfaces.
        }

        try
        {
            await File.WriteAllTextAsync(errorPath, exception.ToString());
        }
        catch
        {
            // Ignore error log failures so the original test failure surfaces.
        }

        try
        {
            await context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = tracePath
            });
        }
        catch
        {
            // Ignore trace failures so the original test failure surfaces.
        }
    }

    private static string? NormalizeBaseUrl(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ResolveArtifactsRoot()
    {
        var current = Directory.GetCurrentDirectory();
        var root = FindRepoRoot(current) ?? current;
        return Path.Combine(root, "tests", "TestResults", "playwright");
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string SanitizeFileSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];
        var index = 0;

        foreach (var ch in value)
        {
            buffer[index++] = Array.IndexOf(invalid, ch) >= 0 ? '-' : ch;
        }

        return new string(buffer, 0, index);
    }

    private static string BuildPlaywrightSkipMessage(PlaywrightException ex)
    {
        var baseMessage = "Playwright browser launch failed. Install the Playwright browsers";
        var installCommand = "pwsh tests/Honua.Admin.Playwright/bin/Debug/net10.0/playwright.ps1 install";
        var depsCommand = "pwsh tests/Honua.Admin.Playwright/bin/Debug/net10.0/playwright.ps1 install-deps";
        if (OperatingSystem.IsLinux())
        {
            return $"{baseMessage} (and Linux dependencies) and retry.\n{depsCommand}\n{installCommand}\nDetails: {ex.Message}";
        }

        return $"{baseMessage} and retry.\n{installCommand}\nDetails: {ex.Message}";
    }
}
