# AthenaAgent.Ptc — .NET programmatic tool calling prototype

Two independent `net10.0` packages, version `0.1.0-preview.4`:

- `AthenaAgent.Ptc.Core` (`Ptc.Core` namespace): contracts, authorization,
  bounded scheduling, budgets, events, and explicit model-result projection.
- `AthenaAgent.Ptc.JavaScript` (`Ptc.JavaScript` namespace): Jint **4.17.0** runtime.

This is **in-process execution, not a strong sandbox or a security audit**.
The libraries make no model requests and hold no credentials. Hosts supply a tool
snapshot, actual authorization, cancellation-aware tools, artifact ownership, and
durable records. Never expose CLR, filesystem, process, network, or module loaders.

Implementation evidence is maintained in the source repository at
`Libraries/Ptc/IMPLEMENTATION.md`; the source design is not itself test evidence.
No public NuGet publication is part of this prototype. Other target frameworks,
NativeAOT, trimming, and untested platforms are not claimed supported.

## Host integration

Implement `IPtcToolHost` with a bounded tool catalog, pure scheduling decisions, and
an actual cancellation-aware, reauthorizing `InvokeAsync`. Construct a host-owned
`PtcExecutionRequest` (code, object inputs, allowed-tool names, run context, finite
limits), then:

```csharp
using Ptc.Core;
using Ptc.JavaScript;

IPtcExecutor executor = new PtcExecutor(new JintScriptRuntime());
PtcExecutionResult result = await executor.ExecuteAsync(request, host, events, cancellationToken);
string modelJson = PtcModelResultProjector.Project(result, request.Limits.MaxModelResultBytes, traceRef);
```

The host creates `traceRef` only after persisting the immutable result. Never
serialize the full execution result to a model. `ToolRecords` contain complete
business results and arguments, not model-reading summaries. Core never receives
an artifact path or a provider client; artifacts are opaque ID/media/name references.
Default engine execution is off the caller's UI thread. Async I/O is never bridged
with `.Result`, `.Wait`, or `GetAwaiter().GetResult`.

One engine, grant snapshot and budget per run. No persistent globals or concurrent
engine access. Host I/O may overlap. `Serial` is an in-run order barrier: it drains
prior reads and excludes later reads. It is not a cross-run file transaction; a
host with shared writes supplies its own shared locks. Unknown scheduling is serial.
The host rechecks authorization and approval at the actual invocation point.

## JavaScript protocol

Submit an **async function body**, not an IIFE. Only explicit `text(value)` is model
output; returning a value just finishes execution.

```javascript
const listed = await tools.call("list_orders", {});
if (!listed.success) { text({error: listed.errorCode}); return; }
const details = await Promise.all(listed.data.ids.map(id => tools.call("get_order", {id})));
const failed = details.filter(item => !item.success);
if (failed.length) { text({failed: failed.map(item => item.errorCode)}); return; }
const ids = details.map(item => item.data).filter(item => item.overdue).map(item => item.id);
text({count: ids.length, ids});
```

- `inputs`: a detached ordinary JSON object.
- `tools.list()`: authorized names and short descriptions, not the global catalog.
- `tools.describe(name)`: a detached definition/inputSchema, or null if unavailable.
- `await tools.call(name, args)`: JSON object arguments and a native Promise resolving
  to `{success,message,data,errorCode,artifacts}` (camelCase). Read `result.data`.
- `text(value)`: validates and appends explicit JSON output immediately.

Always await tool calls. Detached promises cannot become background work. Both
Promise.all and Promise.allSettled are supported; input order is preserved.
Business denials/failures are result envelopes; budget, timeout and cancellation
stops are irrevocable even if script code catches an exception.

Only losslessly JSON-encodable plain values cross the bridge. Duplicate properties,
excess nesting, invalid Unicode, circular objects, BigInt, undefined/function/symbol
fields, accessors, sparse/extended arrays, nonfinite numbers and unsafe integer
values are rejected rather than silently dropped/coerced. Represent business
integers outside ±(2^53−1) as strings in the tool schema. No arbitrary CLR objects,
Task objects, services, filesystem/network functions or module loaders are exposed.
String compilation (eval and function constructors), reflection and CLR loading
are disabled by Jint configuration, not just by removing global names.

## Limits and outcomes

Defaults are finite and positive: 32,000 code characters; 100,000 statements;
recursion 64; engine allocations 32 MiB; total wall time 30 s; tool including host
approval 10 s; 64 attempts; concurrency 4; pending queue 64; JSON depth 32; input
128 KiB; arguments 64 KiB; one result 512 KiB; run data 8 MiB; explicit output
16 KiB; model projection 24 KiB; 32 unique artifact references. Catalog: at most
128 definitions / 512 KiB. Observer delivery and drain each have a 2 s bound.
The model projection budget must be at least 256 bytes; zero never means unlimited.
Engine allocation limits are **not** a bound on the entire host process's memory.

The first stop reason wins. No new queued tool starts after stop. Before returning,
in-flight work is drained up to DrainTimeout; a noncooperative host is recorded as
`OutcomeUnknown`, not falsely reported as terminated. Hosts retain observation and
cleanup responsibility. Late completions cannot mutate the returned record or a
different run. If an already-executed result is too large, its actual state, raw
byte count, non-delivery flag and reason are retained; the tool is never rerun.
Completed artifacts survive later script failure. There are no automatic retries,
repair, replay, exactly-once guarantees or durable continuation.

Stable failure codes include tool_not_available, tool_denied, invalid_arguments,
schema_unsupported, tool_failed, invalid_code, script_error, call_limit_exceeded,
data_limit_exceeded, output_limit_exceeded, model_output_limit_exceeded,
deadline_exceeded, cancelled, observer_error and host_error. Runtime constraint
codes additionally distinguish statement/recursion/engine-memory limits.
Host exception text/CLR stacks never become script or model diagnostics.
Oversized model projections return whole, parseable limit-error JSON, never a cut
JSON string. Earlier valid explicit output is retained in the execution record.

Events are metadata only with run/parent/call IDs and monotonically increasing
sequence. They are delivered through a bounded channel. Observer failures stop new
work and are visible as observer_error; cleanup does not inherit the cancelled
business token. Host-only records and model results are deliberately separate.
`OccurredTimestamp` is captured by `Stopwatch` when the event happens (not when
the observer receives it); it is comparable only to the same process's monotonic
clock. `ScriptStarted`/`ScriptFinished` bracket actual interpreter execution;
script lifetime includes awaited tool I/O. `CallState` and `RunStatus` report
actual outcomes, including NotStarted and OutcomeUnknown. New fields are additive
to the existing event constructor; no CLR objects or raw arguments are exposed.

## Schema subset

Supports boolean/empty schemas, type (including unions), properties, required,
additionalProperties, items, enum/const, min/max string and array lengths,
minimum/maximum/exclusive bounds, allOf/anyOf/oneOf/not. Names compare ordinally.
Missing additionalProperties allows extras; false closes the object. An empty
schema accepts all otherwise valid JSON. description/title/default/examples/$comment
are annotations; defaults do not inject arguments. Unsupported constraints
(including pattern/format and **all refs**) or malformed schemas fail with
schema_unsupported before any tool executes. This is not full JSON Schema support.

## Build, tests and real package consumption

From the source repository root:

```bash
dotnet restore Libraries/Ptc/Ptc.slnx --locked-mode
dotnet test Libraries/Ptc/Ptc.slnx -c Release --no-restore --logger trx --results-directory artifacts/ptc/test-results
bash Libraries/Ptc/scripts/build-local-feed.sh
bash Libraries/Ptc/scripts/verify-package-consumer.sh
```

StandaloneHost is a source-development convenience, **not** package evidence.
PackageConsumer is copied outside the repository, restores exact PackageReferences
with a fresh dedicated cache and source mapping, verifies nuspec/assets/package and
loaded assembly hashes, runs dependency-chain/denial/cancellation assertions twice,
and retains logs under artifacts/ptc. Verification tooling requires Python 3;
neither library has a Python/Node/Office/runtime shell dependency.

Prototype code/tests have actually run on Fedora 44 Linux x64, SDK 10.0.113,
runtime 10.0.13, Jint 4.17.0. Linux/Windows/macOS CI is configured, but other
platforms are **unverified until their jobs actually pass**. Listed security paths
are tests, not a sandbox escape audit. A successful interpreter or scripted HTTP
loopback says nothing about a real model's code-generation reliability, task
completion rate or provider token usage; those require a separately authorized
model evaluation.
