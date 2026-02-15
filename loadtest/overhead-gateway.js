import http from "k6/http";

export const options = {
    vus: 1,
    duration: "10s",
};

const HEADERS = { headers: { "X-Api-Key": "dev-secret-key" } };

export default function () {
    // Hit upstream through the gateway — same endpoint, extra middleware
    http.get("http://localhost:5050/api/a/ping", HEADERS);
}
