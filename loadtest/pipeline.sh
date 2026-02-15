#!/bin/bash
# End-to-end pipeline test: healthy → fail → circuit trips → cooldown → half-open → recover
# Shows every resilience layer in action

BASE="http://localhost:5050"
STATUS="http://localhost:5050/gateway/status"
KEY="X-Api-Key: dev-secret-key"
BLUE='\033[0;34m'
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[0;33m'
NC='\033[0m'

step() { echo -e "\n${BLUE}━━━ $1 ━━━${NC}"; }
status() { echo -e "${YELLOW}Circuit: $(curl -s $STATUS | jq -r '.routes["/api/a"].circuitBreaker')${NC}"; }

# ── Phase 1: Healthy traffic ──
step "PHASE 1: Sending healthy requests (expect 200s)"
for i in $(seq 1 3); do
    code=$(curl -s -o /dev/null -w "%{http_code}" -H "$KEY" "$BASE/api/a/ping")
    echo -e "  Request $i: ${GREEN}$code${NC}"
done
status

# ── Phase 2: Failing requests with retries ──
step "PHASE 2: Sending failing requests (100% failure, gateway will retry each 3x)"
echo "  Circuit threshold is 5 — need 5 failed requests to trip"
for i in $(seq 1 6); do
    code=$(curl -s -o /dev/null -w "%{http_code}" -H "$KEY" "$BASE/api/a/fail?rate=1.0")
    if [ "$code" = "503" ]; then
        echo -e "  Request $i: ${RED}$code${NC} ← circuit is OPEN, fail-fast"
    elif [ "$code" = "502" ]; then
        echo -e "  Request $i: ${RED}$code${NC} ← upstream failed after all retries"
    else
        echo -e "  Request $i: ${RED}$code${NC}"
    fi
    status
    sleep 0.2
done

# ── Phase 3: New requests get 503 immediately ──
step "PHASE 3: Circuit is open — new requests should get 503 instantly"
for i in $(seq 1 3); do
    code=$(curl -s -o /dev/null -w "%{http_code}" -H "$KEY" "$BASE/api/a/ping")
    echo -e "  Request $i: ${RED}$code${NC} ← gateway didn't even try upstream"
done
status

# ── Phase 4: Wait for cooldown ──
step "PHASE 4: Waiting for circuit breaker cooldown (10s)..."
for i in $(seq 10 -1 1); do
    printf "\r  %ds remaining..." "$i"
    sleep 1
done
echo ""
status

# ── Phase 5: Half-open — one test request ──
step "PHASE 5: Circuit should be half-open — sending one test request to healthy endpoint"
code=$(curl -s -o /dev/null -w "%{http_code}" -H "$KEY" "$BASE/api/a/ping")
if [ "$code" = "200" ]; then
    echo -e "  Test request: ${GREEN}$code${NC} ← upstream is healthy, circuit CLOSES"
else
    echo -e "  Test request: ${RED}$code${NC} ← still failing"
fi
status

# ── Phase 6: Circuit closed — traffic flows again ──
step "PHASE 6: Circuit recovered — all requests should pass"
for i in $(seq 1 5); do
    code=$(curl -s -o /dev/null -w "%{http_code}" -H "$KEY" "$BASE/api/a/ping")
    echo -e "  Request $i: ${GREEN}$code${NC}"
done
status

step "DONE — full pipeline validated"
