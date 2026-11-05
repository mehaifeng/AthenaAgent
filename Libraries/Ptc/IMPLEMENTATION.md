# PTC implementation evidence

## Baseline / M0

- Branch: `feat/ptc-nuget-prototype`, base `cc31679`.
- Pre-existing user changes: `AGENTS.md`, `CLAUDE.md`, and the untracked design
  `Docs/PTC_NuGet_Implementation_Design_CN.md`; preserved, not discarded.
- Platform: Fedora 44 Linux x64; SDK 10.0.113; runtime 10.0.13.
- NuGet flat-container name checks (2026-11-04): `Ptc.Core`, `Ptc.JavaScript`,
  `AthenaAgent.Ptc.Core`, `AthenaAgent.Ptc.JavaScript` returned HTTP 404.
  Fixed IDs: **AthenaAgent.Ptc.Core**, **AthenaAgent.Ptc.JavaScript**.
  This is an availability observation, not a reservation of public names.
- Version: 0.1.0-preview.1; target net10.0; Jint pinned 4.17.0.
- Actual code matches the documented Registry / request-snapshot / AsyncLocal
  execution boundaries. `read_system_file` also needs `oneOf`, `anyOf`, `not`;
  the Core schema subset must implement these rather than ignore them.
- No production model requests, user credentials, user NuGet configuration,
  public publishing, deployment, or push are authorized or performed.

## Stage status

M0–M6 prototype delivery: verified on the Linux environment above, subject to
the explicit unverified platform/model/security boundaries below.

### M0 commands

- `dotnet restore Libraries/Ptc/Ptc.slnx --use-lock-file --nologo`: exit 0;
  four independent projects restored, lock files generated.
- `dotnet build Athena.UI.csproj -p:UseAppHost=false --no-restore --nologo -v q`:
  exit 0, no component C# included in Athena. One pre-existing CS1734 XML
  comment warning in MainConversationViewModel, not changed as part of PTC.

### M1 / M2 commands and negative evidence

- `dotnet test Libraries/Ptc/Ptc.slnx -c Release --no-restore --logger trx
  --results-directory artifacts/ptc/test-results`: initial exit 0, **36 Core + 38
  JavaScript = 74 executed**; final expanded suite exit 0, **57 Core + 38
  JavaScript = 95 executed, 0 skipped** (`m6-delivery*.trx`).
- `PTC_ASSERTION_PROBE=1 dotnet test Libraries/Ptc/tests/Ptc.Core.Tests/Ptc.Core.Tests.csproj
  -c Release --no-build --no-restore --filter FullyQualifiedName~TestHarnessFailureProbe`:
  exit **1**, 1 discovered/executed failing assertion. TRX:
  `artifacts/ptc/test-results/m1-failing-probe.trx`.
- The first JavaScript run was red on two cases: the test's lone Unicode
  surrogate was accidentally encoded by the C# attribute, and Jint 4.17 wraps
  parser errors in `ScriptPreparationException`. The Unicode fixture now uses a
  raw JavaScript escape, and that exact wrapper maps to `invalid_code`/Rejected.
  Both cases pass. This was not fixed by loosening their assertions.
- Built-in runtime constraint tests cover loops both before and after await,
  recursion, engine allocations, and a script trying to catch a budget stop.
- Concurrency is demonstrated by TaskCompletionSource rendezvous and reverse
  completion, not inferred solely from elapsed milliseconds.

### Public API refinements before consumer installation

- Package IDs use the collision-checked AthenaAgent.Ptc prefix; namespaces stay Ptc.*.
- Limits additionally expose MaxToolDefinitions, MaxCatalogBytes and
  EventDeliveryTimeout (defaults 128, 512 KiB, 2 s).
- Tool records add RawResultBytes, ResultDelivered and DeliveryErrorCode so an
  executed-but-undelivered result is never described as not executed.
- Model projections smaller than 256 bytes are invalid host configuration.
- JSON Schema supports the documented subset plus const, inclusive/exclusive
  number bounds, allOf/anyOf/oneOf/not, and boolean schemas. Unknown constraints
  (including pattern, format and all refs) fail before tool execution.
- Jint/Acornima licenses were checked from the installed nuspec and corresponding
  source commits; full redistribution notices ship with both packages.

Subsequent pre-delivery refinements are recorded rather than silently changing
the protocol: model statuses consistently use camelCase (`timedOut`,
`budgetExceeded`); strict UTF-8 and escaped/raw unpaired-surrogate checks now apply
to JSON values and names, not just the JavaScript encoder. A successfully executed
tool whose JSON cannot be delivered keeps its actual Succeeded outcome and
completed artifact references, with ResultDelivered=false and a delivery error.
Dedicated assertions cover all these cases.

### M3 package-consumer evidence

- `bash Libraries/Ptc/scripts/build-local-feed.sh`: exit 0, locked restore then
  two Release nupkg builds.
- `bash Libraries/Ptc/scripts/verify-package-consumer.sh`: exit 0. A repository-
  external consumer installed only PackageReferences into an initially empty
  dedicated package directory; its 8 assertions passed on each of two runs.
- Checked nuspec ID/version/license/repository/dependencies, net10.0 DLL + XML
  docs, README/license/notices, absence of Athena/test/source/config assets,
  package-cache SHA-512, and loaded DLL SHA-256 against installed package assets.
- Evidence: `artifacts/ptc/package-consumer.log`, `package-consumer.json`,
  `SHA256SUMS`. Only local generation/installation; no publication.
- Validator bugs found and fixed before passing: Core/JavaScript nuspec namespace
  revisions differ, and SDK 10 does not necessarily end packageFolders with a
  separator. Validation now reads the actual namespace and compares canonical
  directory identities (the strict package/cache/assembly checks remain).
- Before Athena restore, both component IDs were absent from its ordinary package
  cache. Recorded in `artifacts/ptc/athena-cache-before.json`. Athena references
  exact nupkg versions through repository-local package-source mapping; user-level
  NuGet configuration is not modified.

## M4 / M5 Athena integration

Implemented in Services/Ptc and Services/Functions/PtcFunctions, with the optional
entry captured directly in OpenAIChatService.RunOneAsync. The required executor,
runtime, lazy Registry factory and trace store resolve through production DI;
there is no nullable PTC dependency hiding a constructor cycle.

- PtcEnabled defaults false and survives configuration serialization. The settings
  page has a localized user toggle. ConfigFieldCatalog cannot read/write it.
- The main model retains direct tools. execute_program is explicitly Sensitive;
  unattended lists, guessed-name calls, and inherited interactive grants cannot
  expose it to subagents or maintenance, even under Off/Trusted modes.
- Five read-only file tools intersect the actual request snapshot. Each child
  re-enters the actual Registry with explicit session/workspace/delegated-task,
  approval and linked cancellation scopes. Live policy changes are reclassified;
  a separate approval-only gate avoids parallel prompts without serializing safe I/O.
- Complete FunctionResult.SerializeRaw data, not BudgetedJson, feeds scripts.
  The ordinary context budget remains active. A 12,000-character raw-result case
  under a 2,000-character ordinary budget retains its tail and accurate aggregate.
- Child progress is a separate metadata callback; transient ProgramCalls and
  ProgramRunId are JsonIgnored. No child assistant/tool protocol carriers exist.
  A cancelled outer call preserves its saved terminal trace/reference through the
  existing interruption-repair path instead of orphaning it or completed artifacts.
- Records reserve capacity before tools, commit atomically, and never overwrite or
  automatically delete history. Per-run/store bounds are 16/256 MiB. ReadAsync
  accepts only opaque host GUID references; a failed save returns no phantom reference.
- New-tool registration also required explicit PolisToolCategories and
  PolisResidents entries. The archive guards failed on each missing registration;
  both were fixed, with no game page, animation or tool-mirroring behavior added.

### Executed commands

| Command (from repository root) | Observed result |
|---|---|
| `dotnet restore Athena.UI.csproj --configfile NuGet.Config --no-http-cache --force --nologo` | exit 0; current local nupkg versions installed |
| `dotnet build Athena.UI.csproj -p:UseAppHost=false --nologo` | exit 0; existing CS1734 XML-comment warning, no errors |
| `dotnet run --project Athena.Archive.Tests/Athena.Archive.Tests.csproj -p:UseAppHost=false` | exit 0; **209 PASS cases** |
| `ATHENA_HEADLESS_ONLY=ptc bash Scripts/run-headless-tests.sh` | exit 0; production DI, Registry, SDK and real HTTP loopbacks; 15 integration groups |
| `bash Scripts/run-headless-tests.sh` | exit 0; **[ALL HEADLESS TESTS PASSED]**, including PTC package/approval/scope/stop/UI checks and existing suites |
| `python3 Libraries/Ptc/scripts/verify_negative_tests.py` | verification exit 0; intentionally broken ordinal authorization in an external copy produces exactly 1 failed test, dotnet exit 1 |
| `bash -n Libraries/Ptc/scripts/build-local-feed.sh Libraries/Ptc/scripts/verify-package-consumer.sh release.sh Scripts/run-headless-tests.sh` | exit 0 |

The final adapter suite has **15 integration groups / 156 assertions**, plus
**4 UI projection assertions**. The final full headless run has **150 PASS cases**
and ends with [ALL HEADLESS TESTS PASSED] (exit 0), including both host-output
license assertions. The mutation's assertion is Assert.Empty(actual host calls),
not a compile/restore failure; the canonical source is never modified.

### Evidence boundaries

- A02/A08 use actual loopback HTTP, production OpenAI SDK, OpenAIChatService and
  FunctionRegistry, both Chat Completions and Responses. The recorded second
  payload has exactly one paired outer call/result and no child protocol messages.
- A10 uses production ConversationArchiveStore and persistence cloning: reopen,
  branch copies and rewind projection keep the same immutable reference without
  rerunning tools. Existing archive/headless rewind/fork suites also pass. No new
  claim of a separately automated UI click-through of every rewind/fork path.
- Normal V0 file tools generate no attachments. Adapter ownership/dedup/forwarding
  and post-failure retention use a synthetic attachment-producing Registry; actual
  file safety uses production FileSystemService, blocklists, symlinks and quotas.
- Test model keys/files are synthetic. No production model, user credential or
  real user document is used. A working interpreter or HTTP 200 is not evidence
  that an actual model reliably generates code or completes tasks.

## M6 packages, platform and limitations

Final local artifacts (source at d98d213, plus the independently tested host adapter):

| Artifact | SHA-256 |
|---|---|
| `artifacts/ptc/feed/AthenaAgent.Ptc.Core.0.1.0-preview.1.nupkg` | `c914198ee6569ad1b3a72070b824542cba5c59e58e19344509844d07fdd82985` |
| `artifacts/ptc/feed/AthenaAgent.Ptc.JavaScript.0.1.0-preview.1.nupkg` | `8f8bb5ce4419ddbdb6ada80b206443860f59d0b9e72a20d3d14453769fc9c152` |

The final consumer runs are in a fresh repository-external directory recorded in
artifacts/ptc/package-consumer.json. Restore is followed by locked-mode restore;
both executable runs pass all 8 assertions. Source mapping, package SHA-512 and
loaded DLL SHA-256 checks are retained. Athena A14 independently checks the actual
project.assets.json, exact PackageReferences, installed archive hash and loaded DLL.
When refreshing the unpublished prototype, only the two exact task-created cache
versions (proven absent before this task, and sourced from this local feed) were
invalidated. No pre-existing package, user NuGet source or credential was removed.

Independent component dependency lock files are committed. Athena did not
previously use a root lock file; a temporary application lock generated during the
first validation was removed rather than pinning the hash of a local preview
archive rebuilt at another commit/platform. Consumer locks are generated for each
fresh feed verification. Change the preview version and references for subsequent
library development instead of mistaking a cached same-version package for new code.

CI now defines .NET 10 component/consumer jobs for Linux, Windows and macOS and
builds the local feed before normal Athena CI/release builds. These workflow edits
were syntax/step-validated locally, **not executed on GitHub**. No release workflow,
NuGet push, git push or deployment was invoked.

| Check | Status |
|---|---|
| Fedora 44 Linux x64 / SDK 10.0.113 / runtime 10.0.13 / Jint 4.17.0 | executed |
| Package creation | executed separately |
| Empty-cache external package consumption, twice | executed separately |
| Production Athena dual-protocol scripted HTTP loopback | executed separately |
| Windows/macOS jobs | configured, **not yet executed/verified** |
| NativeAOT, trimming, other TFMs, sandbox escape audit | not verified |
| Authorized real-model task-completion/usage/performance comparison | **not run**, requires separate model authorization/configuration |
| Public NuGet release, push, deployment | **not performed** |

PTC is in-process execution, not a strong sandbox or a process-wide memory limit.
Noncooperative host work can outlive drain and is OutcomeUnknown; existing file
methods without cancellation-aware I/O cannot be promised to stop instantly.
There is no persistent engine, worker process, Python runtime, transaction,
automatic repair/retry/replay or exactly-once guarantee. Unknown schema constraints
fail closed; this is not full JSON Schema support. No automatic trace reclamation
is provided: disable PTC, preserve related history/artifacts, back up PtcRuns and
have the host operator expand/migrate storage, not silently delete live references.
See Docs/PTC_Prototype_Usage_CN.md for the user switch, query entry and fallback.

Final retained evidence is under artifacts/ptc: components-final.log (95 tests),
archive-final.log (209 cases), headless-final.log (150 cases), athena-build-final.log,
athena-restore-final.log, harness-negative.log (expected exit 1),
authorization-mutation.log, test-results/*.trx, negative-results/*.trx,
package-consumer.log/json, package-inspection.json, verification-summary.json,
the two feed/*.nupkg and SHA256SUMS. Earlier temporary logs were not all retained;
the component/restore/archive commands were rerun into this persistent artifact
directory rather than inventing missing logs. Actual nupkg metadata/file lists and
successful empty-cache consumer evidence independently verify the package step.

Commits are separated by boundary: d5810f2 (standalone Core/Jint), d98d213
(packaging/consumer/CI), followed by the opt-in Athena adapter and its evidence.
The supplied design baseline remains unchanged; its original root-doc links are
preserved and augmented with actual implementation status. No unrelated audio,
shell appearance or historical design was rewritten.

## 2026-11-05: execution timeline event extension

The earlier 0.1.0-preview.1 evidence above is historical; Athena now consumes the
new **0.1.0-preview.4** packages. PtcEvent's existing constructor remains intact,
with additive OccurredTimestamp, CallState and RunStatus metadata. The monotonic
timestamp is taken at event production, before the bounded observer queue; it must
be translated to a host-relative duration in the same process, never persisted as
an absolute Stopwatch value. ScriptStarted/ScriptFinished bracket the interpreter
lifetime separately from PTC reservation and draining, and include awaited I/O.
Late interpreter completion after bounded drain cannot publish into a closed run.
The bounded event queue also accounts for both new script lifecycle events.

The independent component test suite and the external, initially empty-cache
PackageReference consumer are rerun for this new version. The package-consumer
files under artifacts/ptc now describe the latest preview rather than the earlier
preview.1; the historical SHA-256 values above remain evidence of that build, not
the current preview. Actual current hashes live in artifacts/ptc/SHA256SUMS.
No NuGet publication or production model calls are implied by these tests.

Verified on Fedora 44 Linux x64, SDK 10.0.113 / runtime 10.0.13:

- Locked, forced component restore: exit 0. Component tests: **58 Core + 38
  JavaScript = 96 passed**. The delayed-observer test asserts source timestamps,
  not callback arrival times; the intentional authorization mutation still fails.
- Both 0.1.0-preview.4 nupkg files were installed and executed twice from an empty
  external package cache; nuspec, dependency graph and actual DLL hashes checked.
  SHA-256: Core `d407ae41021f4499a4cb2bbb4e87f6e49f762d70213b87a13e7fcf3e6c62677d`,
  JavaScript `643ad0c38157e80b41e5aa6ef093bae7c7f5627d4f011ef9fde2ab69f85da5d6`.
- Archive suite: **210 PASS cases**. Full headless suite: **151 PASS cases**,
  including 18 PTC/timeline integration groups / 205 assertions and actual
  Chat Completions + Responses loopback with production Registry. The headless
  timeline case exercises the real settings toggle, live non-modal window,
  overlapping bars, zoom, keyboard selection, light/dark rendering, opt-out
  and old-history empty state. No real model credentials or requests were used.
- Logs and checksum summary: `artifacts/ptc/timeline-verification-20261105.json`
  and its referenced local logs. Windows/macOS and real-model behaviour have not
  been verified. SDK-internal retries, auxiliary role calls and subagent internals
  are not counted as main-model attempts in the timeline.
