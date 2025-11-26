#!/bin/bash

# Script to deploy the DevOps-lab application to Minikube
# Make sure minikube is running: minikube start

set -e

# Get the directory where the script is located
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PROJECT_ROOT="$( cd "$SCRIPT_DIR/.." && pwd )"

echo "🚀 Deploying DevOps-lab application to Minikube..."

# Load Docker images into Minikube
echo "📦 Loading Docker images into Minikube..."
eval $(minikube docker-env)
cd "$PROJECT_ROOT"
docker build -t producer-api:latest -f ProducerAPI/Dockerfile .
docker build -t worker-service:latest -f WorkerService/Dockerfile .
docker build -t frontend:latest -f frontend/Dockerfile ./frontend

# Apply ConfigMaps and Secrets first
echo "📝 Creating ConfigMaps and Secrets..."
kubectl apply -f "$SCRIPT_DIR/configmap.yaml"
kubectl apply -f "$SCRIPT_DIR/secret.yaml"

# Apply PostgreSQL
echo "🐘 Deploying PostgreSQL..."
kubectl apply -f "$SCRIPT_DIR/postgres-deployment.yaml"

# Wait for PostgreSQL to be ready
echo "⏳ Waiting for PostgreSQL to be ready..."
kubectl wait --for=condition=ready pod -l app=postgres --timeout=120s || true
kubectl wait --for=jsonpath='{.status.phase}'=Running pod -l app=postgres --timeout=120s

# Apply RabbitMQ
echo "🐰 Deploying RabbitMQ..."
kubectl apply -f "$SCRIPT_DIR/rabbitmq-deployment.yaml"

# Wait for RabbitMQ to be ready
echo "⏳ Waiting for RabbitMQ to be ready..."
# Wait for any ready pod, ignoring old pods that might be terminating
kubectl wait --for=condition=ready pod -l app=rabbitmq --timeout=120s || true
# Ensure at least one pod is ready
kubectl wait --for=jsonpath='{.status.phase}'=Running pod -l app=rabbitmq --timeout=120s

# Apply Producer API
echo "🌐 Deploying Producer API..."
kubectl apply -f "$SCRIPT_DIR/producer-api-deployment.yaml"

# Wait for Producer API to be ready
echo "⏳ Waiting for Producer API to be ready..."
kubectl wait --for=condition=ready pod -l app=producer-api --timeout=120s || true
kubectl wait --for=jsonpath='{.status.phase}'=Running pod -l app=producer-api --timeout=120s

# Apply Worker Service
echo "⚙️  Deploying Worker Service..."
kubectl apply -f "$SCRIPT_DIR/worker-service-deployment.yaml"
kubectl apply -f "$SCRIPT_DIR/worker-service-service.yaml"

# Apply HPA for Worker Service
echo "📈 Applying HPA for Worker Service..."
kubectl apply -f "$SCRIPT_DIR/worker-service-hpa.yaml"

# Apply Frontend
echo "🎨 Deploying Frontend..."
kubectl apply -f "$SCRIPT_DIR/frontend-deployment.yaml"

# Wait for all pods to be ready
echo "⏳ Waiting for all pods to be ready..."
kubectl wait --for=condition=ready pod -l app=worker-service --timeout=120s || true
kubectl wait --for=jsonpath='{.status.phase}'=Running pod -l app=worker-service --timeout=120s
kubectl wait --for=condition=ready pod -l app=frontend --timeout=120s || true
kubectl wait --for=jsonpath='{.status.phase}'=Running pod -l app=frontend --timeout=120s

echo "✅ Deployment complete!"
echo ""
echo "🌐 Starting minikube tunnel (for LoadBalancer services)..."
echo "   This will run in the background and enable access from Windows host."
echo ""
# Start minikube tunnel in background if not already running
if ! pgrep -f "minikube tunnel" > /dev/null; then
    nohup minikube tunnel > /tmp/minikube-tunnel.log 2>&1 &
    TUNNEL_PID=$!
    echo $TUNNEL_PID > /tmp/minikube-tunnel.pid
    sleep 3
    echo "✅ Minikube tunnel started (PID: $TUNNEL_PID)"
    echo "   Logs: /tmp/minikube-tunnel.log"
else
    echo "✅ Minikube tunnel is already running"
fi
echo ""
echo "⏳ Waiting for LoadBalancer IPs to be assigned..."
sleep 5
echo ""
# Start port-forward for frontend on port 3000 (port 80 requires sudo and may conflict)
echo "🔌 Starting port-forward for frontend on port 3000..."
# Kill any existing port-forward for frontend
pkill -f "kubectl port-forward.*frontend" 2>/dev/null || true
sleep 1
# Start port-forward on port 3000 (no sudo needed)
nohup kubectl port-forward svc/frontend 3000:80 > /tmp/port-forward-frontend.log 2>&1 &
PORT_FORWARD_PID=$!
echo $PORT_FORWARD_PID > /tmp/port-forward-frontend.pid
sleep 2
echo "✅ Port-forward started (PID: $PORT_FORWARD_PID)"
echo "   Logs: /tmp/port-forward-frontend.log"
echo ""
echo "📊 Service URLs (accessible from Windows host):"
FRONTEND_IP=$(kubectl get svc frontend -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || echo "pending")
API_IP=$(kubectl get svc producer-api -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || echo "pending")
RABBITMQ_IP=$(kubectl get svc rabbitmq -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || echo "pending")

echo "  Frontend:        http://127.0.0.1:3000 (via port-forward)"
echo "                   Note: Use port 3000 instead of 80 to avoid sudo requirement"

if [ "$API_IP" != "pending" ] && [ -n "$API_IP" ]; then
    echo "  Producer API:    http://$API_IP"
else
    echo "  Producer API:    http://127.0.0.1 (internal, proxied via frontend)"
fi

if [ "$RABBITMQ_IP" != "pending" ] && [ -n "$RABBITMQ_IP" ]; then
    echo "  RabbitMQ Mgmt:   http://$RABBITMQ_IP:15672 (guest/guest)"
else
    echo "  RabbitMQ Mgmt:   http://127.0.0.1:15672 (guest/guest)"
fi
echo ""
echo "📈 Check HPA status:"
echo "  kubectl get hpa worker-service-hpa"
echo ""
echo "📋 Check all pods:"
echo "  kubectl get pods"
echo ""
echo "📊 Check services:"
echo "  kubectl get svc"
echo ""
echo "💡 To stop services:"
echo "  pkill -f 'minikube tunnel'"
echo "  pkill -f 'kubectl port-forward.*frontend'"
echo "  or: kill \$(cat /tmp/minikube-tunnel.pid) && kill \$(cat /tmp/port-forward-frontend.pid)"

