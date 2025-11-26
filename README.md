# TextFlow

System przetwarzania tekstów oparty na wzorcu Producer-Consumer z wykorzystaniem C# .NET, RabbitMQ, Docker, Kubernetes i React.

## Architektura

- **Producer API** (C# .NET Web API) - przyjmuje zadania i wysyła je do kolejki
- **Worker Service** (C# .NET Console App) - przetwarza zadania z kolejki
- **RabbitMQ** - kolejka komunikatów
- **PostgreSQL** - baza danych
- **Frontend** (React + TypeScript + Vite + SASS) - interfejs użytkownika
- **SignalR** - komunikacja WebSocket w czasie rzeczywistym

## Wymagania wstępne

- Docker i Docker Compose (wersja 2.0+)
- .NET 8.0 SDK (opcjonalnie, do lokalnego rozwoju)
- Node.js 20+ (opcjonalnie, do lokalnego rozwoju frontendu)
- Minikube i kubectl (dla Kubernetes)

### Sprawdzenie instalacji Docker

Przed uruchomieniem projektu upewnij się, że Docker jest zainstalowany i działa:

```bash
# Sprawdź wersję Docker
docker --version

# Sprawdź wersję Docker Compose
docker compose version

# Sprawdź czy Docker daemon działa
docker ps

# Jeśli powyższe komendy nie działają, zainstaluj Docker:
# Windows/Mac: https://www.docker.com/products/docker-desktop
# Linux: https://docs.docker.com/engine/install/
```

### Sprawdzenie dostępności portów

Upewnij się, że następujące porty są wolne:
- 3000 (frontend)
- 8080 (producer-api)
- 5432 (postgres)
- 5672 (rabbitmq)
- 15672 (rabbitmq management)

```bash
# Linux/Mac: sprawdź czy porty są wolne
netstat -tuln | grep -E ':(3000|8080|5432|5672|15672)'

# Windows PowerShell:
netstat -ano | findstr "3000 8080 5432 5672 15672"
```

## Szybki start z Docker Compose

### 1. Sklonuj repozytorium

```bash
git clone https://github.com/szymon-pawlik/DevOps-lab.git
cd DevOps-lab
```

### 2. Uruchom wszystkie serwisy

```bash
# Uruchom wszystkie serwisy w tle z budowaniem obrazów
docker compose up -d --build

# Jeśli używasz starszej wersji Docker Compose (v1), użyj:
# docker-compose up -d --build
```

**Uwaga**: Pierwsze uruchomienie może zająć kilka minut, ponieważ Docker pobierze wszystkie obrazy bazowe i zbuduje aplikacje.

### 3. Sprawdź status

```bash
# Sprawdź status wszystkich kontenerów
docker compose ps

# Sprawdź logi wszystkich serwisów
docker compose logs

# Sprawdź logi konkretnego serwisu
docker compose logs producer-api
docker compose logs worker-service
docker compose logs frontend
docker compose logs postgres
docker compose logs rabbitmq

# Sprawdź czy wszystkie kontenery są zdrowe
docker compose ps | grep -E "(Up|healthy)"
```

Jeśli któryś z kontenerów nie działa:

```bash
# Zatrzymaj wszystkie kontenery
docker compose down

# Usuń stare obrazy i zbuduj od nowa
docker compose build --no-cache

# Uruchom ponownie
docker compose up -d
```

### 4. Dostęp do aplikacji

- **Frontend**: http://localhost:3000
- **Producer API**: http://localhost:8080
- **RabbitMQ Management**: http://localhost:15672 (guest/guest)
- **PostgreSQL**: localhost:5432 (postgres/postgres)

### 5. Testowanie

1. Otwórz http://localhost:3000
2. Zarejestruj nowego użytkownika lub zaloguj się jako:
   - **Username**: `admin`
   - **Password**: `Admin123!`
3. Utwórz zadanie i obserwuj jego przetwarzanie w czasie rzeczywistym

### 6. Zatrzymanie

```bash
docker compose down
```

Aby usunąć również dane (baza danych):

```bash
docker compose down -v
```

## Uruchomienie w Kubernetes (Minikube)

### 1. Instalacja Minikube i kubectl

#### Ubuntu/WSL2:

```bash
# Zainstaluj kubectl
curl -LO "https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl"
sudo install -o root -g root -m 0755 kubectl /usr/local/bin/kubectl

# Zainstaluj Minikube
curl -LO https://storage.googleapis.com/minikube/releases/latest/minikube-linux-amd64
sudo install minikube-linux-amd64 /usr/local/bin/minikube

# Uruchom Minikube
minikube start
```

### 2. Wdrożenie aplikacji

```bash
cd k8s
./deploy.sh
```

Skrypt automatycznie:
- Buduje obrazy Docker
- Tworzy ConfigMaps i Secrets
- Wdraża wszystkie komponenty
- Uruchamia minikube tunnel (dla LoadBalancer services)

### 3. Dostęp do aplikacji

Po wdrożeniu, aplikacja jest dostępna przez:

- **Frontend**: http://127.0.0.1
- **Producer API**: http://127.0.0.1 (przez nginx proxy)
- **RabbitMQ Management**: http://127.0.0.1:15672 (guest/guest)

### 4. Sprawdzenie statusu

```bash
# Sprawdź wszystkie pody
kubectl get pods

# Sprawdź serwisy
kubectl get svc

# Sprawdź HPA (Auto-scaling)
kubectl get hpa

# Logi
kubectl logs -l app=producer-api
kubectl logs -l app=worker-service
```

### 5. Zatrzymanie minikube tunnel

```bash
pkill -f "minikube tunnel"
```

### 6. Usunięcie wdrożenia

```bash
kubectl delete -f k8s/
```

## Rozwój lokalny (bez Docker)

### Backend (Producer API)

```bash
cd ProducerAPI
dotnet restore
dotnet run
```

API będzie dostępne na: http://localhost:5000

### Worker Service

```bash
cd WorkerService
dotnet restore
dotnet run
```

### Frontend

```bash
cd frontend
npm install
npm run dev
```

Frontend będzie dostępny na: http://localhost:5173

**Uwaga**: Wymaga uruchomionych serwisów (RabbitMQ, PostgreSQL, Producer API).

## Konfiguracja

### Zmienne środowiskowe

#### Producer API (`appsettings.json`):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Port=5432;Database=jobdb;Username=postgres;Password=postgres"
  },
  "RabbitMQ": {
    "Host": "rabbitmq",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest"
  },
  "Jwt": {
    "Key": "YourSuperSecretKeyThatShouldBeAtLeast32CharactersLong!",
    "Issuer": "DevOpsLab",
    "Audience": "DevOpsLab"
  }
}
```

#### Worker Service (`appsettings.json`):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Port=5432;Database=jobdb;Username=postgres;Password=postgres"
  },
  "RabbitMQ": {
    "Host": "rabbitmq",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest"
  },
  "ApiUrl": "http://producer-api:8080"
}
```

#### Frontend (`.env`):

```env
VITE_API_URL=http://localhost:8080
```

## Testowanie

### Test systemu (Docker Compose)

```bash
./test-system.sh
```

Skrypt sprawdza:
- Status wszystkich kontenerów
- Połączenie z API
- Połączenie z bazą danych
- Połączenie z RabbitMQ
- Tworzenie i przetwarzanie zadania

### Testy manualne

1. **Rejestracja użytkownika**:
   - Otwórz frontend
   - Kliknij "Register"
   - Wypełnij formularz

2. **Logowanie**:
   - Użyj utworzonego konta lub admin/admin

3. **Tworzenie zadania**:
   - Wybierz typ zadania (Uppercase, Lowercase, Reverse, Count Words, Translate)
   - Wpisz tekst
   - Kliknij "Submit Job"
   - Obserwuj zmianę statusu w czasie rzeczywistym

4. **Historia zadań**:
   - Zobacz wszystkie swoje zadania
   - Admin widzi wszystkie zadania

## Uwierzytelnianie

- **JWT Token** - używany do autoryzacji
- **Role**: Admin, User
- **Domyślne konto admin**: `admin` / `Admin123!`

## Typy zadań

1. **Uppercase** - konwertuje tekst na wielkie litery
2. **Lowercase** - konwertuje tekst na małe litery
3. **Reverse** - odwraca kolejność znaków
4. **Count Words** - liczy słowa w tekście
5. **Translate** - tłumaczy EN↔PL używając Google Translate

### Tłumaczenie

System używa Google Translate do tłumaczenia tekstów między językami angielskim i polskim. Tłumaczenie działa automatycznie - system wykrywa język źródłowy i tłumaczy tekst w odpowiednią stronę.

## Struktura projektu

```
DevOps-lab/
├── ProducerAPI/          # C# .NET Web API
│   ├── Controllers/       # API Controllers
│   ├── Models/           # Modele danych
│   ├── Data/             # DbContext
│   ├── Hubs/             # SignalR Hubs
│   └── Dockerfile
├── WorkerService/         # C# .NET Console App
│   ├── Models/           # Modele danych
│   ├── Data/             # DbContext
│   └── Dockerfile
├── frontend/              # React + TypeScript
│   ├── src/
│   │   ├── App.tsx       # Główny komponent
│   │   ├── Login.tsx    # Komponent logowania
│   │   └── auth.ts      # Serwis autentykacji
│   └── Dockerfile
├── k8s/                   # Kubernetes manifests
│   ├── deploy.sh         # Skrypt wdrożenia
│   ├── configmap.yaml    # ConfigMaps
│   ├── secret.yaml       # Secrets
│   ├── *-deployment.yaml # Deploymenty
│   └── *-hpa.yaml        # Horizontal Pod Autoscaler
├── docker-compose.yml    # Docker Compose configuration
└── README.md             # Ten plik
```

## CI/CD

Projekt zawiera GitHub Actions workflows:

- **docker-build.yml** - sprawdza poprawność budowania obrazów Docker
- **build-app.yml** - sprawdza poprawność budowania aplikacji

Workflows uruchamiają się automatycznie przy commit/merge do brancha Master.

## Dodatkowe informacje

- **Database**: PostgreSQL 16
- **Message Broker**: RabbitMQ 3-management
- **Backend**: .NET 8.0
- **Frontend**: React 18, TypeScript, Vite, SASS
- **Authentication**: JWT
- **Real-time**: SignalR (Long Polling)

## Licencja

Projekt edukacyjny - DevOps Lab

