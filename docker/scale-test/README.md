# Scale-Test Docker Assets

This directory contains the local multi-node scale-test stack and files that are specific to that stack.

## Layout

- `compose.yml` - Docker Compose stack for multiple Honua instances, PostGIS, Redis, Nginx, optional exporters, Prometheus, and Grafana.
- `nginx/` - Nginx load-balancer template and entrypoint renderer used only by the scale-test stack.
- `prometheus/scale-test.yml` - Prometheus scrape configuration for the local scale-test services.

Use `scripts/scale/scale-test.sh` as the supported entrypoint. Direct compose usage is:

```bash
docker compose -f docker/scale-test/compose.yml up --scale honua=3
```

Reusable Prometheus alert rules and Grafana provisioning live in `docker/monitoring/` because they are not scale-test-only assets.
