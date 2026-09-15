# Owl animation production sources

The shipped animation is **14 clips / 124 independently drawn frames**, imported to
`Assets/SubAgents/V2`. These sources are not packaged into the application.

## Provenance

Generated with the built-in imagegen tool using `Docs/images/owl-animation-v2-concept.png`
as the character reference. Each source PNG contains consecutive poses of one action,
not unrelated concept poses. Exact prompts are retained in `prompts.json`.
No duplicated frames or cross-fade interpolation were used to increase the frame count.

The shipped frames use the previous owl version's cool graphite / silver palette. The
built-in imagegen tool produced `palette-master-v1.png` from the V2 idle sheet and the
four previous idle sprites; its exact edit prompt is retained in
`palette-master-v1.prompt.txt`. Amber irises remain the only warm owl accent.

## Rebuild

On Windows PowerShell 7 (.NET 10 / System.Drawing):

```powershell
./Scripts/import-owl-sprites.ps1
```

The importer finds connected alpha silhouettes (threshold 24), checks the expected
number of characters, sorts four characters per row, isolates each character's pixels
and its immediate antialias fringe, and places it on a transparent 256 × 256 canvas.
It does **not** crop the source into equal grid rectangles. Small disconnected source
components are omitted and counted in each `*-registration.csv` report. The reviewed
source sheets contain zero such components except one in Land and one in Farewell.

`registration.json` retains per-clip scale, frame anchors and destination anchors.
Walking uses stable row baselines; flight is registered around the face instead of
the changing wing bounds; work and takeoff have individually reviewed anchors.
Scaling is constant within each clip. Non-overlapping silhouettes and a minimum
transparent export margin are required; a merged owl or out-of-canvas registration
fails import rather than guessing a crop. Review exports after changing registration.

Before export, `OwlPaletteTransfer` learns a smooth 17 × 17 × 17 RGB lookup table from
the aligned original idle sheet and `palette-master-v1.png`, then applies that same
mapping to every clip. This keeps all 124 frames color-consistent while leaving alpha,
silhouette, anchors and animation timing unchanged. Re-running the importer therefore
reproduces the cool previous-version palette instead of restoring the warm source color.

The PNGs preserve transparency. Do not delete low-alpha pixels indiscriminately or
remove components at runtime; the renderer should only decode verified independent PNGs.

## Playback and checks

`Models/OwlAnimation.cs` is the authoritative frame timing / loop metadata.
`Controls/OwlFrameLibrary.cs` lazily caches each complete clip for both the village
and the conversation's entry flight. Source artwork totals about 18 MB; shipped PNGs
total about 4.8 MiB. Fully decoded 124 × 256 × 256 RGBA frames require about 31 MiB
before platform texture overhead; only requested clips are loaded.

```powershell
# Focused checks: boundaries, one-shot holds, no identical frames, 8px alpha border,
# retargeting continuity, terminal priority, actual village binding/timer and captures.
$env:ATHENA_OWL_TEST_ONLY = '1'
./Scripts/run-headless-tests.ps1 -OutputPath artifacts/owl-qa/main-window.png
Remove-Item Env:ATHENA_OWL_TEST_ONLY
./Scripts/build-owl-preview.ps1

# Full regression suite
./Scripts/run-headless-tests.ps1 -OutputPath artifacts/owl-qa/main-window.png
```

The full suite's existing Git-restore fixture compares LF bytes. On a Windows host
with global `core.autocrlf=true`, run it with a process-scoped Git override:
`GIT_CONFIG_COUNT=1`, `GIT_CONFIG_KEY_0=core.autocrlf`, `GIT_CONFIG_VALUE_0=false`.
Do not change the user's persistent Git configuration for this fixture.
If a running Athena.UI instance locks the default build output, pass
`-BuildOutputPath artifacts/headless-build` to the same test script.

`Docs/OwlAnimationPreview.html` references the exact shipped PNGs and embeds timing
exported by the headless check. It supports normal/slow playback, light/dark/checker
backgrounds, actual 80px display boxes, and a frame-by-frame contact strip. Open it
locally with the repository in place. It has no remote dependencies.

The rendering clock is a shared, mounted-view-only 16ms DispatcherTimer, while frame
selection is based on monotonic elapsed time. This removes the former 120ms sampling
bottleneck but does not promise render-thread animation during a blocked UI thread.
Tool execution never awaits animation. A fast completed tool leaves a presentation-only
gate that finishes arrival, one coalesced zone action, the return flight and success before
the curtain call; newer tool-zone requests replace older queued destinations rather than
building an unbounded animation backlog.
The runner's automatic Meditation request at the start of the next model round is a
deferred visual destination: it cannot retarget the preceding tool flight before that
tool's landing and zone action have completed.
The source frames are AI-generated; the checked small-size result should still be
visually re-reviewed when changing sprite size or character art direction.
