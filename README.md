# Mini API Gateway

A learning-focused API gateway for understanding routing, concurrency, and systems concepts.

## Request Flow

```mermaid
sequenceDiagram
    participant C as Client
    participant G as Gateway
    participant U as Upstream (ServiceA/B)

    C->>G: GET /api/a/ping
    
    Note over G: 1. Route Matching
    G->>G: TryMatch("/api/a/ping", routes)
    G->>G: Match: /api/a → localhost:5051
    G->>G: Forward path: /ping

    Note over G: 2. Auth gate (edge API key)
    G->>G: IsAnonymousPath(/ping)?
    alt anonymous
        G->>G: Skip auth
    else not anonymous
        G->>G: Validate X-Api-Key
        alt missing/invalid
            G-->>C: 401 Unauthorized
            Note over G: Reject before proxying upstream
        end
    end
    
    Note over G: 3. Build Upstream Request
    G->>G: Copy method (GET)
    G->>G: Filter headers (strip hop-by-hop, strip X-Api-Key)
    G->>G: Ensure X-Correlation-Id (generate if missing)
    G->>G: Wrap body as stream (if present)
    
    Note over G: 4. Send (streaming + timeout + cancellation)
    G->>G: Create linked token (client disconnect OR timeout)
    G->>U: GET /ping
    alt upstream responds in time
        U-->>G: 200 OK + body stream
    else timeout budget exceeded
        G-->>C: 504 Gateway Timeout
        Note over G: Cancel upstream request + stop copying
    end
    
    Note over G: 5. Stream Response
    G->>G: Copy status + headers
    G->>G: Echo X-Correlation-Id to client
    G-->>C: 200 OK + body stream
```

## Architecture

```mermaid
flowchart LR
    subgraph Clients
        C1[Client]
    end

    subgraph Gateway[Gateway :5050]
        R[Route Matcher]
        A0[Auth + Admission]
        T[Timeout + Cancellation]
        P[Proxy Handler]
    end

    subgraph Upstreams
        A[ServiceA :5051]
        B[ServiceB :5052]
    end

    C1 -->|/api/a/*| R
    C1 -->|/api/b/*| R
    R --> A0 --> T --> P
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

## Setup

```bash
# Copy env template and adjust if needed
cp .env.example .env
```

The `.env` file configures upstream URLs and per-route timeouts:
```
UPSTREAM_SERVICE_A=http://localhost:5051
UPSTREAM_SERVICE_B=http://localhost:5052
TIMEOUT_API_A_MS=1500
TIMEOUT_API_B_MS=1500
API_KEY=dev-key
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
- [ ] **M5**: Rate limiting (429)
- [ ] **M6**: Concurrency bulkhead (429)
- [ ] **M7**: Retries (safe methods only)
- [ ] **M8**: Circuit breaker
- [ ] **M9**: Observability
- [ ] **M10**: Load testing & analysis
