namespace Honua.TestKit.Helpers;

// LicenseTestSupport also declares a web-host extension that these source-linked unit
// probes never call. Fail loudly if a probe accidentally starts relying on that seam.
public sealed class WebAppFixture
{
    public WebAppFixture ReplaceService<T>(T service) =>
        throw new NotSupportedException("This probe project does not host the server.");
}
