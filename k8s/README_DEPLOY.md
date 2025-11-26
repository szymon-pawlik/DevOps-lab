# Instrukcje wdrożenia Kubernetes

## Pierwsze wdrożenie (tylko raz)

```bash
cd k8s
./deploy.sh
```

To wdroży wszystkie komponenty do Kubernetes i automatycznie uruchomi port forwarding.

## Kiedy uruchamiać `./deploy.sh` ponownie?

**TAK - uruchom ponownie, gdy:**
- ✅ Zmieniłeś konfigurację Kubernetes (ConfigMaps, Secrets, Deploymenty)
- ✅ Dodałeś nowe komponenty
- ✅ Zmieniłeś porty lub ustawienia serwisów
- ✅ Chcesz zresetować całe środowisko

**NIE - nie trzeba uruchamiać, gdy:**
- ❌ Tylko chcesz uzyskać dostęp do aplikacji (użyj `./port-forward.sh start`)
- ❌ Zrestartowałeś minikube (tylko uruchom `./port-forward.sh start`)
- ❌ Pody się zrestartowały (działają automatycznie)

## Codzienne użycie

### 1. Sprawdź czy wszystko działa:
```bash
kubectl get pods
```

### 2. Jeśli wszystko działa, uruchom tylko port forwarding:
```bash
cd k8s
./port-forward.sh start
```

### 3. Zatrzymaj port forwarding (gdy skończysz):
```bash
cd k8s
./port-forward.sh stop
```

## Aktualizacja kodu aplikacji

Jeśli zmieniłeś kod i chcesz zaktualizować aplikację:

### 1. Przebuduj obrazy Docker w Minikube:
```bash
eval $(minikube docker-env)
cd /home/avatar/devops/DevOps-lab

# Przebuduj zmienione komponenty
docker build -t producer-api:latest -f ProducerAPI/Dockerfile .
docker build -t worker-service:latest -f WorkerService/Dockerfile .
docker build -t frontend:latest -f frontend/Dockerfile ./frontend
```

### 2. Zrestartuj deploymenty:
```bash
kubectl rollout restart deployment/producer-api
kubectl rollout restart deployment/worker-service
kubectl rollout restart deployment/frontend
```

### 3. Sprawdź status:
```bash
kubectl get pods -w
```

## Przydatne komendy

```bash
# Sprawdź status wszystkich podów
kubectl get pods

# Sprawdź logi
kubectl logs -l app=producer-api --tail=50
kubectl logs -l app=worker-service --tail=50

# Sprawdź status port forwarding
cd k8s && ./port-forward.sh status

# Zatrzymaj wszystko
cd k8s && ./port-forward.sh stop
kubectl delete -f k8s/
```

## Podsumowanie

- **Pierwsze wdrożenie**: `./deploy.sh` (raz)
- **Codzienne użycie**: `./port-forward.sh start` (gdy potrzebujesz dostępu)
- **Aktualizacja kodu**: przebuduj obrazy + restart deploymentów
- **Zmiana konfiguracji K8s**: `./deploy.sh` ponownie

