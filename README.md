# Mini API Gateway

A learning-focused API gateway for understanding routing, concurrency, and systems concepts.

## Request Flow

```mermaid
sequenceDiagram
    participant C as Client
    participant G as Gateway
    participant U as Upstream (ServiceA/B)

    C->>G: request (/api/a/* or /api/b/*)
    G->>G: route match + auth check + rate limit + timeout budget
    G->>G: ensure X-Correlation-Id
    G->>U: forward (streaming)
    U-->>G: response (streaming)
    G-->>C: response (+ X-Correlation-Id)
```

### Decision flow (auth + rate limit + timeout)

```mermaid
flowchart TD
    R[Request arrives at Gateway] --> M{Route match?}
    M -- no --> N[404]
    M -- yes --> A{Anonymous path?}
    A -- yes --> RL{Under rate limit?}
    A -- no --> K{Valid X-Api-Key?}
    K -- no --> U401[401]
    K -- yes --> RL
    RL -- no --> U429[429 + Retry-After]
    RL -- yes --> F[Forward upstream]
    F --> T{Finished before timeout?}
    T -- no --> U504[504 + cancel upstream]
    T -- yes --> OK[Return upstream response]
```

### Response codes (gateway)

| Status | Meaning |
|---|---|
| 401 | Missing/invalid `X-Api-Key` (non-anonymous route) |
| 404 | No matching route prefix |
| 429 | Client exceeded per-route rate limit (includes `Retry-After` header) |
| 504 | Upstream exceeded per-route timeout budget |

## Architecture

```mermaid
flowchart LR
    subgraph Clients
        C1[Client]
    end

    subgraph Gateway[Gateway :5050]
        R[Route Matcher]
        A0[Auth + Admission]
        RL[Rate Limiter]
        T[Timeout + Cancellation]
        P[Proxy Handler]
    end

    subgraph Upstreams
        A[ServiceA :5051]
        B[ServiceB :5052]
    end

    C1 -->|/api/a/*| R
    C1 -->|/api/b/*| R
    R --> A0 --> RL --> T --> P
    P -->|/api/a/* → /*| A
    P -->|/api/b/* → /*| B
```

## Key Concepts

### Hop-by-Hop Headers
Headers that describe the connection between two adjacent nodes, not the end-to-end request:
- `Connection`, `Keep-Alive` — connection settings for this hop only
- `Host` — must be set to upstream's host
- `Transfer-Encoding` — gateway may re-chunk

### Streaming vs Buffering
```
Buffering:  Client ──[100MB]──► Gateway ──[100MB in RAM]──► Upstream
Streaming:  Client ──[chunk]──► Gateway ──[chunk]──► Upstream (repeat)
```
We use `HttpCompletionOption.ResponseHeadersRead` + `StreamContent` to stream both directions.

### Cancellation Propagation
`ctx.RequestAborted` fires when:
- Client disconnects
- Request times out
- Server shuts down

Passed to `SendAsync` and `CopyToAsync` to stop wasted work.

### Correlation IDs
The gateway ensures every request has an `X-Correlation-Id`:
- If client provides one, we propagate it upstream.
- Otherwise the gateway generates one.
- The gateway echoes it back in the response header so you can grep logs across services.

### Timeouts (per route)
Each route has its own timeout budget. On timeout, the gateway returns `504 Gateway Timeout` and cancels the upstream request.

### Authentication (API key)
The gateway enforces a simple API key at the edge:
- **Header**: `X-Api-Key: <value>`
- **Config**: set `API_KEY` in `.env`
- **Anonymous allowlist**: per-route `AllowAnonymousPrefixes` (e.g. `["/health"]`)
- **Important**: the gateway should **not forward** `X-Api-Key` to upstreams (avoid credential leakage via upstream logs/bugs).

### Rate Limiting (per route, per client IP address)
Fixed-window counter using `ConcurrentDictionary` + `Interlocked.Increment`:
- **Key**: `{routePrefix}:{clientId}` — authenticated clients keyed by API key, anonymous by IP
- **Config**: `RequestsPerWindow` and `Window` per route (via `.env`)
- **Response**: `429 Too Many Requests` with `Retry-After` header (seconds until window resets)
- **Trade-off**: fixed-window is approximate under concurrency (off by a few requests at window boundaries) — acceptable for a gateway protecting upstreams from overload.

## Setup

```bash
# Copy env template and adjust if needed
cp .env.example .env
```

## Running

```bash
# Terminal 1: Gateway (reads .env automatically)
dotnet run --project src/Gateway

# Terminal 2: ServiceA  
dotnet run --project src/ServiceA

# Terminal 3: ServiceB
dotnet run --project src/ServiceB
```

## Testing

```bash
# Through gateway → ServiceA
curl -H "X-Api-Key: dev-key" http://localhost:5050/api/a/ping
curl -H "X-Api-Key: dev-key" http://localhost:5050/api/a/slow?ms=1000
curl -H "X-Api-Key: dev-key" http://localhost:5050/api/a/fail?rate=0.5

# Through gateway → ServiceB
curl -H "X-Api-Key: dev-key" http://localhost:5050/api/b/ping
```

## Milestones

- [x] **M0**: Skeleton & dev loop
- [x] **M1**: Reverse proxy routing
- [x] **M2**: Correlation IDs & logging
- [x] **M3**: Timeouts & cancellation
- [x] **M4**: Authentication
- [x] **M5**: Rate limiting (429)
- [ ] **M6**: Concurrency bulkhead (429)
- [ ] **M7**: Retries (safe methods only)
- [ ] **M8**: Circuit breaker
- [ ] **M9**: Observability
- [ ] **M10**: Load testing & analysis
