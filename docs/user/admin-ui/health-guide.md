# Health Monitoring Guide

The Health Dashboard provides real-time monitoring of your Honua Server instance, including system performance, service health, database connections, and operational metrics. Use it to ensure optimal performance and quickly identify issues.

## 📋 **Prerequisites**

- Honua Server running with monitoring enabled
- Admin access to view health metrics
- Optional: External monitoring integrations (Prometheus, etc.)

## 🎯 **Monitoring Overview**

### **Health Categories:**
- **System Health**: CPU, memory, and disk usage
- **Service Health**: API endpoints and service availability
- **Database Health**: Connection pools and query performance
- **Layer Health**: Published layer status and performance
- **Cache Health**: Response cache hit rates and efficiency

---

## **Step 1: Access Health Dashboard**

Open the monitoring interface:

1. Navigate to Admin UI at `/admin`
2. Click **🏠 Dashboard** in the sidebar (default landing page)
3. Health overview loads with real-time metrics

*🖼️ Screenshot needed: Health dashboard overview with status cards*

---

## **Step 2: System Health Overview**

### **Health Status Cards:**

*🖼️ Screenshot needed: Status cards showing green/yellow/red health indicators*

**Overall System Status:**
- **🟢 Healthy**: All systems operating normally
- **🟡 Warning**: Some performance degradation or warnings
- **🔴 Critical**: Service failures or resource exhaustion

**Quick Metrics:**
- **Uptime**: Time since last restart
- **Active Connections**: Current client connections
- **Request Rate**: Requests per minute
- **Error Rate**: Percentage of failed requests
- **Response Time**: Average API response time

### **System Resource Monitoring:**

**CPU Usage:**
- Current CPU utilization percentage
- Historical CPU trends (last hour/day)
- Peak usage alerts and thresholds

**Memory Usage:**
- Total and available memory
- Memory consumption by component
- Garbage collection metrics (for .NET runtime)

**Disk Usage:**
- Available disk space
- Database file sizes
- Log file growth monitoring

*🖼️ Screenshot needed: System resource charts showing trends over time*

---

## **Step 3: Service Health Details**

### **API Endpoint Health**

Monitor individual API service health:

**Service Status:**
- **FeatureServer**: Esri-compatible REST API health
- **OGC Features**: OGC API Features endpoint health
- **OData**: OData v4 service health
- **Vector Tiles**: MVT tile service health
- **Control Plane**: Admin API health

*🖼️ Screenshot needed: Service health grid with individual endpoint status*

**Health Checks:**
- **HTTP Response**: Service responds to health check requests
- **Database Connection**: Can connect to and query database
- **Cache Availability**: Response cache is functioning
- **Authentication**: Security providers are working

### **Performance Metrics:**

**Response Time Monitoring:**
- Average response times per service
- 95th percentile response times
- Slowest endpoints identification

**Throughput Monitoring:**
- Requests per second per service
- Data transfer rates
- Concurrent request handling

*🖼️ Screenshot needed: Performance metrics charts with response time trends*

---

## **Step 4: Database Health**

### **Connection Pool Status**

Monitor database connectivity and performance:

**Pool Metrics:**
- **Active Connections**: Currently in-use connections
- **Idle Connections**: Available connections in pool
- **Max Pool Size**: Maximum configured connections
- **Wait Time**: Time waiting for available connections

*🖼️ Screenshot needed: Database connection pool visualization*

**Connection Health:**
- **Connection Tests**: Automated connectivity tests
- **Query Performance**: Average database query response time
- **Failed Queries**: Error rates and types
- **Lock Contention**: Database locking issues

### **Database Performance:**

**Query Monitoring:**
- Slowest queries identification
- Query execution frequency
- Index usage effectiveness
- Spatial query performance

**Storage Monitoring:**
- Database size growth
- Table and index sizes
- WAL (Write-Ahead Log) usage
- Vacuum and analyze status

*🖼️ Screenshot needed: Database performance dashboard with query metrics*

---

## **Step 5: Layer Health Monitoring**

### **Published Layer Status**

Monitor health of individual published layers:

**Layer Health Grid:**
- **Layer Name**: Published layer identifier
- **Status**: Healthy, Warning, or Error
- **Last Request**: Most recent successful request
- **Error Count**: Recent error count
- **Performance**: Average response time

*🖼️ Screenshot needed: Layer health grid showing status of multiple layers*

### **Layer Performance Metrics:**

**Request Statistics:**
- **Request Count**: Total requests per layer
- **Data Volume**: Amount of data served
- **Cache Hit Rate**: Percentage of cached responses
- **Error Rate**: Failed request percentage

**Geographic Distribution:**
- **Popular Extents**: Most frequently requested areas
- **Zoom Level Usage**: Common zoom levels for requests
- **Feature Density**: Features per tile/request

*🖼️ Screenshot needed: Layer analytics with geographic usage heatmap*

---

## **Step 6: Cache Performance**

### **Response Cache Health**

Monitor caching effectiveness:

**Cache Metrics:**
- **Hit Rate**: Percentage of requests served from cache
- **Miss Rate**: Requests requiring database queries
- **Cache Size**: Current cache memory usage
- **Eviction Rate**: Rate of cache entry removal

**Cache Performance:**
- **Cache Response Time**: Speed of cached responses
- **Database Query Time**: Time for cache misses
- **Cache Efficiency**: Overall performance improvement
- **Memory Usage**: Cache memory consumption

*🖼️ Screenshot needed: Cache performance dashboard with hit rate trends*

### **Cache Configuration:**

**Cache Settings:**
- **TTL (Time To Live)**: Cache expiration settings
- **Max Size**: Maximum cache memory allocation
- **Eviction Policy**: How entries are removed when full
- **Cache Warmup**: Pre-loading frequently accessed data

---

## **Step 7: Alert Configuration**

### **Alert Thresholds**

Configure monitoring alerts for proactive issue detection:

**System Alerts:**
- **CPU Usage**: Alert when CPU > 80% for 5 minutes
- **Memory Usage**: Alert when memory > 85%
- **Disk Space**: Alert when disk < 10% free
- **Response Time**: Alert when avg response > 5 seconds

*🖼️ Screenshot needed: Alert configuration interface with threshold settings*

**Service Alerts:**
- **Endpoint Down**: Alert when health check fails
- **High Error Rate**: Alert when errors > 5%
- **Database Connection**: Alert on connection failures
- **Cache Efficiency**: Alert when hit rate < 70%

### **Notification Channels:**

**Alert Delivery:**
- **Email**: Send alerts to administrator emails
- **Webhook**: POST alerts to external monitoring systems
- **Slack/Teams**: Integration with communication platforms
- **SMS**: Critical alerts via text message

*🖼️ Screenshot needed: Notification configuration with multiple channels*

---

## **Step 8: Historical Monitoring**

### **Metrics History**

View historical performance trends:

**Time Range Selection:**
- **Last Hour**: Recent real-time performance
- **Last Day**: Daily performance patterns
- **Last Week**: Weekly trends and patterns
- **Last Month**: Long-term performance trends

**Metric Export:**
- **CSV Download**: Export metrics for external analysis
- **API Access**: Programmatic access to historical data
- **Grafana Integration**: External dashboard visualization

*🖼️ Screenshot needed: Historical metrics charts with time range selector*

### **Performance Baselines:**

**Baseline Establishment:**
- **Performance Baseline**: Normal operating parameters
- **Anomaly Detection**: Identify unusual performance patterns
- **Trend Analysis**: Performance trajectory over time
- **Capacity Planning**: Resource usage projections

---

## **Step 9: External Monitoring Integration**

### **Prometheus Metrics**

Export metrics for external monitoring:

**Metrics Endpoint:**
```
GET /metrics
```

**Available Metrics:**
- `honua_requests_total`: Total request count by endpoint
- `honua_request_duration_seconds`: Request response time histogram
- `honua_database_connections_active`: Active database connections
- `honua_cache_hit_rate`: Response cache hit rate
- `honua_errors_total`: Error count by type and endpoint

*🖼️ Screenshot needed: Prometheus metrics configuration interface*

### **Integration Examples:**

**Grafana Dashboard:**
```yaml
# Sample Grafana query
rate(honua_requests_total[5m])
```

**AlertManager Rules:**
```yaml
# Sample alert rule
alert: HonuaHighErrorRate
expr: rate(honua_errors_total[5m]) > 0.05
```

---

## 🔧 **Troubleshooting Health Issues**

### **Common Health Problems**

**"High CPU Usage"**
- Identify resource-intensive queries
- Check for runaway processes
- Review recent configuration changes
- Consider horizontal scaling

**"Database Connection Exhaustion"**
- Increase connection pool size
- Identify long-running queries
- Check for connection leaks
- Monitor concurrent user load

**"Low Cache Hit Rate"**
- Review cache TTL settings
- Analyze request patterns
- Increase cache memory allocation
- Optimize frequently-accessed data

**"Slow Response Times"**
- Analyze slow query logs
- Check database index usage
- Review layer complexity
- Optimize vector tile generation

*🖼️ Screenshot needed: Troubleshooting panel with common issues and solutions*

### **Performance Optimization**

**Database Optimization:**
- Regular VACUUM and ANALYZE operations
- Index optimization for spatial queries
- Query plan analysis for slow queries
- Connection pooling configuration

**Cache Optimization:**
- Appropriate TTL settings for data volatility
- Cache warming for frequently accessed data
- Memory allocation based on usage patterns
- Cache eviction policy optimization

**Layer Optimization:**
- Geometry simplification for appropriate zoom levels
- Attribute filtering to reduce data transfer
- Spatial indexing for query performance
- Tile cache pre-generation for popular areas

---

## **Automated Health Actions**

### **Self-Healing Features**

**Automatic Recovery:**
- **Connection Recovery**: Automatic database reconnection
- **Cache Refresh**: Automatic cache invalidation on errors
- **Service Restart**: Restart unhealthy services
- **Resource Cleanup**: Automatic cleanup of temporary resources

**Maintenance Automation:**
- **Database Maintenance**: Scheduled VACUUM and statistics updates
- **Log Rotation**: Automatic log file management
- **Cache Cleanup**: Automatic removal of stale cache entries
- **Health Check Scheduling**: Regular automated health verification

*🖼️ Screenshot needed: Automated actions configuration panel*

---

## ➡️ **Next Steps**

Based on health monitoring insights:

1. **[Performance Optimization](../../devops/query-optimization.md)** - Optimize slow-performing components
2. **[Capacity Planning](../../devops/load-soak-testing.md)** - Plan for usage growth
3. **[Troubleshooting Guides](../../devops/troubleshooting/)** - Address specific health issues

---

## 🔗 **Related Documentation**

- [Performance Monitoring](../../devops/performance-monitoring.md) - Advanced monitoring setup
- [Troubleshooting](../../devops/TROUBLESHOOTING.md) - Common issue resolution
- [Operational Excellence](../../devops/OPERATIONAL_EXCELLENCE.md) - Operations best practices
- [Database Optimization](../../devops/query-optimization.md) - Database performance tuning

---
*Proactive health monitoring ensures your Honua Server maintains optimal performance and availability for all users and applications.*