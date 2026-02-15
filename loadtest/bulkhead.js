import http from "k6/http";
import { Counter } from "k6/metrics";

export const options = {
    vus: 250,
    duration: "15s",
};

const BASE = "http://localhost:5050";
const HEADERS = { headers: { "X-Api-Key": "dev-secret-key" } };

const passed = new Counter("requests_passed");
const rejected = new Counter("requests_rejected");

export default function () {
    // /slow?ms=2000 holds each request for 2s, filling bulkhead slots
    // max concurrent on /api/a is 200, so 250 VUs will overflow
    const res = http.get(`${BASE}/api/a/slow?ms=2000`, HEADERS);

    if (res.status === 200) {
        passed.add(1);
    } else if (res.status === 429) {
        rejected.add(1);
    }
}
