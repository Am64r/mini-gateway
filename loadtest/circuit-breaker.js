import http from "k6/http";
import { Counter } from "k6/metrics";
import { sleep } from "k6";

// Scenario 1: trip the circuit with 100% failures
// Scenario 2: wait for cooldown, then send healthy requests to recover
export const options = {
    scenarios: {
        trip: {
            executor: "constant-vus",
            vus: 5,
            duration: "10s",
            exec: "tripCircuit",
        },
        recover: {
            executor: "constant-vus",
            vus: 2,
            duration: "10s",
            startTime: "20s", // start after cooldown (10s) + buffer
            exec: "recoverCircuit",
        },
    },
};

const BASE = "http://localhost:5050";
const HEADERS = { headers: { "X-Api-Key": "dev-secret-key" } };

const failures = new Counter("upstream_5xx");
const circuitOpen = new Counter("circuit_open_503");
const recovered = new Counter("recovered_200");

export function tripCircuit() {
    // 100% failure rate — every request returns 5xx
    const res = http.get(`${BASE}/api/a/fail?rate=1.0`, HEADERS);

    if (res.status >= 500 && res.status !== 503) {
        failures.add(1);
    } else if (res.status === 503) {
        circuitOpen.add(1); // circuit is open — gateway didn't even try upstream
    }
    sleep(0.5);
}

export function recoverCircuit() {
    // healthy endpoint — should close the circuit
    const res = http.get(`${BASE}/api/a/ping`, HEADERS);

    if (res.status === 200) {
        recovered.add(1);
    } else if (res.status === 503) {
        circuitOpen.add(1); // still open, waiting for half-open test
    }
    sleep(1);
}
