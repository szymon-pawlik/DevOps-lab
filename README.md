# System Przetwarzania Zadań (Producer-Consumer)

Projekt demonstracyjny systemu rozproszonego opartego na wzorcu Producer-Consumer, wykorzystujący RabbitMQ jako broker komunikatów.

## Architektura

Projekt składa się z trzech głównych komponentów:

1. **Producer API** (C# .NET Web API) - wystawia endpoint `/api/job` do przyjmowania zleceń
2. **RabbitMQ** - broker komunikatów (gotowy obraz Docker)
3. **Worker Service** (C# .NET Console App) - przetwarza zadania z kolejki

## Struktura projektu

```
.
├── ProducerAPI/          # Web API - Producer
│   ├── Controllers/
│   ├── Dockerfile
│   └── ...
├── WorkerService/        # Console App - Consumer
│   ├── Dockerfile
│   └── ...
├── k8s/                  # Pliki konfiguracyjne Kubernetes
│   ├── rabbitmq-deployment.yaml
│   ├── producer-api-deployment.yaml
│   └── worker-service-deployment.yaml
├── .github/workflows/    # GitHub Actions
│   ├── docker-build.yml
│   └── build-app.yml
├── docker-compose.yml    # Docker Compose configuration
└── README.md
```

## Wymagania

- .NET 8.0 SDK
- Docker i Docker Compose
- Kubernetes (minikube) - opcjonalnie

## Uruchomienie z Docker Compose

```bash
# Zbuduj i uruchom wszystkie serwisy
docker-compose up --build

# Uruchom w tle
docker-compose up -d --build
```

Aplikacja będzie dostępna pod adresem:
- Producer API: http://localhost:8080
- RabbitMQ Management UI: http://localhost:15672 (guest/guest)

## Testowanie

### Wysłanie zadania do przetworzenia:

```bash
curl -X POST http://localhost:8080/api/job \
  -H "Content-Type: application/json" \
  -d '{"text": "Hello World"}'
```

Worker Service automatycznie przetworzy zadanie (zmieni tekst na wielkie litery) i zaloguje wynik.

## Dockerfile

Główny Dockerfile dla Producer API znajduje się w: [ProducerAPI/Dockerfile](ProducerAPI/Dockerfile)

## GitHub Actions

Projekt zawiera dwa workflow:

1. **docker-build.yml** - sprawdza poprawność budowania obrazów Docker
2. **build-app.yml** - sprawdza poprawność budowania aplikacji .NET

Workflow są automatycznie uruchamiane przy:
- Commit do brancha `master` lub `main`
- Merge Pull Request do brancha `master` lub `main`

## Kubernetes Deployment

Szczegółowe instrukcje dotyczące wdrożenia w Kubernetes znajdują się w [k8s/README.md](k8s/README.md)

### Szybki start:

```bash
# 1. Zbuduj obrazy w minikube
eval $(minikube docker-env)
docker build -f ProducerAPI/Dockerfile -t producer-api:latest .
docker build -f WorkerService/Dockerfile -t worker-service:latest .

# 2. Wdróż do Kubernetes
kubectl apply -f k8s/rabbitmq-deployment.yaml
kubectl apply -f k8s/producer-api-deployment.yaml
kubectl apply -f k8s/worker-service-deployment.yaml

# 3. Sprawdź status
kubectl get pods
kubectl get services
```

Worker Service jest domyślnie skalowany do 2 replik.

## Linki

- [Dockerfile Producer API](ProducerAPI/Dockerfile)
- [Dockerfile Worker Service](WorkerService/Dockerfile)

