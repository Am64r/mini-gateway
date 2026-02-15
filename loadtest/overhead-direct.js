import http from "k6/http";

export const options = {
    vus: 1,
    duration: "10s",
};

export default function () {
    // Hit upstream directly, bypassing the gateway entirely
    http.get("http://localhost:5051/ping");
}
