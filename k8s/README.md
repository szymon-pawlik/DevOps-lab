# Kubernetes Deployment

## Prerequisites
- minikube installed and running
- kubectl configured

## Building Docker Images in Minikube

Before deploying, you need to build the Docker images in minikube's Docker environment:

```bash
# Set Docker environment to minikube
eval $(minikube docker-env)

# Build Producer API image
docker build -f ProducerAPI/Dockerfile -t producer-api:latest .

# Build Worker Service image
docker build -f WorkerService/Dockerfile -t worker-service:latest .

# Note: RabbitMQ uses the official image from Docker Hub, no build needed
```

## Deploying to Kubernetes

Deploy in the following order:

```bash
# 1. Deploy RabbitMQ (must be first)
kubectl apply -f rabbitmq-deployment.yaml

# Wait for RabbitMQ to be ready
kubectl wait --for=condition=available --timeout=300s deployment/rabbitmq

# 2. Deploy Producer API
kubectl apply -f producer-api-deployment.yaml

# 3. Deploy Worker Service (with 2 replicas)
kubectl apply -f worker-service-deployment.yaml
```

## Accessing the Application

### Get Producer API URL:
```bash
minikube service producer-api --url
```

Or access directly:
```bash
# Get the NodePort URL
kubectl get service producer-api
# Access via: http://<minikube-ip>:30080
```

### Access RabbitMQ Management UI:
```bash
# Port forward to access management UI
kubectl port-forward service/rabbitmq 15672:15672
# Then access: http://localhost:15672 (guest/guest)
```

## Testing

Send a job to the Producer API:
```bash
curl -X POST http://$(minikube ip):30080/api/job \
  -H "Content-Type: application/json" \
  -d '{"text": "Hello World"}'
```

## Scaling

To scale the worker service:
```bash
kubectl scale deployment worker-service --replicas=3
```

## Cleanup

To remove all resources:
```bash
kubectl delete -f worker-service-deployment.yaml
kubectl delete -f producer-api-deployment.yaml
kubectl delete -f rabbitmq-deployment.yaml
```

