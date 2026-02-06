# Server Down Runbook

**Alert**: HonuaServerDown
**Severity**: Critical
**Response Time**: < 5 minutes

## Symptoms
- Honua Server is not responding to health checks
- Application is returning 5xx errors or no response
- Monitoring shows service as down

## Immediate Actions

### 1. Acknowledge Alert (0-1 minute)
- Acknowledge alert in PagerDuty
- Post initial status update: "Investigating server outage"

### 2. Quick Health Check (1-3 minutes)
```bash
# Check if service is responding
curl -f https://api.honua.example.com/healthz/live
curl -f https://api.honua.example.com/healthz/ready

# Check from multiple locations if possible
curl -f https://staging.honua.example.com/healthz/live
```

### 3. Check Infrastructure (3-5 minutes)

#### Kubernetes Environment
```bash
# Check pod status
kubectl get pods -n honua-production
kubectl describe pods -l app=honua-server -n honua-production

# Check service status
kubectl get services -n honua-production
kubectl describe service honua-server -n honua-production

# Check ingress/load balancer
kubectl get ingress -n honua-production
kubectl describe ingress honua-server -n honua-production
```

#### Docker Environment
```bash
# Check container status
docker ps | grep honua
docker logs --tail 100 honua-server

# Check if container is running
docker inspect honua-server | grep Status
```

## Investigation Steps

### Step 1: Check Recent Changes
- Review recent deployments in the last 2 hours
- Check git commits: `git log --oneline --since="2 hours ago"`
- Verify if any infrastructure changes were made

### Step 2: Examine Logs
```bash
# Application logs (last 30 minutes)
kubectl logs --since=30m -l app=honua-server -n honua-production

# System logs
journalctl -u docker --since="30 minutes ago"

# Check for OOM kills
dmesg | grep -i "killed process"
```

### Step 3: Check Resource Usage
```bash
# Pod resource usage
kubectl top pods -n honua-production

# Node resource usage
kubectl top nodes

# Check if nodes are ready
kubectl get nodes
```

### Step 4: Database Connectivity
```bash
# Test database connection
kubectl exec -it deployment/honua-server -n honua-production -- \
  psql -h postgres.honua.internal -U honua_prod -c "SELECT 1;"

# Check database status
kubectl get pods -n postgres
```

## Common Causes & Solutions

### Cause 1: Pod Crashed/Restarted
**Symptoms**: Pod shows restart count > 0, recent restart time

**Solution**:
```bash
# Check restart reason
kubectl describe pod <pod-name> -n honua-production

# If crash loop, check logs for errors
kubectl logs <pod-name> -n honua-production --previous

# If OOM kill, increase memory limits
kubectl patch deployment honua-server -n honua-production -p \
  '{"spec":{"template":{"spec":{"containers":[{"name":"honua-server","resources":{"limits":{"memory":"1Gi"}}}]}}}}'
```

### Cause 2: Database Connection Issues
**Symptoms**: Health checks fail, database connection errors in logs

**Solution**:
```bash
# Check database pod status
kubectl get pods -n postgres
kubectl logs deployment/postgres -n postgres

# Test connectivity from app pod
kubectl exec -it deployment/honua-server -n honua-production -- \
  nc -zv postgres.honua.internal 5432

# Check connection strings in secrets
kubectl get secrets honua-database -n honua-production -o yaml
```

### Cause 3: Configuration Issues
**Symptoms**: App starts but health checks fail, configuration errors in logs

**Solution**:
```bash
# Check configuration
kubectl get configmap honua-config -n honua-production -o yaml
kubectl get secrets honua-secrets -n honua-production -o yaml

# Verify environment variables
kubectl exec -it deployment/honua-server -n honua-production -- env | grep -E "(DATABASE|API)"
```

### Cause 4: Load Balancer Issues
**Symptoms**: External access fails, internal health checks pass

**Solution**:
```bash
# Check load balancer status
kubectl get service honua-server -n honua-production
kubectl describe service honua-server -n honua-production

# Check ingress controller
kubectl get pods -n ingress-nginx
kubectl logs deployment/ingress-nginx-controller -n ingress-nginx

# Test internal connectivity
kubectl run test-pod --rm -it --image=curlimages/curl -- \
  curl -f http://honua-server.honua-production.svc.cluster.local:8080/healthz/live
```

## Escalation Procedures

### Escalate if:
- Unable to identify root cause within 15 minutes
- Multiple systems affected
- Database corruption suspected
- Security incident suspected

### Escalation Actions:
1. **Immediate**: Page senior engineer via PagerDuty
2. **15 minutes**: Notify engineering manager
3. **30 minutes**: Consider customer communication
4. **1 hour**: Escalate to incident commander

## Recovery Actions

### Quick Restart (if simple issue)
```bash
# Restart deployment
kubectl rollout restart deployment/honua-server -n honua-production

# Wait for rollout
kubectl rollout status deployment/honua-server -n honua-production
```

### Rollback (if recent deployment caused issue)
```bash
# Rollback to previous version
kubectl rollout undo deployment/honua-server -n honua-production

# Or use rollback script
./scripts/rollback-deployment.sh
```

### Scale Up (if resource exhaustion)
```bash
# Increase replicas temporarily
kubectl scale deployment honua-server --replicas=5 -n honua-production

# Increase resource limits
kubectl patch deployment honua-server -n honua-production -p \
  '{"spec":{"template":{"spec":{"containers":[{"name":"honua-server","resources":{"limits":{"cpu":"1000m","memory":"1Gi"}}}]}}}}'
```

## Verification

### After Recovery:
1. **Health Checks**: Verify all health endpoints respond
2. **Functionality**: Test key API endpoints
3. **Performance**: Check response times are normal
4. **Monitoring**: Verify all metrics are green

### Test Commands:
```bash
# Health checks
curl -f https://api.honua.example.com/healthz/live
curl -f https://api.honua.example.com/healthz/ready

# API functionality
curl https://api.honua.example.com/rest/services
curl https://api.honua.example.com/swagger.json

# Performance test
time curl https://api.honua.example.com/healthz/live
```

## Communication

### Status Updates:
- **Initial**: "Investigating server outage affecting API access"
- **Progress**: "Issue identified as [cause], implementing fix"
- **Resolution**: "Service restored, monitoring for stability"

### Channels:
- Internal: #incidents Slack channel
- External: Status page (status.honua.example.com)
- Customers: Email notification for extended outages

## Post-Incident

### Required Actions:
1. **Monitor**: Watch service for 30 minutes post-recovery
2. **Document**: Update incident ticket with timeline and actions
3. **Review**: Schedule post-mortem for critical outages
4. **Improve**: Update runbooks based on lessons learned

### Metrics to Review:
- Mean Time to Detection (MTTD)
- Mean Time to Recovery (MTTR)
- Customer impact duration
- Root cause analysis

## Prevention

### Monitoring Improvements:
- Add additional health checks
- Implement synthetic monitoring
- Set up predictive alerts

### Infrastructure Hardening:
- Implement high availability
- Add circuit breakers
- Improve resource limits

### Process Improvements:
- Blue-green deployments
- Canary releases
- Better rollback procedures

---

**Last Updated**: [Current Date]
**Next Review**: Quarterly
**Owner**: DevOps Team