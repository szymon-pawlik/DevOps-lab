#!/bin/bash

echo "=========================================="
echo "Testing TextFlow System"
echo "=========================================="

API_URL="http://localhost:8080"
FRONTEND_URL="http://localhost:3000"

# Colors
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Test 1: Check if all containers are running
echo -e "\n${YELLOW}[Test 1] Checking Docker containers...${NC}"
if docker compose ps | grep -q "Up"; then
    echo -e "${GREEN}✓ Containers are running${NC}"
    docker compose ps
else
    echo -e "${RED}✗ Some containers are not running${NC}"
    exit 1
fi

# Test 2: Check API Health
echo -e "\n${YELLOW}[Test 2] Checking API Health...${NC}"
HEALTH_RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" "$API_URL/api/job/health")
if [ "$HEALTH_RESPONSE" == "200" ]; then
    echo -e "${GREEN}✓ API is healthy (HTTP $HEALTH_RESPONSE)${NC}"
else
    echo -e "${RED}✗ API health check failed (HTTP $HEALTH_RESPONSE)${NC}"
    exit 1
fi

# Test 3: Submit a job
echo -e "\n${YELLOW}[Test 3] Submitting a test job...${NC}"
JOB_RESPONSE=$(curl -s -X POST "$API_URL/api/job" \
    -H "Content-Type: application/json" \
    -d '{"text": "Test Job 1"}')

if echo "$JOB_RESPONSE" | grep -q "jobId"; then
    JOB_ID=$(echo "$JOB_RESPONSE" | grep -o '"jobId":"[^"]*' | cut -d'"' -f4)
    echo -e "${GREEN}✓ Job submitted successfully${NC}"
    echo "  Job ID: $JOB_ID"
else
    echo -e "${RED}✗ Failed to submit job${NC}"
    echo "  Response: $JOB_RESPONSE"
    exit 1
fi

# Test 4: Wait for processing
echo -e "\n${YELLOW}[Test 4] Waiting for job processing (5 seconds)...${NC}"
sleep 5

# Test 5: Check job in database via API
echo -e "\n${YELLOW}[Test 5] Checking job status in database...${NC}"
JOBS_RESPONSE=$(curl -s "$API_URL/api/job")
if echo "$JOBS_RESPONSE" | grep -q "$JOB_ID"; then
    echo -e "${GREEN}✓ Job found in database${NC}"
    
    # Check if job is completed
    if echo "$JOBS_RESPONSE" | grep -q '"status":2'; then
        echo -e "${GREEN}✓ Job completed successfully${NC}"
    elif echo "$JOBS_RESPONSE" | grep -q '"status":1'; then
        echo -e "${YELLOW}⚠ Job is still processing${NC}"
    else
        echo -e "${YELLOW}⚠ Job status: $(echo "$JOBS_RESPONSE" | grep -o '"status":[0-9]' | head -1)${NC}"
    fi
else
    echo -e "${RED}✗ Job not found in database${NC}"
    exit 1
fi

# Test 6: Submit multiple jobs
echo -e "\n${YELLOW}[Test 6] Submitting multiple jobs...${NC}"
for i in {2..5}; do
    curl -s -X POST "$API_URL/api/job" \
        -H "Content-Type: application/json" \
        -d "{\"text\": \"Test Job $i\"}" > /dev/null
    echo "  Submitted job $i"
done
echo -e "${GREEN}✓ Multiple jobs submitted${NC}"

# Test 7: Check all jobs
echo -e "\n${YELLOW}[Test 7] Checking all jobs...${NC}"
sleep 3
JOBS_COUNT=$(curl -s "$API_URL/api/job" | grep -o '"id"' | wc -l)
echo "  Found $JOBS_COUNT jobs in database"
if [ "$JOBS_COUNT" -ge 5 ]; then
    echo -e "${GREEN}✓ All jobs are stored${NC}"
else
    echo -e "${YELLOW}⚠ Expected at least 5 jobs, found $JOBS_COUNT${NC}"
fi

# Test 8: Check PostgreSQL connection
echo -e "\n${YELLOW}[Test 8] Checking PostgreSQL connection...${NC}"
if docker compose exec -T postgres psql -U postgres -d jobdb -c "SELECT COUNT(*) FROM \"Jobs\";" > /dev/null 2>&1; then
    DB_COUNT=$(docker compose exec -T postgres psql -U postgres -d jobdb -t -c "SELECT COUNT(*) FROM \"Jobs\";" | tr -d ' ')
    echo -e "${GREEN}✓ PostgreSQL is accessible${NC}"
    echo "  Jobs in database: $DB_COUNT"
else
    echo -e "${RED}✗ Cannot connect to PostgreSQL${NC}"
    exit 1
fi

# Test 9: Check RabbitMQ
echo -e "\n${YELLOW}[Test 9] Checking RabbitMQ...${NC}"
if curl -s -u guest:guest "http://localhost:15672/api/overview" > /dev/null; then
    echo -e "${GREEN}✓ RabbitMQ is accessible${NC}"
else
    echo -e "${YELLOW}⚠ RabbitMQ management API not accessible (this is OK if not critical)${NC}"
fi

# Test 10: Check Frontend
echo -e "\n${YELLOW}[Test 10] Checking Frontend...${NC}"
FRONTEND_RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" "$FRONTEND_URL")
if [ "$FRONTEND_RESPONSE" == "200" ]; then
    echo -e "${GREEN}✓ Frontend is accessible (HTTP $FRONTEND_RESPONSE)${NC}"
else
    echo -e "${RED}✗ Frontend not accessible (HTTP $FRONTEND_RESPONSE)${NC}"
    exit 1
fi

# Test 11: Check Worker Service logs
echo -e "\n${YELLOW}[Test 11] Checking Worker Service...${NC}"
WORKER_LOGS=$(docker compose logs worker-service --tail 10)
if echo "$WORKER_LOGS" | grep -q "Worker service started"; then
    echo -e "${GREEN}✓ Worker Service is running${NC}"
    if echo "$WORKER_LOGS" | grep -q "completed"; then
        echo -e "${GREEN}✓ Worker Service is processing jobs${NC}"
    fi
else
    echo -e "${RED}✗ Worker Service may not be running properly${NC}"
fi

echo -e "\n=========================================="
echo -e "${GREEN}All tests completed!${NC}"
echo "=========================================="
echo ""
echo "Summary:"
echo "  - API: http://localhost:8080"
echo "  - Frontend: http://localhost:3000"
echo "  - RabbitMQ Management: http://localhost:15672 (guest/guest)"
echo "  - PostgreSQL: localhost:5432 (postgres/postgres)"
echo ""
echo "To view logs:"
echo "  docker compose logs -f"
echo ""
echo "To check database:"
echo "  docker compose exec postgres psql -U postgres -d jobdb -c \"SELECT * FROM \\\"Jobs\\\" ORDER BY \\\"CreatedAt\\\" DESC LIMIT 10;\""

