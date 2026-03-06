# Updated Repository Structure

## 🎯 **Clean Repository Naming**

### **Before (Verbose)**
- `geospatial-grpc-standard` ❌
- `honua-shared` ❌

### **After (Clean)**
- **`geospatial-grpc`** ✅
- **`honua-core`** ✅

## 📁 **Complete Repository Family**

### **Protocol & Standards**
**Repository**: `geospatial-grpc`
**URL**: https://github.com/mikemcdougall/geospatial-grpc
**License**: Apache 2.0
**Purpose**: gRPC protocol definitions for geospatial services

```
geospatial-grpc/
├── geospatial/v1/
│   ├── feature_service.proto      # Feature CRUD operations
│   ├── form_service.proto         # Mobile data collection
│   └── map_service.proto          # Map rendering (planned)
├── buf.yaml                       # buf.build/geospatial/standard
├── buf.gen.yaml                   # Multi-language code generation
└── docs/
    ├── specification.md           # Full protocol specification
    └── getting-started.md         # Developer quick start
```

### **Foundation Library**
**Repository**: `honua-core`
**URL**: https://github.com/mikemcdougall/honua-core
**License**: Apache 2.0
**NuGet**: `Honua.Core`
**Purpose**: .NET foundation library for Honua applications

```
honua-core/
├── src/Honua.Core/
│   ├── Models/                    # FeatureQuery, SpatialFilter, etc.
│   ├── Converters/                # gRPC conversion helpers
│   ├── Services/                  # Service interfaces
│   └── Extensions/                # Extension methods
├── tests/Honua.Core.Tests/
└── docs/
    ├── api-reference.md
    └── migration-guide.md
```

### **Server Implementation**
**Repository**: `honua-server`
**URL**: https://github.com/makamekm/honua-server
**License**: ELv2 (Elastic License v2)
**Purpose**: Honua geospatial server

### **Client SDKs (Future)**
**Repository**: `honua-mobile-sdk`
**License**: Apache 2.0
**NuGet**: `Honua.Mobile.Sdk`
**Purpose**: .NET MAUI mobile SDK

**Repository**: `honua-js-sdk`
**License**: Apache 2.0
**NPM**: `@honua/sdk`
**Purpose**: JavaScript/TypeScript SDK

## 🔗 **Updated Dependencies**

### **buf.build Registry**
```yaml
# Protocol definition registry (stays descriptive)
name: buf.build/geospatial/standard
repository: https://github.com/mikemcdougall/geospatial-grpc
```

### **Consumer Dependencies**

**honua-server:**
```xml
<PackageReference Include="Honua.Core" Version="1.0.0" />
```

**honua-mobile-sdk:**
```xml
<PackageReference Include="Honua.Core" Version="1.0.0" />
```

**Any .NET application:**
```bash
dotnet add package Honua.Core
```

### **Protocol Dependencies**
```yaml
# honua-core/buf.yaml
deps:
  - buf.build/geospatial/standard:v0.2.0

# honua-server/buf.yaml
deps:
  - buf.build/geospatial/standard:v0.2.0

# honua-mobile-sdk/buf.yaml
deps:
  - buf.build/geospatial/standard:v0.2.0
```

## 🚀 **Updated Usage Examples**

### **Installing Honua.Core**
```bash
# Add to any .NET project
dotnet add package Honua.Core

# Available immediately
using Honua.Core.Models;
using Honua.Core.Converters;
```

### **Referencing geospatial-grpc Protocol**
```bash
# Clone protocol definitions
git clone https://github.com/mikemcdougall/geospatial-grpc.git

# Generate code for any language
buf generate
```

## 📋 **Benefits of Clean Naming**

### **`geospatial-grpc` (was geospatial-grpc-standard)**
- ✅ **Shorter URLs**: easier to share and remember
- ✅ **Industry standard**: follows protobuf, grpc, openapi patterns
- ✅ **Professional**: less verbose, more elegant
- ✅ **Clear purpose**: obviously contains protocol definitions

### **`honua-core` (was honua-shared)**
- ✅ **Suggests foundation**: core building blocks
- ✅ **Industry alignment**: matches .NET Core pattern
- ✅ **Clear branding**: foundational Honua functionality
- ✅ **Future-proof**: room for Honua.Mobile, Honua.Server.Extensions

## 🔄 **Migration Actions**

### **Repository Renames**
1. Rename `geospatial-grpc-standard` → `geospatial-grpc`
2. Update all README.md files with new repository URLs
3. Update buf.yaml repository references
4. Update documentation links

### **Package Updates**
1. Publish `Honua.Core` package (was Honua.Shared)
2. Update namespace from `Honua.Shared.*` to `Honua.Core.*`
3. Update all consumer project references

### **Documentation Updates**
1. Update getting-started guides with new names
2. Update API documentation references
3. Update example code snippets
4. Update tutorial links

## 🎯 **Final Repository Structure**

**Clean, professional naming that follows industry standards:**

```
Open Source Ecosystem:
├── geospatial-grpc/          # Protocol definitions
├── honua-core/               # .NET foundation library
├── honua-mobile-sdk/         # MAUI mobile SDK (future)
└── honua-js-sdk/             # JavaScript SDK (future)

Commercial Implementation:
└── honua-server/             # Server implementation (ELv2)
```

This creates a **clear separation between the open ecosystem and commercial implementation** while maintaining professional, memorable naming throughout! 🚀