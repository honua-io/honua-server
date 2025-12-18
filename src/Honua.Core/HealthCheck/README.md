# Health Check Architecture

## Clean Architecture Compliance

This module follows Clean Architecture dependency rules:

```
┌─────────────────────┐
│   Honua.Server      │  ← Composition Root (DI Registration)
│   (Application)     │
└─────────┬───────────┘
          │ depends on
          ▼
┌─────────────────────┐
│   Honua.Core        │  ← Domain Abstractions (IDatabaseHealthChecker)
│   (Domain)          │
└─────────┬───────────┘
          ▲
          │ implements
          │
┌─────────────────────┐
│   Honua.Postgres    │  ← Infrastructure Implementation
│   (Infrastructure)  │     (PostgresDatabaseHealthChecker)
└─────────────────────┘
```

## Dependency Direction (✅ Correct)

1. **Core defines abstractions** - `IDatabaseHealthChecker` interface
2. **Infrastructure implements** - `PostgresDatabaseHealthChecker : IDatabaseHealthChecker`
3. **Application composes** - `Program.cs` registers `IDatabaseHealthChecker → PostgresDatabaseHealthChecker`

**No dependency violations**: Core has zero project references, Infrastructure depends only on Core.

## Files

- `IDatabaseHealthChecker.cs` - Core abstraction (Domain layer)
- `../Honua.Postgres/HealthCheck/PostgresDatabaseHealthChecker.cs` - Implementation (Infrastructure)
- `../Honua.Server/Endpoints/HealthEndpoints.cs` - Endpoints (Application)
- `../Honua.Server/Program.cs` - DI registration (Composition root)