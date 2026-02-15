import http from "k6/http";
import { check, sleep } from "k6";

// Ramp up to find the breaking point, then ramp down to test recovery
export const options = {
    stages: [
        { duration: "15s", target: 200 },  // warm up
        { duration: "30s", target: 500 },   // moderate load
        { duration: "30s", target: 1000 },  // push hard
        { duration: "15s", target: 0 },     // ramp down — does gateway recover?
    ],
};

const BASE = "http://localhost:5050";
const HEADERS = { headers: { "X-Api-Key": "dev-secret-key" } };

export default function () {
    const res = http.get(`${BASE}/api/a/ping`, HEADERS);
    check(res, {
        "not 5xx": (r) => r.status < 500,
    });
    sleep(0.1);
}
