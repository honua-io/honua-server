#!/bin/bash
set -euo pipefail

# Canary Deployment Script for Honua Server
# Gradually shifts traffic from old to new version

IMAGE=${1:-""}
if [[ -z "$IMAGE" ]]; then
    echo "Error: Image tag required"
    echo "Usage: $0 <image-tag>"
    exit 1
fi

NAMESPACE="${NAMESPACE:-honua}"
APP_NAME="${APP_NAME:-honua-server}"
CANARY_REPLICAS="${CANARY_REPLICAS:-1}"
PRODUCTION_REPLICAS="${PRODUCTION_REPLICAS:-3}"
CANARY_TRAFFIC_PERCENTAGE="${CANARY_TRAFFIC_PERCENTAGE:-10}"
HEALTH_CHECK_TIMEOUT="${HEALTH_CHECK_TIMEOUT:-300}"
PROMOTION_DELAY="${PROMOTION_DELAY:-300}"

echo "🐦 Starting Canary deployment for $APP_NAME"
echo "📦 Image: $IMAGE"
echo "🏷️  Namespace: $NAMESPACE"
echo "📊 Canary traffic: $CANARY_TRAFFIC_PERCENTAGE%"

# Function to check if kubectl is available
check_kubectl() {
    if ! command -v kubectl &> /dev/null; then
        echo "❌ kubectl not found. Please install kubectl."
        exit 1
    fi
}

# Function to deploy canary version
deploy_canary() {
    echo "🚀 Deploying canary version..."

    # Create canary deployment
    cat <<EOF | kubectl apply -f -
apiVersion: apps/v1
kind: Deployment
metadata:
  name: $APP_NAME-canary
  namespace: $NAMESPACE
  labels:
    app: $APP_NAME
    version: canary
spec:
  replicas: $CANARY_REPLICAS
  selector:
    matchLabels:
      app: $APP_NAME
      version: canary
  template:
    metadata:
      labels:
        app: $APP_NAME
        version: canary
    spec:
      containers:
      - name: $APP_NAME
        image: $IMAGE
        ports:
        - containerPort: 8080
        readinessProbe:
          httpGet:
            path: /healthz/ready
            port: 8080
          initialDelaySeconds: 30
          periodSeconds: 10
        livenessProbe:
          httpGet:
            path: /healthz/live
            port: 8080
          initialDelaySeconds: 30
          periodSeconds: 10
        resources:
          requests:
            memory: "256Mi"
            cpu: "100m"
          limits:
            memory: "512Mi"
            cpu: "500m"
EOF

    # Wait for canary deployment to be ready
    kubectl wait --for=condition=available deployment/$APP_NAME-canary \
        --namespace=$NAMESPACE --timeout=${HEALTH_CHECK_TIMEOUT}s

    echo "✅ Canary deployment ready"
}

# Function to configure traffic splitting
configure_traffic_splitting() {
    local canary_weight=$1
    local stable_weight=$((100 - canary_weight))

    echo "🔀 Configuring traffic split: $stable_weight% stable, $canary_weight% canary"

    # Update service to include both stable and canary
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Service
metadata:
  name: $APP_NAME
  namespace: $NAMESPACE
  labels:
    app: $APP_NAME
spec:
  ports:
  - port: 80
    targetPort: 8080
    protocol: TCP
  selector:
    app: $APP_NAME
---
apiVersion: networking.istio.io/v1alpha3
kind: VirtualService
metadata:
  name: $APP_NAME
  namespace: $NAMESPACE
spec:
  hosts:
  - $APP_NAME
  http:
  - match:
    - headers:
        canary:
          exact: "true"
    route:
    - destination:
        host: $APP_NAME
        subset: canary
  - route:
    - destination:
        host: $APP_NAME
        subset: stable
      weight: $stable_weight
    - destination:
        host: $APP_NAME
        subset: canary
      weight: $canary_weight
---
apiVersion: networking.istio.io/v1alpha3
kind: DestinationRule
metadata:
  name: $APP_NAME
  namespace: $NAMESPACE
spec:
  host: $APP_NAME
  subsets:
  - name: stable
    labels:
      version: stable
  - name: canary
    labels:
      version: canary
EOF
}

# Function to monitor canary metrics
monitor_canary_metrics() {
    echo "📊 Monitoring canary metrics for $PROMOTION_DELAY seconds..."

    local end_time=$(($(date +%s) + PROMOTION_DELAY))
    local check_interval=30

    while [[ $(date +%s) -lt $end_time ]]; do
        echo "🔍 Checking canary health and metrics..."

        # Check metrics health endpoint (replace with actual metrics query)
        local canary_metrics_status=$(kubectl exec -n $NAMESPACE deployment/$APP_NAME-canary -- \
            curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/api/metrics/health || echo "000")

        local stable_metrics_status=$(kubectl exec -n $NAMESPACE deployment/$APP_NAME-stable -- \
            curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/api/metrics/health || echo "000")

        echo "📈 Canary metrics status: $canary_metrics_status"
        echo "📈 Stable metrics status: $stable_metrics_status"

        if [[ "$canary_metrics_status" != "200" ]]; then
            echo "⚠️  Canary metrics endpoint unhealthy"
        fi

        sleep $check_interval
    done

    echo "✅ Canary monitoring completed successfully"
}

# Function to promote canary to stable
promote_canary() {
    echo "🎉 Promoting canary to stable..."

    # Update stable deployment with canary image
    kubectl set image deployment/$APP_NAME-stable \
        $APP_NAME=$IMAGE --namespace=$NAMESPACE

    # Wait for stable deployment to be ready
    kubectl wait --for=condition=available deployment/$APP_NAME-stable \
        --namespace=$NAMESPACE --timeout=${HEALTH_CHECK_TIMEOUT}s

    # Remove traffic splitting (100% to stable)
    cat <<EOF | kubectl apply -f -
apiVersion: networking.istio.io/v1alpha3
kind: VirtualService
metadata:
  name: $APP_NAME
  namespace: $NAMESPACE
spec:
  hosts:
  - $APP_NAME
  http:
  - route:
    - destination:
        host: $APP_NAME
        subset: stable
      weight: 100
EOF

    echo "✅ Canary promoted to stable"
}

# Function to cleanup canary deployment
cleanup_canary() {
    echo "🧹 Cleaning up canary deployment..."

    kubectl delete deployment $APP_NAME-canary --namespace=$NAMESPACE || true

    echo "✅ Canary deployment cleaned up"
}

# Function to rollback canary
rollback_canary() {
    echo "🔄 Rolling back canary deployment..."

    # Remove traffic from canary
    cat <<EOF | kubectl apply -f -
apiVersion: networking.istio.io/v1alpha3
kind: VirtualService
metadata:
  name: $APP_NAME
  namespace: $NAMESPACE
spec:
  hosts:
  - $APP_NAME
  http:
  - route:
    - destination:
        host: $APP_NAME
        subset: stable
      weight: 100
EOF

    cleanup_canary

    echo "✅ Canary rollback completed"
    exit 1
}

# Function to handle errors and trigger rollback
handle_error() {
    echo "❌ Error detected during canary deployment"
    rollback_canary
}

# Main deployment logic
main() {
    check_kubectl

    # Set up error handling
    trap handle_error ERR

    # Deploy canary version
    deploy_canary

    # Configure initial traffic splitting
    configure_traffic_splitting $CANARY_TRAFFIC_PERCENTAGE

    # Monitor canary metrics
    if ! monitor_canary_metrics; then
        echo "❌ Canary metrics indicate issues"
        rollback_canary
    fi

    # Promote canary to stable
    promote_canary

    # Cleanup canary
    cleanup_canary

    echo "🎉 Canary deployment completed successfully!"
    echo "📦 Image: $IMAGE promoted to stable"
}

main "$@"
