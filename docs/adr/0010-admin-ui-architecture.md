# ADR-0010: Admin UI Architecture (Blazor WASM)

## Status

Accepted

## Context

The Admin UI needs architectural decisions around:
1. **Component library** - Build custom vs use existing (MudBlazor, Radzen, etc.)
2. **Hosting model** - Integrated with Server vs standalone static hosting
3. **State management** - How to manage client-side state
4. **Testing** - How to test UI interactions
5. **Map integration** - How to embed MapLibre

## Decision

### Component Library: MudBlazor

**Choice:** Use [MudBlazor](https://mudblazor.com/) as the primary UI component library.

| Option | Pros | Cons |
|--------|------|------|
| **MudBlazor** ✅ | Material Design, large component set, active maintenance, good docs | ~500KB additional WASM size |
| Radzen | Free components, code gen | Heavier, less community |
| Fluent UI Blazor | Microsoft supported | Smaller component set |
| Custom | Full control, minimal size | Significant dev time, inconsistent UX |

**Rationale:**
- MudBlazor provides tables, forms, dialogs, navigation - everything Admin UI needs
- Material Design gives professional appearance with minimal design effort
- Well-suited for data-heavy admin interfaces
- Active community, good Blazor WASM support

### Hosting Model: Dual Support

Support both integrated and standalone hosting:

```
Option A: Integrated (Default for dev/simple deployments)
┌─────────────────────────────────────────┐
│           Honua.Server                   │
│  ├── /rest/services/...  (API)          │
│  ├── /ogc/features/...   (API)          │
│  ├── /odata/v4/...       (API)          │
│  └── /admin/...          (Blazor WASM)  │
└─────────────────────────────────────────┘

Option B: Standalone (Production/CDN)
┌─────────────────┐     ┌─────────────────┐
│  S3/CloudFront  │     │  Honua.Server   │
│  /admin/*       │────▶│  /api/v1/admin/*   │
│  (Static WASM)  │     │  (API only)     │
└─────────────────┘     └─────────────────┘
```

**Configuration:**
```csharp
// Program.cs
if (builder.Configuration.GetValue<bool>("ServeAdminUI", true))
{
    // Integrated: serve WASM from /admin
    app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
}
// Standalone: Admin UI hosted separately, only API endpoints served
```

**Environment variables:**
```bash
# Integrated (default)
HONUA_SERVE_ADMIN_UI=true

# Standalone (API only, WASM hosted elsewhere)
HONUA_SERVE_ADMIN_UI=false
HONUA_ADMIN_UI_CORS_ORIGINS=https://admin.example.com
```

### State Management: Fluxor (Optional) or Simple Services

For MVP, use **simple injectable services** with `INotifyPropertyChanged`:

```csharp
// Services/AppState.cs
public class AppState : INotifyPropertyChanged
{
    private ConnectionInfo? _selectedConnection;

    public ConnectionInfo? SelectedConnection
    {
        get => _selectedConnection;
        set { _selectedConnection = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Registration
builder.Services.AddSingleton<AppState>();
```

**Upgrade path:** If state complexity grows, migrate to [Fluxor](https://github.com/mrpmorris/Fluxor) (Redux pattern for Blazor).

### Testing: Playwright + bUnit

**Unit Tests (bUnit):**
```csharp
// Test component rendering and interactions
[Fact]
public void ConnectionForm_ValidInput_EnablesSubmit()
{
    using var ctx = new TestContext();
    var cut = ctx.RenderComponent<ConnectionForm>();

    cut.Find("input[name=host]").Change("localhost");
    cut.Find("input[name=database]").Change("honua");

    cut.Find("button[type=submit]").GetAttribute("disabled").Should().BeNull();
}
```

**Integration Tests (Playwright):**
```csharp
// End-to-end UI flows
[Fact]
public async Task Admin_CanPublishLayerFromTable()
{
    await Page.GotoAsync("/admin");
    await Page.ClickAsync("text=Connections");
    await Page.ClickAsync("text=Add Connection");

    await Page.FillAsync("[name=host]", "localhost");
    await Page.FillAsync("[name=database]", "honua_test");
    await Page.ClickAsync("text=Test Connection");

    await Expect(Page.Locator("text=Connection successful")).ToBeVisibleAsync();

    await Page.ClickAsync("text=Save");
    await Page.ClickAsync("text=Browse Tables");
    await Page.ClickAsync("tr:has-text('parcels')");
    await Page.ClickAsync("text=Publish as Layer");

    await Expect(Page.Locator("text=Layer published")).ToBeVisibleAsync();
}
```

**Test project structure:**
```
tests/
├── Honua.Admin.Tests/           # bUnit component tests
│   ├── Components/
│   │   ├── ConnectionFormTests.cs
│   │   └── LayerListTests.cs
│   └── Pages/
│       └── DashboardTests.cs
│
└── Honua.Admin.Playwright/      # Playwright E2E tests
    ├── AdminFlowTests.cs
    ├── ImportWizardTests.cs
    └── PlaywrightFixture.cs
```

### Map Integration: MapLibre GL JS via JS Interop

```csharp
// Components/MapView.razor
@inject IJSRuntime JS

<div id="map-container" style="height: 500px;"></div>

@code {
    [Parameter] public string? LayerId { get; set; }
    [Parameter] public string? StyleUrl { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JS.InvokeVoidAsync("initMapLibre", "map-container", new
            {
                style = StyleUrl ?? "/api/styles/default.json",
                sources = new[] { new { id = LayerId, type = "vector", url = $"/tiles/{LayerId}/tile.json" } }
            });
        }
    }
}
```

```javascript
// wwwroot/js/maplibre-interop.js
window.initMapLibre = (containerId, options) => {
    const map = new maplibregl.Map({
        container: containerId,
        style: options.style,
        center: [0, 0],
        zoom: 2
    });

    options.sources?.forEach(source => {
        map.on('load', () => {
            map.addSource(source.id, { type: source.type, url: source.url });
        });
    });

    return map;
};
```

## Project Structure

```
src/Honua.Admin/
├── wwwroot/
│   ├── index.html
│   ├── css/
│   └── js/
│       └── maplibre-interop.js
│
├── Layout/
│   ├── MainLayout.razor
│   └── NavMenu.razor
│
├── Pages/
│   ├── Dashboard.razor
│   ├── Connections/
│   │   ├── Index.razor
│   │   └── Edit.razor
│   ├── Layers/
│   │   ├── Index.razor
│   │   ├── Publish.razor
│   │   └── Configure.razor
│   ├── Import/
│   │   └── Wizard.razor
│   └── Preview/
│       └── Index.razor
│
├── Components/
│   ├── ConnectionForm.razor
│   ├── TableSelector.razor
│   ├── FileUploader.razor
│   ├── MapView.razor
│   └── StyleEditor.razor
│
├── Services/
│   ├── HonuaApiClient.cs
│   ├── AppState.cs
│   └── AuthStateProvider.cs
│
└── Program.cs
```

## Package References

```xml
<!-- Honua.Admin.csproj -->
<ItemGroup>
  <!-- UI Components -->
  <PackageReference Include="MudBlazor" Version="7.*" />

  <!-- HTTP Client -->
  <PackageReference Include="Microsoft.Extensions.Http" Version="9.*" />

  <!-- Auth -->
  <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Authentication" Version="9.*" />
</ItemGroup>

<!-- Honua.Admin.Tests.csproj -->
<ItemGroup>
  <PackageReference Include="bunit" Version="1.*" />
  <PackageReference Include="MudBlazor" Version="7.*" />
</ItemGroup>

<!-- Honua.Admin.Playwright.csproj -->
<ItemGroup>
  <PackageReference Include="Microsoft.Playwright" Version="1.*" />
  <PackageReference Include="Microsoft.Playwright.NUnit" Version="1.*" />
</ItemGroup>
```

## Consequences

### Benefits
- **MudBlazor** provides professional UI with minimal custom CSS
- **Dual hosting** supports both simple and enterprise deployments
- **Playwright** ensures UI regressions are caught
- **bUnit** enables fast component-level testing

### Trade-offs
- **WASM size** increases ~500KB with MudBlazor (mitigated by CDN/caching)
- **JS Interop** for MapLibre adds complexity (unavoidable for WebGL maps)
- **Two test frameworks** (bUnit + Playwright) to maintain

### Standalone Hosting Benefits
When hosted on S3/CloudFront:
- Reduces Honua.Server container size
- Better global CDN distribution for WASM
- Independent scaling of UI and API
- Simpler server container (API only)

## References

- MudBlazor: https://mudblazor.com/
- Playwright for .NET: https://playwright.dev/dotnet/
- bUnit: https://bunit.dev/
- MapLibre GL JS: https://maplibre.org/maplibre-gl-js/docs/
