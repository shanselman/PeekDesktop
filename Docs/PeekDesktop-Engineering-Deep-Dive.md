# PeekDesktop Engineering Deep Dive

*Prepared from inspection of the current working tree in `D:\github\PeekDesktop`, plus recent commit history through the `v0.4` baseline and subsequent experimental work.*

> **Status note:** this codebase is currently in motion. The stable behavioral north star is the simplicity and predictability of the `v0.4` era, while the intended **ship/default** experience is now the Explorer-backed **Native Show Desktop** path. Experimental work, especially virtual desktop shell switching, is worth keeping, but it should remain clearly isolated from the default path until manually validated.

---

## 1. Project goal and UX target

PeekDesktop is a tiny Windows tray app that recreates a very specific macOS Sonoma interaction:

- **Click empty desktop wallpaper** -> reveal the desktop immediately
- **Click or drag desktop icons normally** -> do **not** accidentally trigger peek
- **Click a window, the taskbar, or the wallpaper again** -> restore the previous workspace

That sounds simple, but on Windows it sits at the intersection of:

- shell behavior
- global mouse hooks
- focus/foreground-window churn
- accessibility hit testing
- window placement restore
- virtual desktop internals

The product goal is not "do something visually clever to windows." The real goal is:

> **Make "click wallpaper to peek" feel native, predictable, and boringly reliable.**

That framing matters. A mode that is flashy but occasionally restores incorrectly, breaks icon interaction, or feels shell-hostile is not shippable. The desired outcome is a native-feeling desktop reveal, with experimental ideas kept off the mainline unless they earn their way in.

---

## 2. High-level architecture and important files

PeekDesktop is a .NET 10 native Win32 tray application with a very small but well-factored architecture.

### Main runtime pieces

| File | Responsibility | Notes |
|---|---|---|
| `src\PeekDesktop\Program.cs` | Entry point and single-instance mutex | Starts the native `Win32MessageLoop` and defers init until it is pumping |
| `src\PeekDesktop\DesktopPeek.cs` | Core state machine | Orchestrates Idle <-> Peeking transitions and chooses the active peek implementation |
| `src\PeekDesktop\MouseHook.cs` | Explorer-surface mouse detection | Uses `WH_MOUSE_LL`, but only tracks configured desktop/taskbar surfaces |
| `src\PeekDesktop\DesktopDetector.cs` | Desktop/background/icon discrimination | Differentiates wallpaper clicks from icon clicks |
| `src\PeekDesktop\FocusWatcher.cs` | Foreground-window monitoring | Uses `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` to know when to restore |
| `src\PeekDesktop\WindowTracker.cs` | Classic capture/minimize/restore pipeline | Tracks window placement, filters bad candidates, restores Z-order |
| `src\PeekDesktop\NativeMethods.cs` | P/Invoke and interop boundary | Contains the Win32, DWM, shell, MSAA, and input interop surface |
| `src\PeekDesktop\VirtualDesktopService.cs` | Experimental virtual desktop implementation | Uses undocumented shell COM interfaces and build-specific adapters |
| `src\PeekDesktop\TrayIcon.cs` | System tray UX | Mode switching, enable/disable, autostart, about, updates |
| `src\PeekDesktop\Settings.cs` | Persistence | Stores config under `%APPDATA%\PeekDesktop\settings.json` and controls autostart |
| `src\PeekDesktop\AppDiagnostics.cs` | Debug logging | Emits trace + `OutputDebugString` logs for file diagnostics and DebugView |
| `src\PeekDesktop\AppUpdater.cs` | GitHub release update checks | Nice-to-have operational feature, not core peek logic |

### Core design shape

The app is intentionally a tray resident with no main window. That is the right model for a utility that should "just exist" in the background and not feel like a normal document or settings app.

`Program.cs` also uses a named mutex (`Local\PeekDesktop_SingleInstance`) so the app behaves like a proper utility rather than allowing multiple competing hook instances.

---

## 3. How desktop-click detection works

This is the heart of the project. The reliability of the UX depends on correctly distinguishing **empty wallpaper** from **desktop icons** and from everything else.

### Step 1: low-level mouse hook

`MouseHook.cs` installs a global low-level hook using the official Win32 API:

- `SetWindowsHookEx(WH_MOUSE_LL)`
- observe `WM_LBUTTONDOWN`, `WM_MOUSEMOVE`, and `WM_LBUTTONUP`
- call `WindowFromPoint` to get the window under the cursor
- immediately pass through clicks outside enabled Explorer desktop/taskbar surfaces

A subtle but important implementation detail: the hook callback posts heavier work back to the application thread via `Win32MessageLoop.BeginInvoke`.

Low-level hooks must return quickly or Windows may unhook them or make the system feel sluggish. PeekDesktop only performs lightweight surface gating and click-sequence tracking in the hook. Explorer-specific hierarchy, list-view, MSAA, and UI Automation classification runs after the hook has returned. Non-Explorer clicks do not create pending state or classification callbacks.

### Step 2: classify the click target

`DesktopDetector.cs` decides whether the click is:

- `DesktopBackground`
- `DesktopIcon`
- `TaskbarBackground`
- `NonDesktop`

It does this in layers:

1. **Configured Explorer surface check**
   - Verify that the target belongs to `explorer.exe`.
   - Reject desktop or taskbar surfaces when their corresponding trigger is disabled.
   - Ignore ordinary Explorer windows that are not part of the desktop or taskbar.

2. **Desktop ancestry check**
   - Walk up the parent chain looking for:
     - `Progman`
     - `WorkerW` that actually hosts `SHELLDLL_DefView`
   - This avoids treating unrelated `WorkerW` shell helper windows as the real desktop.

3. **List-view hit test for icons**
   - If the click is over `SysListView32`, the code performs a real list-view hit test to determine whether the pointer landed on an icon item or just empty desktop area.

4. **MSAA accessibility fallback**
   - `AccessibleObjectFromPoint` is used as a fallback to inspect the accessibility role at the click point.
   - If the role is `ROLE_SYSTEM_LISTITEM`, the click is treated as an icon click.

5. **Taskbar blank-area check**
   - Known taskbar ancestry is checked first.
   - Windows 11 composition overlays fall back to UI Automation so buttons remain interactive while truly blank space can trigger peek.

This two-layer icon detection is important. `WindowFromPoint` alone is not enough because the desktop surface and the icon list view overlap conceptually.

### Step 3: state machine transition

`DesktopPeek.cs` operates as a tiny explicit state machine:

```text
Idle -> Peeking   when empty wallpaper is clicked
Peeking -> Idle   when a non-desktop window is activated
```

Key fields:

- `_isPeeking`
- `_isTransitioning`
- `_ignoreFocusUntil`
- `_activePeekMode`
- `_nativeShellToggled`
- `_virtualDesktopToggled`

Important behaviors:

- wallpaper click while idle -> enter peek
- wallpaper click while already peeking -> restore
- icon click while idle -> do nothing
- icon click while peeking -> stay peeking
- non-desktop click while peeking -> restore

### Step 4: focus watching and grace period

`FocusWatcher.cs` uses the official Win32 event hook:

- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`

This lets the app know when the user leaves the desktop and enters an application again.

`DesktopPeek.cs` adds the real-world hardening:

- ignore focus churn during transitions
- ignore foreground windows owned by PeekDesktop itself
- treat transient shell UI as "still on desktop" (menus, tooltips, taskbar surfaces)
- use a post-peek grace period to avoid immediately restoring due to focus turbulence caused by minimizing or shell toggling

That grace period is not glamorous, but it is exactly the kind of practical fix that turns a clever prototype into a usable product.

---

## 4. Peek modes over time

PeekDesktop has gone through a healthy but messy experimental phase. The current codebase reflects that history.

### Mode timeline

| Mode | Mechanism | Status | What we learned |
|---|---|---|---|
| **Classic Minimize** | Capture windows, minimize them, restore exact placement | Stable baseline | Predictable and easy to reason about |
| **Fly Away** | Animate windows off-screen with `SetWindowPos` | Experimental | Fun visually, but more complexity and more edge cases |
| **Native Show Desktop (Explorer)** | Ask Explorer to toggle desktop | **Preferred default/ship direction** | Feels most native and offloads behavior to the shell |
| **Virtual Desktop** | Switch to a temporary empty virtual desktop | Experimental and isolated | Surprisingly convincing, but it depends on undocumented shell COM |
| **DWM Cloak** | Attempt to cloak arbitrary windows | Abandoned dead end | Not reliable enough as a ship mechanism |

### Chronology from recent commits

- `eb660a6` (`v0.4`) - last clean, dependable baseline
- `4881715` - fly-away animation experiment
- `05a6676` - Win+D + DWM cloak experiments
- `1b8e4da` - live peek-style switching
- `7ebd4c8` - Explorer-backed Show Desktop implementation
- `88d6764` - default moved to Explorer Show Desktop
- `d7a882e`, `dd96964` - diagnostics improved for DebugView / release troubleshooting

The important product management lesson is simple:

> **The default path should optimize for predictability, not novelty.**

That means the project can absolutely keep interesting experiments, but they must stay explicitly labeled and cleanly separated from the stable behavior.

---

## 5. Why DWM cloak was attempted - and why it does not work well for arbitrary windows

This is one of the most valuable lessons in the codebase.

### Why the idea was attractive

If the goal is "show the desktop without minimizing windows," DWM cloaking sounds perfect in theory:

- no minimize animation
- no window placement loss
- potentially instant reveal
- maybe closer to the macOS illusion

That logic drove an earlier experiment. The historical code shows:

- a legacy `PeekMode.Cloak = 3`
- `TrySetWindowCloak(hwnd, bool)` calling `DwmSetWindowAttribute`
- `WindowTracker.CloakAll()` attempting to cloak tracked windows and fall back to minimize if it failed

You can still see the aftermath in current code:

- `Settings.cs` maps legacy mode `3` back to `NativeShowDesktop`
- `NativeMethods.IsWindowCloaked()` remains, but only as a **query** for filtering windows already hidden by the system

### Why it was ultimately the wrong tool

The key distinction is this:

- **Official/public and useful:** querying whether a window is already cloaked (`DWMWA_CLOAKED`)
- **Not a good general app contract:** trying to use DWM cloak as a reliable way to hide arbitrary third-party top-level windows

In practice, arbitrary-window cloaking is a poor foundation for this app because:

1. **It is not a well-supported general-purpose UX API for desktop apps**
   - Windows exposes cloak state, but arbitrary third-party cloaking is not a stable, well-documented "you can safely build your app around this" feature.

2. **Behavior varies by app type and shell state**
   - different window technologies can behave differently
   - some windows will not cloak cleanly
   - fallback behavior becomes necessary, which defeats the simplicity of the idea

3. **Focus/activation gets weird quickly**
   - hiding a window visually is not the same as integrating correctly with Explorer's desktop/show-desktop semantics
   - restore and interaction behavior can become inconsistent

4. **It increases debugging cost for little ship value**
   - if the happy path already requires "cloak if possible, otherwise minimize," the design is no longer simple or trustworthy

### The lesson to preserve

DWM cloak was a useful investigation, but it is best treated as a **dead end for the primary implementation**.

The repo's current direction reflects the right conclusion:

> Use DWM cloak **passively** (to recognize windows Windows has already cloaked), not **actively** as the main reveal mechanism for arbitrary apps.

---

## 6. Explorer Show Desktop implementation details and why it is preferred

This is the implementation the project should treat as the default ship path.

### How it works today

`NativeMethods.TryToggleDesktop()` attempts two mechanisms in order:

1. **Preferred:** synthesize **Win+D** with `SendInput`
2. **Fallback:** `Shell.Application.ToggleDesktop`

### Recent native-show-desktop regression and fix

The most important regression in this cycle was deceptively small: the `INPUT` interop definition used for `SendInput` only modeled keyboard input, not the full Win32 union (`MOUSEINPUT`, `KEYBDINPUT`, `HARDWAREINPUT`).

That made `Marshal.SizeOf<INPUT>()` wrong for the platform ABI, so Win+D injection could fail and the app would fall back to shell COM more often than expected. The fix was:

- define the full `INPUT` union correctly
- mark LWIN key events as extended-key input
- add explicit failure logging (`sent=n/N`, `lastError`) for `SendInput`

After that fix, logs show the native path activating through Win+D as intended again.

From `DesktopPeek.cs`:

- when `PeekMode.NativeShowDesktop` is active, the app calls `NativeMethods.TryToggleDesktop()`
- if successful, it does **not** keep a custom restore list for that transition
- instead, it remembers that the shell was toggled (`_nativeShellToggled = true`)
- restore later is simply another shell desktop toggle

### Why Explorer-backed behavior is better than custom window management

When Explorer owns the show-desktop transition, PeekDesktop benefits from native shell semantics:

- correct integration with Windows' own notion of "show desktop"
- less risk of getting maximized state or Z-order wrong
- fewer special cases for Win32 vs WPF vs Electron vs UWP-ish windows
- behavior that already feels familiar to Windows users

It is also architecturally cleaner:

- PeekDesktop becomes a **trigger and state coordinator**, not a fragile window-hiding engine
- the shell does the shell work
- the app's custom logic can stay focused on click detection, state transitions, and graceful restore triggers

### API classification

This area deserves nuance:

- **`SendInput` / Win+D path** - official Win32 input API, but still synthetic input
- **`Shell.Application` COM automation fallback** - publicly accessible shell automation surface and practical to use, but it is still effectively delegating to Explorer-owned behavior rather than a dedicated modern SDK API

That makes it a good fit for a utility like PeekDesktop: it is close to the platform's native behavior without forcing the app to manually re-implement the shell.

### Why this mode should stay the default

If the project ships one experience confidently, it should be this one:

> **Explorer-backed Native Show Desktop first; classic minimize as the boring fallback; experiments opt-in only.**

---

## 7. Virtual desktop experiment details, including undocumented shell COM APIs and risks

This is the most interesting experimental feature in the repo, and it is also the one that needs the clearest warning label.

### What it does

Instead of minimizing or hiding windows, the app tries to switch the user to a temporary empty virtual desktop, creating the impression that the wallpaper has been fully revealed.

In `VirtualDesktopService.cs`, the flow is roughly:

1. initialize the immersive shell COM services
2. obtain the current desktop and record its GUID
3. find or create a dedicated temporary peek desktop
4. optionally name it `PeekDesktop (Experimental)`
5. switch to it with animation
6. later, switch back to the original desktop by GUID
7. clean up the temporary desktop on dispose when possible

### What APIs it uses

This code is explicitly built on **undocumented/internal shell COM**, including:

- `CLSID_ImmersiveShell`
- `CLSID_VirtualDesktopManagerInternal`
- `IVirtualDesktop`
- `IVirtualDesktopManagerInternalPre24H2`
- `IVirtualDesktopManagerInternal24H2`
- methods like `SwitchDesktopWithAnimation`, `CreateDesktop`, `RemoveDesktop`, and `SetDesktopName`

These are **not** part of a stable, officially supported public Windows SDK contract for third-party app behavior.

### Why the implementation is more defensive than it first appears

The code already shows respect for that risk:

- it probes the Windows build number
- it uses separate adapters for pre-24H2 and 24H2 signatures
- it performs a smoke test before accepting an adapter
- it falls back out of virtual desktop mode if initialization or switching fails
- `DesktopPeek.cs` degrades from `VirtualDesktop` back to `NativeShowDesktop` if needed

That is good engineering. It acknowledges that the contract is unstable and treats the feature as opportunistic rather than guaranteed.

### Why it must remain isolated

This experiment is worth keeping because it is genuinely interesting and, when it works, quite compelling. But it must stay isolated because the risks are real:

- interface signatures may change across Windows releases
- undocumented behavior can regress without notice
- a Windows update can silently break the code path
- focus and shell restore behavior can vary by build
- cleanup/removal of a temporary desktop must succeed cleanly or the UX degrades

So the right product stance is:

- keep it in the tray menu
- label it **Experimental**
- never let it destabilize the default Explorer-backed behavior
- design all failure paths to fall back safely

---

## 8. Diagnostics and the DebugView workflow

For a tray utility with no console window, logging quality matters a lot.

Recent work improved diagnostics so release builds emit useful traces via `OutputDebugString`, not just debugger-only messages.

### Current diagnostics stack

`AppDiagnostics.cs` writes every log line to:

- `Trace.WriteLine(...)` (persisted local file listener)
- `OutputDebugString(...)` (live DebugView stream)

`Program.cs` clears default trace listeners before attaching the file listener, which prevents duplicate/triple emission while keeping both persistent and live diagnostics.

Messages are prefixed with timestamps and categories such as:

- normal lifecycle logs
- window descriptions
- benchmark/metric logs (`BENCH`)

Examples include:

- mouse hook installed/uninstalled
- click target and click classification
- foreground change events
- native show desktop activation
- capture/minimize/restore timings
- virtual desktop initialization failures

### Why this was important

Many of PeekDesktop's hardest bugs are:

- timing-sensitive
- focus-sensitive
- shell-sensitive
- more visible in release builds than under an attached debugger

Improving DebugView visibility was therefore a major quality-of-life improvement. It gives the engineer a way to inspect real user behavior without adding UI clutter or a permanent log file dependency.

### Typical debugging workflow

1. Launch **Sysinternals DebugView**
2. Filter for `PeekDesktop`
3. Start the app normally
4. Reproduce a scenario:
   - click empty wallpaper
   - click or drag a desktop icon
   - click the taskbar
   - click back into an app
5. Watch the event sequence:
   - click classification
   - state transition
   - focus churn
   - restore cause

For this project, DebugView is not optional sugar. It is effectively the primary field-debugging tool.

---

## 9. Packaging and release direction

The current packaging direction is sensible and pragmatic.

### What the repo is doing now

The GitHub Actions workflow publishes:

- `win-x64`
- `win-arm64`

as:

- **self-contained**
- **single-file**
- zipped artifacts containing `PeekDesktop.exe` and the README

It also supports Azure Trusted Signing when the relevant secrets or variables are configured.

### Why the project moved away from AOT for now

There was a reasonable attempt to prepare for Native AOT, but the current repo direction has moved back toward self-contained single-file publishing. That is the right tradeoff for now because PeekDesktop depends on things that are awkward in AOT-heavy scenarios:

- COM interop with shell automation
- dynamic invocation / reflection-based shell calls
- MSAA accessibility probing for icon hit testing
- experimental shell COM for virtual desktops

The code even contains a direct clue in `NativeMethods.cs`:

- if dynamic code is not supported, accessibility hit testing is skipped
- that is a strong sign that AOT can interfere with the app's most important interaction correctness

So the current decision is sound:

> Prefer a reliable, easy-to-run **self-contained single EXE** over a more fragile AOT story.

### Signing and zip discussion

The present shipping format is also good for a small utility:

- no MSI required
- no service install
- no complex updater agent
- just download, unzip, and run

That matches the product well. An installer might be reasonable later, but only after the app's shell behavior is fully stable.

### Operational release policy

One critical non-technical rule should remain explicit:

> **Do not push tags or publish releases until the user has tested and approved the build.**

The repo already has automation for signed tagged releases, but human validation should gate that process.

---

## 10. Known pitfalls and regressions - and what they taught us

The codebase has already taught a number of good engineering lessons.

### 1. `WindowFromPoint` is not enough

**Symptom:** icon clicks sometimes look like wallpaper clicks.

**Lesson:** the desktop is not a single simple surface. Reliable behavior requires class-name checks, list-view hit testing, and an accessibility fallback.

### 2. Low-level hook callbacks must be fast

**Symptom:** hook-based apps can become flaky or sluggish if they do too much in the callback.

**Lesson:** capture minimal data in the hook, then bounce real work to the UI thread.

### 3. Focus changes are noisy during shell transitions

**Symptom:** the app can immediately restore after entering peek because foreground focus churns.

**Lesson:** a small grace period and explicit filtering of transient shell windows are necessary for good UX.

### 4. Restore order matters

**Symptom:** windows can come back with the wrong layering if restored naively.

**Lesson:** restoring bottom-to-top helps preserve perceived Z-order.

### 5. DWM cloak was a seductive dead end

**Symptom:** "hide without minimize" sounds ideal but does not behave robustly for arbitrary third-party windows.

**Lesson:** platform-adjacent tricks are not the same as supported platform contracts.

### 6. Experiments create blast radius if they are not isolated

**Symptom:** interesting branches can accidentally complicate the default experience.

**Lesson:** stable path and experimental path must be kept visibly separate in both code and UI.

### 7. Packaging choices affect runtime behavior

**Symptom:** AOT-related constraints can quietly reduce correctness in accessibility or COM-driven code.

**Lesson:** for this project, packaging is not just a deployment topic; it directly affects whether the app behaves correctly.

---

## 11. Guidance for a new engineer picking up the codebase

If you are new to PeekDesktop, approach it in this order.

### First, understand the product contract

Do not start with COM trivia or animation polish. Start with the UX contract:

- wallpaper click reveals desktop
- icon interaction remains normal
- restore is predictable and quick
- default behavior feels native

If a change makes that contract less reliable, it is probably the wrong change.

### Second, read the code in this order

1. `Program.cs`
2. `DesktopPeek.cs`
3. `MouseHook.cs`
4. `DesktopDetector.cs`
5. `FocusWatcher.cs`
6. `WindowTracker.cs`
7. `NativeMethods.cs`
8. `VirtualDesktopService.cs`
9. `TrayIcon.cs`
10. `Settings.cs`

That order mirrors the actual runtime flow.

### Third, use DebugView early

Before making any nontrivial change:

- run the app
- capture `PeekDesktop` logs in DebugView
- reproduce the issue once
- understand whether it is a **click classification**, **focus churn**, **shell toggle**, or **window restore** problem

That will save hours.

### Fourth, keep the code mentally split into two buckets

#### Stable bucket

- click detection
- desktop/icon discrimination
- focus watcher
- Explorer-backed native show desktop
- classic minimize fallback

#### Experimental bucket

- fly-away animation
- virtual desktop shell switching
- any future visual tricks

That separation is the architectural discipline the repo needs most right now.

### Fifth, know what is official vs risky

| API / mechanism | Status |
|---|---|
| `SetWindowsHookEx`, `SetWinEventHook`, `EnumWindows`, `GetWindowPlacement`, `ShowWindow`, `SendInput` | **Official Win32** |
| `AccessibleObjectFromPoint` / MSAA | **Official, older accessibility API** |
| `Shell.Application.ToggleDesktop` | **Public shell automation surface; practical, but Explorer-owned behavior** |
| `DwmGetWindowAttribute(DWMWA_CLOAKED)` | **Official query API** |
| `DwmSetWindowAttribute` as an arbitrary third-party window cloaking strategy | **Not a good supported foundation for this app** |
| `IVirtualDesktopManagerInternal` and related immersive shell COM | **Undocumented/internal** |

That table should guide your risk assessment before every change.

### Finally: accept that manual validation is essential

There is currently no dedicated automated test suite in the repo. `dotnet build` and publish validation are necessary, but not sufficient. Manual testing on real desktop scenarios is part of the engineering process here.

---

## 12. Recommended next steps to stabilize and ship safely

This is the path I would recommend.

### 1. Treat `Native Show Desktop (Explorer)` as the intended default

Keep the default path simple and shell-native. That is the best match for the product goal.

### 2. Keep `Classic Minimize` healthy as the fallback safety net

If Explorer toggle fails for any reason, the app should still degrade gracefully to the boring baseline.

### 3. Keep `Virtual Desktop` explicitly experimental

Do not let undocumented shell COM become an invisible dependency of the default user experience.

### 4. Do not revive DWM cloak as a ship path

Document it, learn from it, and move on.

### 5. Focus testing on the real regressions

Build a manual validation checklist for at least:

- empty wallpaper click
- second wallpaper click to restore
- click and drag icons
- right-click desktop context menu
- click taskbar while peeking
- maximized windows
- multi-monitor setups
- Win10 and Win11
- Explorer restart behavior
- `Start with Windows` behavior
- x64 and ARM64 release packaging

### 6. Keep diagnostics on by default in release-friendly form

The recent DebugView improvements should remain; they are disproportionately valuable for a tray utility.

### 7. Ship only after manual approval

The repo's automation can build, sign, zip, and release, but it should not be used as a substitute for the final human check.

### Recommended shipping stance

If a release were being prepared after validation, the right public posture would be:

- **default:** Explorer-backed Native Show Desktop
- **fallback:** classic minimize
- **experimental:** fly-away and virtual desktop
- **not supported / retired:** arbitrary DWM cloak

That keeps the product honest and the codebase maintainable.

---

## Closing summary

PeekDesktop is a deceptively small utility with real shell-integration depth. The core lesson from the recent round of work is that the app succeeds when it leans into:

- precise click detection
- conservative state management
- native Explorer behavior
- strong diagnostics
- careful isolation of experiments

The most important architectural decision going forward is not "what is the cleverest way to hide windows?" It is:

> **How do we preserve the delight of the feature while keeping the default implementation simple, native-feeling, and safe to ship?**

That answer, at least for now, is: **Explorer-backed show desktop by default, experiments clearly labeled, and manual validation before any release.**
