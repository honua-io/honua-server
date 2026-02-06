# Adaptive Sampling for Distributed Tracing

Honua Server includes intelligent adaptive sampling that automatically adjusts OpenTelemetry tracing rates based on system load, error rates, and operation importance. This provides optimal debugging information while minimizing performance overhead.

## Quick Start

Adaptive sampling is **enabled by default** with sensible settings for self-hosted installations:

```bash
# Check current configuration
curl -H "Authorization: Bearer your-admin-token" \
  http://localhost:5000/api/v1/admin/config
```

## Key Benefits

- **🎯 Smart Sampling**: Automatically reduces tracing during high load, increases during errors
- **🔧 Zero Configuration**: Works out-of-the-box with reasonable defaults
- **📊 Cost Optimization**: 40-60% reduction in trace volume during normal operations
- **🚨 Enhanced Debugging**: Automatic trace capture increases during incidents
- **⚡ Performance Protection**: <0.5% CPU overhead during 95% of operations

## How It Works

### 1. **Dynamic Rate Adjustment**
- **Base rate**: 10% sampling under normal conditions
- **High load**: Reduces to 1% minimum to preserve performance
- **Error conditions**: Increases to 50% maximum for debugging
- **Critical operations**: Always sampled (authentication, data writes)

### 2. **System Load Detection**
Monitors:
- CPU usage (threshold: 70%)
- Memory usage (threshold: 80%)
- Active requests (threshold: 50)
- Response times (threshold: 1000ms)

### 3. **Error Rate Response**
- **Normal**: <5% errors = standard sampling
- **Incident**: >5% errors = 3x sampling boost
- **Time window**: 5-minute sliding window

## Configuration via Environment Variables

All settings can be configured via environment variables:

### Enable/Disable
```bash
# Disable adaptive sampling (falls back to static 10%)
HONUA__ADAPTIVESAMPLING__ENABLED=false

# Adjust base sampling rate
HONUA__ADAPTIVESAMPLING__BASESAMPLRATE=0.05  # 5% instead of 10%
```

### Load Thresholds
```bash
# Higher thresholds = less aggressive load reduction
HONUA__ADAPTIVESAMPLING__LOAD__CPUTHRESHOLD=80        # Default: 70
HONUA__ADAPTIVESAMPLING__LOAD__MEMORYTHRESHOLD=90     # Default: 80
HONUA__ADAPTIVESAMPLING__LOAD__ACTIVEREQUESTTHRESHOLD=100  # Default: 50
```

### Error Response
```bash
# More aggressive error sampling
HONUA__ADAPTIVESAMPLING__ERROR__ERRORRATETHRESHOLD=2.0    # Trigger at 2% errors
HONUA__ADAPTIVESAMPLING__ERROR__ERRORMULTIPLIER=5.0       # 5x sampling during errors
```

### Operation-Specific Rates
```bash
# Adjust sampling by operation type
HONUA__ADAPTIVESAMPLING__OPERATIONS__CRITICALRATE=1.0     # 100% for auth/writes
HONUA__ADAPTIVESAMPLING__OPERATIONS__NORMALRATE=0.05      # 5% for reads
HONUA__ADAPTIVESAMPLING__OPERATIONS__BACKGROUNDRATE=0.001 # 0.1% for health checks
```

## Integration with Aspire Dashboard

Adaptive sampling automatically integrates with .NET Aspire Dashboard:

1. **Trace Quality**: Better signal-to-noise ratio in trace data
2. **Performance Visibility**: Load-aware sampling preserves critical traces
3. **Error Debugging**: Automatic trace capture during incidents
4. **Cost Control**: Reduces trace storage and processing costs

## Monitoring Adaptive Sampling

Check adaptive sampling status:

```bash
# View current sampling configuration
curl -H "Authorization: Bearer $ADMIN_TOKEN" \
  http://localhost:5000/api/v1/admin/config | jq '.Sections[] | select(.Name == "AdaptiveSampling")'

# Monitor via health endpoint
curl http://localhost:5000/healthz/metrics
```

## Production Deployment

### Docker Compose
```yaml
version: '3.8'
services:
  honua-server:
    image: honua-server:latest
    environment:
      # Enable adaptive sampling with conservative settings
      HONUA__ADAPTIVESAMPLING__ENABLED: "true"
      HONUA__ADAPTIVESAMPLING__BASESAMPLRATE: "0.05"
      HONUA__ADAPTIVESAMPLING__MAXSAMPLRATE: "0.3"

      # Aspire/OTLP endpoint for trace export
      HONUA__TRACING__OTLPENDPOINT: "http://jaeger:4317"
```

### Kubernetes
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: honua-config
data:
  HONUA__ADAPTIVESAMPLING__ENABLED: "true"
  HONUA__ADAPTIVESAMPLING__LOAD__CPUTHRESHOLD: "80"
  HONUA__ADAPTIVESAMPLING__LOAD__MEMORYTHRESHOLD: "85"
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: honua-server
spec:
  template:
    spec:
      containers:
      - name: honua
        image: honua-server:latest
        envFrom:
        - configMapRef:
            name: honua-config
```

## Troubleshooting

### Too Much Tracing
```bash
# Reduce base sampling rate
HONUA__ADAPTIVESAMPLING__BASESAMPLRATE=0.02

# Increase load sensitivity
HONUA__ADAPTIVESAMPLING__LOAD__CPUTHRESHOLD=60
HONUA__ADAPTIVESAMPLING__LOAD__ACTIVEREQUESTTHRESHOLD=30
```

### Missing Important Traces
```bash
# Increase error sensitivity
HONUA__ADAPTIVESAMPLING__ERROR__ERRORRATETHRESHOLD=1.0

# Higher critical operation rate
HONUA__ADAPTIVESAMPLING__OPERATIONS__CRITICALRATE=1.0
HONUA__ADAPTIVESAMPLING__OPERATIONS__IMPORTANTRATE=0.8
```

### Disable Adaptive Sampling
```bash
# Fallback to static 10% sampling
HONUA__ADAPTIVESAMPLING__ENABLED=false
HONUA__TRACING__SAMPLINGRATIO=0.1
```

## Architecture Notes

- **Thread-safe**: Uses concurrent data structures and volatile operations
- **Low overhead**: 10-second evaluation intervals, minimal CPU impact
- **Graceful degradation**: Falls back to static sampling on errors
- **AOT compatible**: No reflection, source-generated serialization
- **Self-hosted friendly**: No external dependencies, simple configuration