import http from "k6/http";
import { Counter } from "k6/metrics";

export const options = {
    vus: 1,
    duration: "10s",
};

const BASE = "http://localhost:5050";
const HEADERS = { headers: { "X-Api-Key": "dev-secret-key" } };

const passed = new Counter("requests_passed");
const limited = new Counter("requests_limited");

export default function () {
    // Target /api/b — rate limit is 50/60s, easy to exceed with no sleep
    const res = http.get(`${BASE}/api/b/ping`, HEADERS);

    if (res.status === 200) {
        passed.add(1);
    } else if (res.status === 429) {
        limited.add(1);
        const retryAfter = res.headers["Retry-After"];
        if (!retryAfter) {
            console.warn("429 without Retry-After header");
        }
    }
    // No sleep — hammer as fast as possible to trigger rate limit
}
