# CLAUDE.md Architecture Guidance Improvements

## Current Issues with Architecture Review Guidance

### Problem: Insufficient Guidance for Effective Architecture Review
The current "Critical Rules" section in CLAUDE.md lacks specificity needed for consistent architecture enforcement.

## Proposed Enhanced Architecture Section

Replace the current brief "Architecture" subsection with this comprehensive guidance:

```markdown
### Architecture Enforcement

#### 🚫 BLOCKING VIOLATIONS (Must Fix Before Merge)

**1. Dependency Direction Violations**
```csharp
// ❌ VIOLATION: Core depending on Infrastructure
// File: src/Honua.Core/SomeFile.cs
using Honua.Postgres;        // BLOCKING - Core cannot depend on Infrastructure
using Honua.Server;          // BLOCKING - Core cannot depend on Server

// ✅ CORRECT: Infrastructure depending on Core
// File: src/Honua.Postgres/SomeFile.cs
using Honua.Core.Features.Abstractions;  // OK - Infrastructure can use Core abstractions
```

**Dependency Flow Rule**: `Honua.Core` ← `Honua.Postgres` ← `Honua.Server`
- Core defines abstractions and domain models
- Postgres implements Core interfaces
- Server uses both Core and Postgres

**2. API Pattern Violations**
```csharp
// ❌ VIOLATION: Controller usage (legacy pattern)
public class FeaturesController : ControllerBase  // BLOCKING - No controllers allowed
{
    // Controllers create 22-dependency anti-pattern
}

// ✅ CORRECT: Minimal API pattern
// File: src/Honua.Server/Features/FeatureServer/FeatureServerEndpoints.cs
public static void MapFeatureServerEndpoints(this WebApplication app)
{
    app.MapGet("/rest/services/{id}/FeatureServer/{layerId}/query",
        async (int id, int layerId, IFeatureStore store) => { });
}
```

**3. Encapsulation Violations**
```csharp
// ❌ VIOLATION: Public infrastructure types (security risk)
public class FeatureRepository { }        // BLOCKING - Should be internal
public class PostgresConnection { }       // BLOCKING - Should be internal

// ✅ CORRECT: Proper encapsulation
internal class FeatureRepository { }      // OK - Implementation details are internal
public interface IFeatureStore { }        // OK - Abstractions can be public
```

**4. Missing Documentation**
```csharp
// ❌ VIOLATION: Public type without XML docs
public class LayerDefinition  // BLOCKING - Missing /// documentation
{
}

// ✅ CORRECT: Documented public API
/// <summary>
/// Represents a geospatial layer definition with metadata and spatial reference.
/// </summary>
public class LayerDefinition
{
}
```

#### ⚠️ WARNING VIOLATIONS (Review Recommended)

**1. Organizational Anti-Patterns**
```csharp
// ⚠️ WARNING: Layer-based organization (should be vertical slices)
src/
├── Controllers/           // Layer-based anti-pattern
├── Services/
├── Models/
└── Repositories/

// ✅ PREFERRED: Vertical slice organization
src/Honua.Server/Features/
├── FeatureServer/         // Feature-based organization
│   ├── FeatureServerEndpoints.cs
│   ├── FeatureServerHandler.cs
│   └── Models/
└── Admin/
    ├── AdminEndpoints.cs
    └── Services/
```

**2. Complexity Violations**
```csharp
// ⚠️ WARNING: Too many dependencies (endpoint limit: 5, handler limit: 4)
public class QueryHandler(
    IFeatureStore store,        // 1
    ILayerCatalog catalog,      // 2
    ILogger<QueryHandler> log,  // 3
    IValidator validator,       // 4
    IMetrics metrics,          // 5 - At limit, consider refactoring if adding more
    IEventBus events)          // 6 - WARNING: Exceeds limit
```

**3. Performance Anti-Patterns**
```csharp
// ⚠️ WARNING: Sync-over-async (performance issue)
var result = asyncOperation.Result;      // Use await instead
asyncOperation.Wait();                   // Use await instead

// ⚠️ WARNING: Deep inheritance (composition preferred)
class A : B : C : D { }  // WARNING: >3 levels, consider composition
```

#### ✅ POSITIVE PATTERNS TO REINFORCE

**1. Clean Dependency Flow**
```csharp
// ✅ GOOD: Proper dependency direction
// Honua.Core defines interface
public interface IFeatureStore { }

// Honua.Postgres implements interface
internal class PostgresFeatureStore : IFeatureStore { }

// Honua.Server uses interface
public static async Task<IResult> QueryFeatures(IFeatureStore store) { }
```

**2. Vertical Slice Organization**
```csharp
// ✅ GOOD: Feature cohesion
Features/FeatureServer/
├── FeatureServerEndpoints.cs    // API endpoints
├── FeatureServerHandler.cs      // Business logic
├── FeatureServerModels.cs       // DTOs
└── Services/                    // Supporting services
    └── GeometryConverter.cs
```

**3. Proper Testing Structure**
```csharp
// ✅ GOOD: Comprehensive test coverage with proper attributes
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class QueryEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures()
    {
        // Test implementation
    }
}
```

#### Architecture Review Checklist

**For AI Reviews - Check These Patterns:**
1. **Scan `using` statements** for dependency direction violations
2. **Look for `ControllerBase`** inheritance (forbidden pattern)
3. **Check public class declarations** in Infrastructure projects (should be internal)
4. **Verify XML documentation** on all public types (`///` comments)
5. **Count constructor parameters** (endpoints ≤5, handlers ≤4)
6. **Search for `.Result` or `.Wait()`** (sync-over-async anti-pattern)
7. **Verify file organization** follows vertical slice pattern

**Severity Assessment:**
- **BLOCKING**: Dependency violations, Controller usage, Public infrastructure types, Missing docs
- **WARNING**: Organizational issues, Complexity violations, Performance anti-patterns
- **APPROVED**: Clean dependencies, Vertical slices, Proper testing, Good documentation
```

## Implementation Strategy

### 1. Replace Current Architecture Section
Replace the 3-bullet architecture section in CLAUDE.md with the comprehensive guidance above.

### 2. Update Architecture Review Scripts
Both Claude and OpenAI scripts will automatically inherit the improved guidance since they read from CLAUDE.md.

### 3. Add Specific Detection Patterns
Enhance the local Claude script to detect these specific patterns:

```python
# Enhanced pattern detection
def detect_dependency_violations(file_path: str, content: str) -> List[str]:
    violations = []

    # Check specific dependency direction rules
    if "Honua.Core" in file_path:
        if "using Honua.Postgres" in content or "using Honua.Server" in content:
            violations.append("BLOCKING: Core layer depending on Infrastructure")

    # Check for controller usage
    if ": ControllerBase" in content or "public class" in content and "Controller" in content:
        violations.append("BLOCKING: Controller usage detected - use Minimal APIs")

    # Check for public infrastructure types
    if "Honua.Postgres" in file_path or "Repository" in file_path:
        if "public class" in content and not "interface" in content:
            violations.append("BLOCKING: Public infrastructure type - should be internal")

    return violations
```

### 4. Verification
Test the improved guidance:
- Run architecture reviews on existing code
- Verify proper violation detection
- Confirm severity classification works correctly

## Expected Benefits

1. **Consistent Reviews**: Clear, specific rules eliminate ambiguity
2. **Better Detection**: Concrete patterns enable accurate violation identification
3. **Proper Prioritization**: Blocking vs warning classification guides action
4. **Educational Value**: Examples teach correct patterns while identifying issues
5. **Maintainable**: Single source of truth automatically updates all review systems

This enhanced guidance transforms abstract architectural principles into actionable, enforceable rules.