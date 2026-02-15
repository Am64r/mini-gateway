import http from "k6/http";
import { check, sleep } from "k6";

export const options = {
    vus: 50,
    duration: "30s",
    thresholds: {
        http_req_duration: ["p(95)<200"],
        http_req_failed: ["rate<0.01"],
    },
};

const BASE = "http://localhost:5050";
const HEADERS = { headers: { "X-Api-Key": "dev-secret-key" } };

export default function () {
    const res = http.get(`${BASE}/api/a/ping`, HEADERS);
    check(res, {
        "status is 200": (r) => r.status === 200,
    });
    sleep(0.1);
}
