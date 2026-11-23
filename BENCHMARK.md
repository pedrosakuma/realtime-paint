# Repeatable local benchmark

This is a provisional protocol for comparing implementations, not a model of human drawing.
It uses a saturated synthetic workload on one machine. Choose the performance objective and
an acceptable tradeoff before judging a change; three runs alone do not establish significance.

## Conditions

- Use the same machine, OS, .NET SDK/runtime, Release build and relevant runtime settings for
  both versions. Record the commit, working-tree changes, `dotnet --info`, OS, logical processor
  count and any GC or tiered-compilation overrides. Note other activity on the machine.
- Start a fresh server with an empty board and no other clients or writers. The server and load
  generator share the machine and contend for its resources.
- Measure without a profiler or MCP collectors attached. Run diagnostics separately and report
  their window; measure instrumentation overhead separately before making claims about it.
- Keep the warm-up, client count, input, event count and cadence identical. Start a fresh server
  for each candidate. The warm-up establishes the board pattern and advances the sequence into
  seven digits; it stays seven digits throughout all three measured runs.
- The slow-client test is a separate scenario, not a directly comparable throughput run.

## Run

From the repository root, build both projects in Release and record the revision:

```powershell
git rev-parse HEAD
git status --short
dotnet --info
[System.Runtime.InteropServices.RuntimeInformation]::OSDescription
[Environment]::ProcessorCount
dotnet build .\RealtimePaint.csproj -c Release
dotnet build .\load\RealtimePaint.Load.csproj -c Release
```

In a separate terminal, start a new server. Choose a free loopback port and change `--base-url`
below if 5089 is already in use; never stop an unrelated process:

```powershell
dotnet .\bin\Release\net10.0\RealtimePaint.dll --urls http://127.0.0.1:5089
```

Confirm `/scenario` reports the PID of this server and zero connected clients, and `/state`
reports sequence zero. Then, from another terminal at the repository root:

```powershell
$output = Join-Path $env:TEMP ("realtime-paint-benchmark-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $output | Out-Null
$common = @("--base-url", "http://127.0.0.1:5089", "--clients", "4",
    "--strokes-per-client", "8192", "--interval-ms", "0")

dotnet .\load\bin\Release\net10.0\RealtimePaint.Load.dll @common `
    --rounds 31 --output "$output\warmup.json"
if ($LASTEXITCODE -ne 0) { throw "Warm-up failed." }

foreach ($run in 1..3) {
    dotnet .\load\bin\Release\net10.0\RealtimePaint.Load.dll @common `
        --rounds 25 --output "$output\run-$run.json"
    if ($LASTEXITCODE -ne 0) { throw "Run $run failed." }
}
```

The warm-up sends 1,015,808 events. Each measured run sends 819,200 events and expects
3,276,800 deliveries. The client verifies ordered delivery and the exact final board; all
three final board hashes must match. Save the four JSON manifests and environment details.
Stop the server in its terminal when finished.

## Interpret

Report each run's elapsed time, events per second, P95/P99 and maximum delivery latency,
delivery count, disconnects, and board hash. The latency is measured from server publication
to client parsing with a same-machine monotonic clock; it is not browser or network round-trip
latency. Percentiles use 0.1 ms buckets and are `null` above 10 seconds.

Compare distributions and correctness, not a single best result. Differences comparable to
normal run-to-run variation are not evidence of improvement. Repeat or alternate baseline and
candidate measurements when needed, and keep diagnostic traces separate. An improvement must
not silently sacrifice event delivery, final state, or an agreed latency objective.
