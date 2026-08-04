# ZX0ai

A premium multi-agent AI chat client for Windows. WinUI 3 · .NET 8 · Win2D.

Built in phases per `ZX0ai_MASTER_PROMPT.md`. **All phases 0–6 are implemented.**

---

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET 8 SDK | `dotnet --list-sdks` must show `8.0.x` |
| Windows 10 1903+ / Windows 11 | Mica needs Win11; Win10 falls back to Acrylic, then a flat fill |

Visual Studio is **not** required. Everything below runs from the command line.

---

## Build and run

```powershell
dotnet build ZX0ai.sln -c Debug -p:Platform=x64
.\ZX0ai\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\ZX0ai.exe
```

### Deployment modes

The app builds **unpackaged and Windows App SDK self-contained** by default: a plain
`.exe` that runs by double-click, with no Developer Mode, no MSIX registration and no
separately installed Windows App Runtime.

For MSIX (Store / enterprise distribution, packaged-identity APIs):

```powershell
dotnet build ZX0ai.sln -c Release -p:Platform=x64 -p:ZX0aiPackaged=true
```

`Package.appxmanifest` is already wired for that path, including the `microphone`
and `internetClient` capabilities Phases 1–3 need.

---

## Configuration

Shipped defaults live in `ZX0ai/appsettings.json` (copied next to the executable).
Per-user overrides go in `%LOCALAPPDATA%\ZX0ai\appsettings.local.json`, which is
git-ignored and always wins.

### Credentials

**API keys exist only as environment variables.** They are never stored in the app,
never written to any file it owns, never logged, and there is deliberately no input
field for them — the settings pane shows only whether a credential was found.

Each tier names its own variable, so a tier definition stays safe to commit:

| Tier | Variable | Model |
|---|---|---|
| `zxa-very-low-free` | `ZX0AI_KEY_FREE` | `nvidia/nemotron-3-ultra-550b-a55b:free` |
| `zxa-Light` | `ZX0AI_KEY_LIGHT` | `google/gemma-4-31b-it:free` |
| `zxa-medim` | `ZX0AI_KEY_MEDIUM` | team, leader-delegate |
| `zxa-Ultra` | `ZX0AI_KEY_ULTRA` | team, debate-then-synthesize |

Any tier without its own variable falls back to `OPENROUTER_API_KEY`. Set them once:

```powershell
[Environment]::SetEnvironmentVariable('ZX0AI_KEY_FREE','sk-or-v1-...','User')
```

New: a convenience alias/tier `zax-v2` has been added. It maps to the `zax-pro` tier by
default but can also be configured explicitly. If you want a dedicated key for `zax-v2`,
set `ZAX_KEY_V2` in the environment.

Credentials are re-read from the environment on **every request**, so rotating a key
takes effect on the next message rather than the next launch.

### Language

`ui.language` selects the UI language. `en-US` (default) or `ar`.

Switching to `ar` flips the entire shell to `FlowDirection=RightToLeft` — the sidebar
moves to the right, the send button flips, and every string resolves from
`Strings/ar/Resources.resw`. Model slugs, code and terminal output stay LTR in either
direction via explicit `FlowDirection=LeftToRight` islands. Both languages are complete
and verified.

### Orb debug overlay

Set `ui.showOrbDebugOverlay: true` to render the live readout on the orb:

```
Listening  fps  60.0
rms 0.976  raw 0.32161
fft [▂▅▇███▅▂]
```

`rms` is the normalised value that drives the visuals; `raw` is the linear RMS before
normalisation. The two together distinguish a silent room from a dead capture path —
`raw 0.00000` means no signal is reaching the app at all.

### Logs

Diagnostics are written to `%LOCALAPPDATA%\ZX0ai\zx0ai.log` (rolled at 512 KB).
Startup failures also land in `startup.log`.

---

## Phase 0 verify checklist

| # | Check | How |
|---|---|---|
| 1 | Solution builds clean | `dotnet build ZX0ai.sln -c Debug -p:Platform=x64` — 0 warnings, 0 errors, warnings-as-errors on |
| 2 | Three projects wired | `ZX0ai` (UI) → `ZX0ai.Core` (transport-agnostic) ; `ZX0ai.Backend` → `ZX0ai.Core` |
| 3 | App launches | Window opens, shell mounts, no entry in `%LOCALAPPDATA%\ZX0ai\startup.log` |
| 4 | Orb runs at 60fps | Enable the debug overlay; it reads `fps 60.0` |
| 5 | Orb is Idle | Breathing ±3%, slow swirl, soft bloom, chromatic rim |
| 6 | Reduced motion honoured | Set `ui.reducedMotion: true` — orb settles to a static frame and the render loop pauses |
| 7 | Theme tokens applied | No colour, radius, spacing or duration literal appears in any view |
| 8 | Localization live | Flip `ui.language` between `en-US` and `ar`; all strings and the whole layout follow |
| 9 | DI composition | `App.GetService<T>()` resolves `IConfigService`, `ILocalizationService`, `ShellViewModel`, `HomeViewModel` |
| 10 | Config-driven tiers | Header and prompt-bar selectors list the three tiers from `appsettings.json` |

## Phase 1 verify checklist

| # | Check | How |
|---|---|---|
| 1 | Audio maths correct | `dotnet test` — 24 tests: RMS identities, tone-to-band mapping, dB monotonicity, sample-rate independence |
| 2 | Capture starts | Tap the mic; log shows `Microphone capture started: 48000 Hz, 2 channel(s), quantum 128 samples` |
| 3 | Real samples arrive | Log shows `Microphone is live: peak <n>`; overlay `raw` is non-zero |
| 4 | Orb reacts | Silhouette ripples with speech; deformation tracks loudness |
| 5 | Bands do different work | Low bands swell the body, high bands sharpen the surface and lift the glow |
| 6 | 60fps under load | Overlay holds `fps 60.0` while capturing |
| 7 | Push-to-talk | Press and hold the mic (>450 ms) records, release stops. A short tap latches on |
| 8 | Denied mic | Deny desktop-app mic access — inline banner, orb returns to Idle, composer stays usable |
| 9 | Muted mic | A device delivering only zeros is reported as muted rather than silently doing nothing |
| 10 | No leaked capture | Navigating away or closing stops the graph; log shows `Microphone capture stopped` |

---

## Architecture

```
ZX0ai/            WinUI 3 app — thin views, no business logic in code-behind
ZX0ai.Core/       Transport-agnostic domain. No WinUI or WinRT reference, by design,
                  so it lifts into ZX0ai.Backend unchanged.
ZX0ai.Backend/    ASP.NET Core minimal-API scaffold. /chat, /agents, /skills stubbed
                  against the same Core interfaces the app uses.
ZX0ai.Tests/      xunit over ZX0ai.Core. Runs anywhere — no device, no graph, no UI.
```

The app depends only on Core **interfaces** (`IChatProvider`, `IConfigService`,
`IAudioService`), so orchestration can move server-side later with no UI rewrite.

### The orb

`Views/Controls/OrbControl.xaml.cs` — a `CanvasAnimatedControl` running its own render
thread. Per frame: a radial-gradient sphere plus chromatic rim is drawn into a bounded
offscreen, warped by a scrolling fractal `TurbulenceEffect` through a
`DisplacementMapEffect`, then additively combined with a blurred copy for bloom.

Two non-obvious constraints are load-bearing:

- **Threading.** `Update`/`Draw` run on the Win2D game-loop thread, where touching a
  DependencyProperty throws `RPC_E_WRONG_THREAD`. Every input is mirrored into an
  immutable snapshot on the UI thread and published by atomic reference swap.
- **Bounded source.** The sphere goes into a `CanvasRenderTarget`, not a
  `CanvasCommandList`. Displacement and blur over a command list's unbounded extent
  sample outside the drawn geometry and hollow the sphere out.

Displacement, blur and noise frequency scale with control size, so the 30px sidebar
orb and the 200px hero orb read identically.

Two further traps worth knowing about, both fixed:

- **`TurbulenceEffect.Offset` moves the generated rectangle, not the noise phase.**
  Animating it alone walks the displacement map off the sphere and leaves a hard tear
  where the rect ends. A `Transform2DEffect` cancels the translation, so the pattern
  scrolls while the rect stays anchored over the control.
- **Peak displacement is capped** at roughly a quarter of the radius. Past that the
  orb stops reading as a sphere and turns into an amoeba.

### The audio pipeline

`ZX0ai.Core/Audio/SpectrumAnalyzer.cs` holds the maths — Hann window, iterative
radix-2 FFT, eight log-spaced bands from 60 Hz to 8 kHz, all normalised through a dB
window. It is pure, so it is covered by unit tests. `ZX0ai/Services/AudioService.cs`
holds only the WinRT device plumbing.

Levels are mapped through decibels rather than linear amplitude: linear RMS spends
almost its whole range in the top few percent of loudness, so a linear mapping leaves
the orb looking dead until you shout.

One trap here too: reading an `AudioFrame`'s bytes needs `IMemoryBufferByteAccess`,
and the usual `[ComImport]` interface cast is a UWP/.NET Native idiom. Under CsWinRT
the projection returns an `IInspectable` RCW that does not support that cast and
throws `InvalidCastException` on every quantum. The working approach is an explicit
`QueryInterface` on the native pointer plus a direct vtable call.

---

## Deviations from the master prompt

| Spec | Built | Why |
|---|---|---|
| Primary UI language Arabic | **English default**, Arabic one config value away | Explicitly requested after the spec was written. Both resource sets are complete; RTL mirroring is implemented and verified. |
| Windows App SDK "latest stable" | 2.3.1 | Latest stable at build time. Verified against .NET 8. |
| `AudioService` in `ZX0ai.Core/Services` | `IAudioService` in Core; WinRT implementation lands in the app in Phase 1 | Core must stay free of WinRT to remain liftable into the backend (Section 15). |
| `zxa0-Ultra-full-max` has a member with role `leader` | That member is `planner` | A second Leader would contradict the constitution's single-final-authority rule. |
| `RuntimeIdentifiers` x86/x64/ARM64 | x64 only | Each RID pulls a full runtime pack; this machine is disk-constrained. Add the others in `ZX0ai.csproj` for release packaging. |

Model slugs in `appsettings.json` were resolved against the live OpenRouter model list,
not left as placeholders.

---

## Product naming — ZXA only

The user-facing product is **ZXA**. Provider slugs (`anthropic/claude-…`) and vendor
display names stay on the domain objects because logs, diagnostics and the capability
adapter genuinely need them, but **nothing vendor-specific reaches a rendered
surface**:

- Tier selectors show the ZXA tier key (`zxa-Lite`, `zxa-Ultra-full-max`).
- Agent turn cards and the team roster show ZXA callsigns (`ZXA Lead`,
  `ZXA Engineer`, …) derived from role, never from the slug.
- `ZxaBranding.LooksLikeVendorIdentifier` backs a regression suite so a vendor name
  cannot reach a label by accident when a model is swapped.

Because the callsign is derived from **role**, changing the model behind a tier can
never change what the customer sees.

## Tier identity and the Ultra mode

Tiers are not just a model name. Each declares a `level` (1–4) and optionally a
`theme`, and selecting one re-skins the running app:

- The **tier menu** in the header is a Model / Effort / Speed popup with submenus.
  **Effort is the real control** — it maps onto the configured tiers, so raising
  effort is what switches models rather than being a second setting to keep in sync.
- `zxa-Ultra` declares `"theme": "fire"`. Choosing it turns the whole shell red and
  ember — accents, gradients, ambient blooms, the orb and the tier flame icon.
- A **tok/s readout** appears in the header while tokens are streaming.

The switch works by mutating the shared brush instances in `Application.Resources`
rather than reloading dictionaries. Every view resolves accents through
`{StaticResource}`, so they all hold the same brush objects and repaint the instant a
colour changes — no rebinding, no per-view plumbing.

---

## Phases 2–6 verify checklist

| # | Check | How |
|---|---|---|
| 1 | All tests pass | `dotnet test` — **132 tests** across audio maths, SSE framing, tool-call reassembly, markdown, orchestrator protocols, skill gating, command allow-list |
| 2 | Streaming chat | Send a prompt; tokens stream into the bubble, model slug shown LTR, Stop button appears |
| 3 | Markdown + code | Ask for a code block; it renders with a language label and a copy button, forced LTR |
| 4 | Typed errors | Unset every key → "No provider credential"; use a revoked key → "The provider rejected the credential" |
| 5 | Cancellation | Press Stop mid-stream; partial text is kept, no error |
| 6 | Per-tier keys | Switch tier in Settings; the matching `ZX0AI_KEY_*` is used |
| 7 | Agent protocols | Pick `zxa-medim` or `zxa-Ultra`; turn cards stream per agent, roster dims inactive members, leader answers last |
| 8 | Constitution | Edit `constitution.md` next to the exe; the change reaches every agent's system prompt |
| 9 | Skill gating | A Reviewer calling `write_file` is refused; a Coder needs Leader approval; every attempt is audited |
| 10 | Command safety | `git status && rm -rf /` is refused for chaining; `rm` is refused as off-list |
| 11 | Live preview | Toggle the preview in the header; `render_preview` pushes HTML into WebView2, device widths switch |
| 12 | Backend contract | `dotnet run --project ZX0ai.Backend` then `GET /health`, `/protocols`, `/constitution` |

---

## What each phase added

| Phase | Delivered |
|---|---|
| 0 | Solution, DI, theme tokens, shell, 60fps idle orb |
| 1 | `AudioService` on AudioGraph, RMS + 8-band FFT, mic → Listening |
| 2 | `OpenRouterProvider` SSE streaming, markdown + code blocks, Thinking/Speaking, cancellation, typed errors |
| 3 | `ISkill` + registry + six built-ins, tool-calling, audit log, destructive-action gating |
| 4 | `Agent`, `AgentBus`, `AgentOrchestrator`, constitution, three protocols, turn cards, roster |
| 5 | `CommandRunner` with allow-list, `CommandCard`, `PreviewService` + WebView2 panel |
| 6 | Settings, backend stubs, per-tier credentials, layout and motion pass |
