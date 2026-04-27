# AthenaAgent Headless Browser Implementation Roadmap

## 1. Objective

Introduce a headless browser capability into AthenaAgent that can inspect and operate web pages through visual reasoning.

The browser feature must satisfy four core requirements:

- Use Playwright for .NET as the browser automation layer.
- Support visual mode by sending screenshots to a dedicated vision model.
- Use Set-of-Mark (SoM) annotations to label interactive page elements.
- Isolate browser execution context from the main chat context to prevent intermediate browser tool calls, screenshots, and page state from polluting the user conversation.

## 1.1 Implementation Progress

- [x] Phase 1: Configuration and contracts.
- [x] Phase 2: Playwright runtime foundation.
- [x] Phase 3: SoM observation.
- [x] Phase 4: Internal browser agent.
- [x] Phase 5: Vision model integration.
- [x] Phase 6: Main tool registration.
- [x] Phase 7: Hardening and cross-platform validation.

Latest validation:

- `dotnet build` passed on `BrowserUsing` with 0 errors after Phase 7.
- Existing warnings remain in `ViewModels/ChatTabViewModel.cs` for nullable dialog owner arguments.

## 2. Target Architecture

The main conversation should expose only a high-level browser task function.

Recommended main tool:

```text
run_browser_task(instruction, startUrl?, maxSteps?)
```

The low-level browser operations must stay inside an internal browser agent loop.

Internal browser tools:

- `browser_navigate`
- `browser_observe`
- `browser_click`
- `browser_type`
- `browser_press_key`
- `browser_scroll`
- `browser_wait`
- `browser_extract_text`
- `browser_close`

The main conversation receives only the final task result, key evidence, final URL, and optional screenshot reference. It must not receive every intermediate screenshot, SoM element list, DOM dump, or browser tool trace.

## 3. Component Plan

### 3.1 Configuration Model

Extend `AppConfig` with browser and vision settings.

Browser settings:

- `BrowserEnabled`
- `BrowserHeadless`
- `BrowserUseVisionMode`
- `BrowserViewportWidth`
- `BrowserViewportHeight`
- `BrowserMaxSteps`
- `BrowserOperationTimeoutSeconds`
- `BrowserSessionTtlMinutes`
- `BrowserPersistSession`
- `BrowserDownloadEnabled`
- `BrowserScreenshotScale`
- `BrowserImageQuality`
- `BrowserSomEnabled`
- `BrowserSomMaxElements`
- `BrowserSomIncludeText`

Vision model settings:

- `BrowserVisionProvider`
- `BrowserVisionBaseUrl`
- `BrowserVisionApiKey`
- `BrowserVisionModel`
- `BrowserVisionMaxTokens`
- `BrowserVisionTemperature`

Default rule:

- Browser vision uses an independent provider, `BaseUrl`, and `ApiKey`.
- The UI should match the secondary model and embedding model configuration style.

### 3.2 Config UI

Add a Browser tab under the existing auxiliary cores section in `ConfigTabView`.

Suggested UI groups:

- Enablement: browser enabled, vision mode enabled, headless mode.
- Runtime: viewport, operation timeout, max steps, session TTL.
- Session policy: persist session, allow downloads.
- SoM: enable annotations, max elements, include element text.
- Vision model: provider, endpoint, API key, model ID, max tokens, temperature.
- Diagnostics: test browser runtime, test vision model, test annotated screenshot generation.

### 3.3 Browser Automation Layer

Add:

- `IHeadlessBrowserService`
- `PlaywrightBrowserService`

Responsibilities:

- Initialize and validate Playwright runtime.
- Create isolated browser contexts.
- Navigate pages.
- Execute actions such as click, type, scroll, wait, and key press.
- Capture screenshots.
- Dispose pages, contexts, and browser processes safely.

Important rule:

- Each `run_browser_task` should create a new Playwright `BrowserContext` by default.
- Cookies, localStorage, cache, permissions, and downloads must not be shared with other tasks unless `BrowserPersistSession` is explicitly enabled.

### 3.4 Browser Session Manager

Add:

- `IBrowserSessionManager`
- `BrowserSessionManager`
- `BrowserSession`

Responsibilities:

- Create session IDs.
- Track active browser contexts and pages.
- Apply TTL cleanup.
- Close sessions after task completion.
- Guard against leaked browser processes.

Session data should include:

- `SessionId`
- `CreatedAt`
- `LastAccessedAt`
- `BrowserContext`
- `Page`
- `CurrentUrl`
- `TaskId`
- `IsPersistent`

### 3.5 SoM Annotation Layer

Add:

- `ISomAnnotator`
- `SomAnnotator`
- `SomElement`
- `SomObservation`

Recommended flow:

1. Use Playwright to execute JavaScript and collect interactive elements.
2. Capture a clean page screenshot with Playwright.
3. Use SkiaSharp to draw element boxes and numeric labels on top of the screenshot.
4. Return the annotated image plus structured element metadata.

Do not inject visible overlays into the web page before screenshot unless needed for debugging. DOM overlays can change layout, affect hit testing, and create side effects on complex pages.

Interactive element candidates:

- `a[href]`
- `button`
- `input`
- `textarea`
- `select`
- `[role=button]`
- `[role=link]`
- `[role=menuitem]`
- `[role=tab]`
- `[tabindex]`
- `[contenteditable=true]`
- visible elements with click handlers when detectable

HTML overlays and modal layers:

- Treat DOM-based modal/popover/dialog layers as first-class SoM targets.
- Filter candidates by hit testing with `elementsFromPoint` so visually covered background elements are not marked.
- Prioritize elements inside active dialogs, popovers, and modal roots before normal page elements.
- Traverse open shadow roots when collecting candidates. Iframe traversal remains a separate hardening item because it requires frame-aware coordinate translation.

Element metadata:

- `ElementId`
- `Index`
- `TagName`
- `Role`
- `Text`
- `AriaLabel`
- `Placeholder`
- `Href`
- `BoundingBox`
- `IsVisible`
- `IsEnabled`
- `Selector`

### 3.6 Vision Decision Layer

Add:

- `IBrowserVisionService`
- `BrowserVisionService`

Responsibilities:

- Send annotated screenshot and element metadata to the configured vision model.
- Ask the model to choose the next browser action.
- Parse and validate the returned action.
- Keep the visual decision prompt separate from the main persona prompt.

The vision model should return structured decisions such as:

```json
{
  "action": "click",
  "targetElementId": "som-12",
  "reason": "The element is the visible search button.",
  "confidence": 0.82
}
```

### 3.7 Internal Browser Agent

Add:

- `IBrowserAgentService`
- `BrowserAgentService`
- `BrowserTaskRequest`
- `BrowserTaskResult`

Responsibilities:

- Accept a high-level browser instruction from the main tool.
- Create an isolated browser session.
- Run an internal loop using browser-specific messages and browser-specific tools.
- Decide when to observe, act, retry, or stop.
- Return a compact final result to the main conversation.

Phase 4 implementation note:

- The internal agent has a deterministic action executor and default observe/extract loop.
- Autonomous action selection from annotated screenshots is intentionally deferred to Phase 5.

The internal loop should enforce:

- Maximum step count.
- Per-step timeout.
- Navigation timeout.
- Re-observe after every page-changing action.
- Verified action effects for browser operations that mutate page state.
- Progress guards that stop repeated actions on unchanged page state.
- Stop conditions for captcha, login, 2FA, payment, destructive action, or user confirmation requirement.

Type action semantics:

- `type` means fill/replace the target editable value, not append keystrokes.
- Before and after values should be captured in sanitized action effects.
- If the target already contains the requested value, the action should be skipped and the agent should choose the next step.
- Repeating the same completed action on the same page state should terminate as no progress after one corrective feedback turn.

### 3.8 Main Function Integration

Add:

- `BrowserTaskFunctions`

Register only the high-level function in `FunctionRegistry`.

Main function:

```text
run_browser_task
```

Inputs:

- `instruction`
- `startUrl`
- `maxSteps`

Output:

- `success`
- `summary`
- `finalUrl`
- `evidence`
- `actionsTakenCount`
- `requiresUserInput`
- `error`

Do not register low-level browser tools into the existing main `FunctionRegistry`.

### 3.9 Diagnostics and Runtime Checks

Add startup or settings-page checks for:

- Playwright package availability.
- Playwright browser runtime installation.
- Browser launch success.
- Screenshot capture success.
- SkiaSharp annotation success.
- Vision model connectivity.

Playwright requires browser binaries in addition to the NuGet package. The application should provide a clear error message when browser binaries are missing.

## 4. Dependency Plan

NuGet packages:

- `Microsoft.Playwright`
- `SkiaSharp`
- `SkiaSharp.NativeAssets.macOS`
- `SkiaSharp.NativeAssets.Linux`
- `SkiaSharp.NativeAssets.Win32`

Packaging considerations:

- Verify native asset inclusion for macOS, Windows, and Linux.
- Verify Playwright browser installation flow for packaged desktop apps.
- Decide whether AthenaAgent installs browsers automatically or instructs users to run the Playwright install command.

## 5. Implementation Phases

### Phase 1: Configuration and Contracts

Status: Completed.

Deliverables:

- Add browser-related fields to `AppConfig`.
- Add browser model classes.
- Add service interfaces.
- Add Browser tab layout to `ConfigTabView`.
- Add localized labels for English and Chinese.

Acceptance criteria:

- Config changes persist correctly.
- Config tab can edit browser settings.
- Existing AI, memory, WebSearch, and embedding settings continue to work.

### Phase 2: Playwright Runtime Foundation

Status: Completed.

Deliverables:

- Add Playwright dependencies.
- Implement `PlaywrightBrowserService`.
- Implement `BrowserSessionManager`.
- Add browser runtime diagnostic method.
- Add app-driven Chromium runtime installation.

Acceptance criteria:

- App can launch a headless browser.
- App can create and close isolated browser contexts.
- App can navigate to a URL and capture a screenshot.
- Session cleanup works after normal completion and failure.

### Phase 3: SoM Observation

Status: Completed.

Deliverables:

- Implement JS-based interactive element extraction.
- Implement SkiaSharp screenshot annotation.
- Implement `browser_observe`.
- Add annotation diagnostics.

Acceptance criteria:

- Observation returns annotated image plus element metadata.
- Labels align with visible elements.
- Hidden, disabled, and offscreen elements are filtered correctly.
- DOM modal/dialog/popover contents are labeled ahead of background page elements.
- Background elements covered by HTML overlays are not labeled.
- Open shadow DOM controls are included when they are visible and topmost.
- Type actions are idempotent and verified by reading the target value after fill.
- Repeated completed actions do not consume all browser steps.
- Large pages cap the number of annotated elements.

### Phase 4: Internal Browser Agent

Status: Completed.

Deliverables:

- Implement `BrowserAgentService`.
- Implement browser-specific prompt.
- Implement internal browser action loop.
- Add low-level internal browser functions.

Acceptance criteria:

- Browser task execution does not append intermediate browser messages to the main `ConversationContext`.
- Internal loop can navigate, observe, click, type, and stop.
- Max step and timeout limits are enforced.

### Phase 5: Vision Model Integration

Status: Completed.

Deliverables:

- Implement `BrowserVisionService`.
- Add vision model request construction.
- Add structured action parsing and validation.
- Add retry behavior for invalid model output.

Acceptance criteria:

- Vision model can choose actions from annotated screenshots.
- Invalid action IDs are rejected.
- Browser agent re-observes after page-changing actions.
- Final result is concise and useful to the main conversation.

### Phase 6: Main Tool Registration

Status: Completed.

Deliverables:

- Add `BrowserTaskFunctions`.
- Register `run_browser_task` in `FunctionRegistry`.
- Add dependency injection wiring.
- Add logging around browser task lifecycle.

Acceptance criteria:

- Main model can request a browser task through one high-level tool.
- Main conversation only receives final browser task result.
- Existing tools continue to work.

### Phase 7: Hardening and Cross-Platform Validation

Status: Completed.

Implementation note:

- Baseline hardening is complete: browser screenshots and action history are not returned to the main tool result, sessions close by default, runtime/vision diagnostics are available in settings, and build validation is passing.
- Full manual cross-platform packaged-app validation remains a release validation task.

Deliverables:

- Add integration tests or manual test script.
- Validate macOS, Windows, and Linux behavior.
- Validate packaged app behavior.
- Tune screenshot quality and token cost.

Acceptance criteria:

- Browser runtime errors are actionable.
- Browser processes do not leak.
- SkiaSharp native assets load correctly.
- Common websites can be observed and interacted with.

## 6. Suggested File Layout

```text
Models/
  BrowserConfigModels.cs
  BrowserModels.cs

Services/Interfaces/
  IHeadlessBrowserService.cs
  IBrowserSessionManager.cs
  ISomAnnotator.cs
  IBrowserVisionService.cs
  IBrowserAgentService.cs

Services/Browser/
  PlaywrightBrowserService.cs
  BrowserSessionManager.cs
  SomAnnotator.cs
  BrowserVisionService.cs
  BrowserAgentService.cs
  BrowserPrompts.cs

Services/Functions/
  BrowserTaskFunctions.cs
```

If the project prefers flatter service folders, the files can remain under `Services/`, but a dedicated `Services/Browser/` folder will keep the browser feature easier to maintain.

## 7. Main Prompt and Tool Prompt Changes

Main persona prompt should include only high-level browser guidance:

- Use browser tool when page interaction or visual inspection is required.
- Do not ask for low-level browser operations directly.
- Summarize browser results to the user.

Browser agent prompt should include detailed operating rules:

- Always observe before acting.
- Use SoM element IDs for click and type targets.
- Re-observe after navigation, click, form input that changes page state, and scroll.
- Return structured final output.

## 8. Logging and Observability

Log these events:

- Browser task start and end.
- Session creation and cleanup.
- Navigation target.
- Action type and target element ID.
- Timeout and retry events.
- Screenshot and SoM generation failures.

Do not log:

- Full screenshots.
- Passwords or typed sensitive values.
- Cookies, localStorage, or auth tokens.
- Full page HTML by default.

## 9. Testing Strategy

Unit-level tests:

- Config serialization compatibility.
- SoM element filtering.
- Browser action result parsing.
- Vision action JSON parsing.

Integration tests:

- Launch browser.
- Navigate to a simple static page.
- Extract interactive elements.
- Produce annotated screenshot.
- Click an element by SoM ID.
- Verify isolated sessions do not share cookies.

Manual tests:

- Search engine query.
- Documentation lookup.
- Multi-step form without submission.
- Infinite-scroll page.
- Login page stop condition.
- Captcha stop condition.

## 10. Key Risks

Context pollution:

- Avoided by keeping all browser internals inside `BrowserAgentService`.

Browser process leaks:

- Mitigated through `BrowserSessionManager`, TTL cleanup, and `await using` disposal paths.

High token cost:

- Mitigated by screenshot scaling, image quality settings, max SoM elements, and concise element metadata.

Unreliable element targeting:

- Mitigated by stable element IDs, selector fallback, bounding-box validation, and re-observe after page changes.

Browser autonomy risk:

- Mitigated by keeping browser operations behind an internal browser agent and no persistent session by default.

Cross-platform packaging issues:

- Mitigated by explicit Playwright runtime diagnostics and SkiaSharp native asset validation.

## 11. Recommended First Milestone

The first milestone should not attempt full autonomous browsing.

Scope:

- Add config fields and UI.
- Add Playwright runtime service.
- Add isolated session creation.
- Add navigation and screenshot.
- Add SoM annotation.
- Add a diagnostic action that opens a test page and produces an annotated screenshot.

This milestone proves the browser runtime, session isolation, and visual observation pipeline before introducing an internal autonomous browser agent.
