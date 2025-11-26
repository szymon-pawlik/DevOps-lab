#!/bin/bash

# Script to deploy the DevOps-lab application to Minikube
# Make sure minikube is running: minikube start

set -e

echo "🚀 Deploying DevOps-lab application to Minikube..."

# Load Docker images into Minikube
echo "📦 Loading Docker images into Minikube..."
eval $(minikube docker-env)
docker build -t producer-api:latest -f ProducerAPI/Dockerfile .
docker build -t worker-service:latest -f WorkerService/Dockerfile .
docker build -t frontend:latest -f frontend/Dockerfile ./frontend

# Apply ConfigMaps and Secrets first
echo "📝 Creating ConfigMaps and Secrets..."
kubectl apply -f k8s/configmap.yaml
kubectl apply -f k8s/secret.yaml

# Apply PostgreSQL
echo "🐘 Deploying PostgreSQL..."
kubectl apply -f k8s/postgres-deployment.yaml

# Wait for PostgreSQL to be ready
echo "⏳ Waiting for PostgreSQL to be ready..."
kubectl wait --for=condition=ready pod -l app=postgres --timeout=120s

# Apply RabbitMQ
echo "🐰 Deploying RabbitMQ..."
kubectl apply -f k8s/rabbitmq-deployment.yaml

# Wait for RabbitMQ to be ready
echo "⏳ Waiting for RabbitMQ to be ready..."
kubectl wait --for=condition=ready pod -l app=rabbitmq --timeout=120s

# Apply Producer API
echo "🌐 Deploying Producer API..."
kubectl apply -f k8s/producer-api-deployment.yaml

# Wait for Producer API to be ready
echo "⏳ Waiting for Producer API to be ready..."
kubectl wait --for=condition=ready pod -l app=producer-api --timeout=120s

# Apply Worker Service
echo "⚙️  Deploying Worker Service..."
kubectl apply -f k8s/worker-service-deployment.yaml
kubectl apply -f k8s/worker-service-service.yaml

# Apply HPA for Worker Service
echo "📈 Applying HPA for Worker Service..."
kubectl apply -f k8s/worker-service-hpa.yaml

# Apply Frontend
echo "🎨 Deploying Frontend..."
kubectl apply -f k8s/frontend-deployment.yaml

# Wait for all pods to be ready
echo "⏳ Waiting for all pods to be ready..."
kubectl wait --for=condition=ready pod -l app=worker-service --timeout=120s
kubectl wait --for=condition=ready pod -l app=frontend --timeout=120s

echo "✅ Deployment complete!"
echo ""
echo "📊 Get service URLs:"
echo "  Frontend:        http://$(minikube ip):30000"
echo "  Producer API:     http://$(minikube ip):30080"
echo "  RabbitMQ Mgmt:    http://$(minikube ip):$(kubectl get svc rabbitmq -o jsonpath='{.spec.ports[?(@.name=="management")].nodePort}')"
echo ""
echo "📈 Check HPA status:"
echo "  kubectl get hpa worker-service-hpa"
echo ""
echo "📋 Check all pods:"
echo "  kubectl get pods"
echo ""
echo "📊 Check services:"
echo "  kubectl get svc"

