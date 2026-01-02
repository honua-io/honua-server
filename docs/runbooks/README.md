# Honua Server Operational Runbooks

This directory contains operational runbooks for maintaining and troubleshooting the Honua Server geospatial feature server.

## Quick Reference

| Alert | Runbook | Severity | Response Time |
|-------|---------|----------|---------------|
| Server Down | [server-down.md](server-down.md) | Critical | < 5 minutes |
| Health Check Failing | [health-check-failure.md](health-check-failure.md) | Warning | < 15 minutes |
| High Response Time | [high-response-time.md](high-response-time.md) | Warning | < 30 minutes |
| High Error Rate | [high-error-rate.md](high-error-rate.md) | Critical | < 10 minutes |
| Database Issues | [database-issues.md](database-issues.md) | Critical | < 5 minutes |
| High Memory Usage | [high-memory-usage.md](high-memory-usage.md) | Warning | < 1 hour |
| High CPU Usage | [high-cpu-usage.md](high-cpu-usage.md) | Warning | < 1 hour |
| Security Incidents | [security-incidents.md](security-incidents.md) | Critical | < 5 minutes |

## Escalation Matrix

### Level 1 - On-Call Engineer
- **Response Time**: 5 minutes
- **Responsibilities**: Initial triage, basic troubleshooting, alert acknowledgment
- **Escalation Criteria**: Unable to resolve within 30 minutes for critical alerts

### Level 2 - Senior Engineer
- **Response Time**: 15 minutes (when escalated)
- **Responsibilities**: Advanced troubleshooting, system analysis, deployment decisions
- **Escalation Criteria**: Unable to resolve within 1 hour for critical alerts

### Level 3 - Engineering Manager
- **Response Time**: 30 minutes (when escalated)
- **Responsibilities**: Resource allocation, external communication, incident management
- **Escalation Criteria**: Major outage affecting customers for >2 hours

## Contact Information

### Primary Contacts
- **On-Call Engineer**: PagerDuty rotation
- **Engineering Manager**: [manager@honua.example.com]
- **DevOps Lead**: [devops@honua.example.com]
- **Database Administrator**: [dba@honua.example.com]

### Emergency Contacts
- **CEO**: [ceo@honua.example.com] (for major incidents)
- **CTO**: [cto@honua.example.com] (for technical decisions)
- **Security Team**: [security@honua.example.com] (for security incidents)

## Incident Response Process

### 1. Alert Reception
- Acknowledge alert within 5 minutes
- Initial assessment and triage
- Update incident status in PagerDuty

### 2. Investigation
- Follow relevant runbook procedures
- Gather logs and metrics
- Document findings in incident ticket

### 3. Mitigation
- Apply immediate fixes if available
- Implement workarounds if needed
- Escalate if unable to resolve

### 4. Resolution
- Verify fix is working
- Monitor for recurrence
- Update incident status

### 5. Post-Incident
- Write incident report
- Schedule post-mortem if needed
- Update runbooks based on learnings

## Tools and Resources

### Monitoring and Alerting
- **Aspire Dashboard (OTel)**: http://monitoring.honua.example.com:18888
- **PagerDuty**: https://honua.pagerduty.com

### Logs and Tracing
- **Aspire Dashboard (logs/traces)**: http://monitoring.honua.example.com:18888

### Infrastructure
- **AWS Console**: https://console.aws.amazon.com
- **Kubernetes Dashboard**: https://k8s.honua.example.com
- **Terraform Cloud**: https://app.terraform.io/app/honua

### Application
- **Admin Portal**: https://admin.honua.example.com
- **API Documentation**: https://api.honua.example.com/swagger
- **Status Page**: https://status.honua.example.com

## Common Commands

### Kubernetes
```bash
# Get pod status
kubectl get pods -n honua-production

# View pod logs
kubectl logs -f deployment/honua-server -n honua-production

# Execute into pod
kubectl exec -it deployment/honua-server -n honua-production -- bash

# Check resource usage
kubectl top pods -n honua-production
```

### Docker
```bash
# View container logs
docker logs -f honua-server

# Execute into container
docker exec -it honua-server bash

# Check container stats
docker stats honua-server
```

### Database
```bash
# Connect to database
psql -h postgres.honua.internal -U honua_prod -d honua_production

# Check active connections
SELECT count(*) FROM pg_stat_activity;

# Check slow queries
SELECT query, mean_time, calls FROM pg_stat_statements
ORDER BY mean_time DESC LIMIT 10;
```

### Application Health
```bash
# Health checks
curl -f https://api.honua.example.com/healthz/live
curl -f https://api.honua.example.com/healthz/ready

# Metrics
curl https://api.honua.example.com/api/metrics/health

# API test
curl https://api.honua.example.com/rest/services
```

## Change Management

### Deployment Process
1. Deploy to staging environment
2. Run smoke tests
3. Get approval for production deployment
4. Deploy to production during maintenance window
5. Monitor for 30 minutes post-deployment

### Rollback Process
1. Identify deployment causing issues
2. Execute rollback script: `./scripts/rollback-deployment.sh`
3. Verify rollback success
4. Investigate root cause

### Maintenance Windows
- **Standard**: Sundays 2:00-4:00 AM UTC
- **Emergency**: Any time with 2-hour notice
- **Major**: Scheduled 1 week in advance

## Security Procedures

### Incident Response
1. Isolate affected systems
2. Preserve evidence
3. Notify security team
4. Follow security incident runbook
5. Document all actions

### Access Management
- Follow principle of least privilege
- Rotate credentials quarterly
- Audit access permissions monthly
- Use multi-factor authentication

### Compliance
- Regular security scans
- Vulnerability assessments
- Penetration testing annually
- Compliance audits as required

## Documentation Updates

Keep runbooks updated:
- Review quarterly
- Update after incidents
- Version control all changes
- Test procedures regularly

For questions or updates to runbooks, contact: [devops@honua.example.com]
