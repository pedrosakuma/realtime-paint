# Real-time shared paint board (viability prototype)

This prototype tests whether a simple collaborative drawing app can support an evidence-led
.NET Diagnostics MCP investigation. It is not a video-ready benchmark and has no measured
performance claims yet.

## Scope and invariants

- One 256 × 256 board, one pencil, and a small fixed color palette.
- One server process with in-memory state; restarting it clears the board.
- A new WebSocket client receives the current board snapshot. Each pointer segment is sent
  immediately, updates the board, and is broadcast to connected clients.
- Horizontal scaling, persistence, accounts, and production security are out of scope.
- An invalid stroke is rejected by closing that client's connection. When its bounded broadcast
  queue fills, a slow client's WebSocket is aborted rather than silently missing updates. A
  reconnecting client receives a fresh board snapshot.

## Run locally

Run the commands in this document from the scenario directory:

```powershell
dotnet run --project .\RealtimePaint.csproj `
  -c Release --urls http://127.0.0.1:5088
```

Open `http://127.0.0.1:5088` in two browser windows. Draw in one and confirm that the other
receives the same strokes. `GET /scenario` reports the process and active-client count;
`GET /state` returns the current in-memory board and sequence.

## Reproducible load and correctness check

Use an isolated local server: the test requires no other clients or writers. It measures
server-publication-to-client-parsing latency using a same-machine monotonic clock, so it
rejects non-loopback URLs. Run:

```powershell
dotnet run --project .\load\RealtimePaint.Load.csproj `
  -c Release -- `
  --clients 4 `
  --strokes-per-client 1000 `
  --rounds 2 `
  --interval-ms 0 `
  --output "$env:TEMP\realtime-paint-load.json"
```

Each normal client draws deterministic two-point, adjacent segments along its own vertical
stripe; different stripes never overlap, and each round repeats the same path. This is a
synthetic workload, not a recording or a validated model of human drawing. All clients connect
before a shared start signal. `clients × strokes-per-client` must fit on the board. Set
`--interval-ms` above zero to request a delay between segments; actual timing depends on the
operating system and is reported as elapsed time rather than assumed.

The client verifies that every normal client receives every event in order and that the final
board exactly matches the expected state, including pixels that existed before the run. The
manifest records workload parameters, elapsed time, broadcast throughput, delivery count, and
local time from server publication to client parsing (P50/P95/P99/max). Percentiles use 0.1 ms
buckets; a percentile above 10 seconds is `null`, while the maximum remains measured. These
are not internet or browser-user latencies, and throughput alone is not a performance result.

### Slow-client scenario

The slow participant receives its snapshot and then stops reading from the socket. The
following command requires the server to evict the slow client and verifies that the socket
actually closes while the normal client continues to receive every event. It then reconnects
the slow client and checks that its snapshot matches the final board:

```powershell
dotnet run --project .\load\RealtimePaint.Load.csproj `
  -c Release -- `
  --clients 1 `
  --slow-clients 1 `
  --strokes-per-client 16384 `
  --rounds 10 `
  --require-slow-disconnect true `
  --output "$env:TEMP\realtime-paint-slow.json"
```

Operating-system and transport buffers affect when backpressure reaches the application. The
test fails explicitly if the selected workload does not fill the server queue or the socket
does not close; it does not pretend that a paused application reader automatically implies
server-side backpressure. Without `--require-slow-disconnect true`, the manifest reports the
observed eviction count but does not assert that a slow client was evicted.

Run the deterministic connection and workload tests with:

```powershell
dotnet test .\tests\RealtimePaint.Tests.csproj -c Release
```

For a fixed warm-up and repeated, uninstrumented comparison workload, see
[`BENCHMARK.md`](BENCHMARK.md).

## Viability questions (not answered yet)

- Can the MCP produce useful, interpretable evidence under representative drawing traffic?
- Does a load level expose more than one naturally occurring, measurable optimization
  opportunity without injecting sleeps, fake CPU work, or forced contention?
- Can successive changes be compared while preserving board state, event delivery, payload,
  client count, and workload?
- Are results stable enough across repeated runs to support a fair narrated comparison?
- Which metrics and synchronization model should form the eventual benchmark contract?

Investigate these questions before selecting a video format, making optimizations, or claiming
that CPU, allocation, or lock contention is a bottleneck.

## License

No license has been granted for this repository.
