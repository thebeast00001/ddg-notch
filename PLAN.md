# Forty — plan for a Windows island companion

*"Forty" is a working name taken from this folder; the product name is still an open decision.
Researched and written 10 November 2026. Companion file: [MOTION.md](MOTION.md), the motion and interaction spec.*

---

## 1. The short version

**What it is.** A small island at the top of the Windows screen that shows what is happening right now (music, a timer, charging, a call) and an AI companion that does three jobs no Windows island does today: it types what you say into any app, it takes notes on calls you agree to capture, and it turns those notes into the next task or calendar event.

**What the research changed.** The brief you pasted has the direction right. Four things in it needed correcting:

1. **The market is more crowded than the brief says, and the gap is narrower and clearer.** There are at least 18 Windows island apps, nearly all sold for $0–10 as media-and-widget utilities. Across every product page reviewed, none describes call transcription, push-to-talk dictation into other apps, or a daily brief. That is the open lane, so AI moves from "Phase 3" to the second milestone.
2. **Keep WinUI 3 and C#, but change how it is built.** Compile ahead of time from the first commit, package the app from the first commit, run speech and model code in a separate worker that exits when idle, and drop Rive (there is no production Rive runtime for WinUI).
3. **Several "build it ourselves" items have a better ready-made answer:** sherpa-onnx for all speech work, Microsoft.Extensions.AI for model access, and the LocalSend protocol for nearby transfer.
4. **Ship through the Microsoft Store only at first.** Registration is now free and Microsoft signs the package. The low-cost signing service the brief leans on is not open to individual developers outside the US and Canada, and unsigned installers are the most repeated complaint about rival apps.

**Timing.** First public release (island + dictation + call notes) at about 14 weeks of solo work, version 1.0 at about 22 weeks, provided the six proof spikes in section 8 pass. These are rough estimates, not commitments.

**One thing to know before starting.** The earlier build described in my notes (`~/xero AI`) is no longer on this PC; only its settings folder in `%LOCALAPPDATA%\Island` remains. This plan assumes a fresh start in `~/forty`. If that code exists somewhere else, milestone 1 gets shorter.

---

## 2. What I checked, and what in the brief was wrong or out of date

Every product page the brief cites was re-read today, plus four comparison pages and the platform documentation.

| The brief said | What the pages say today | Effect on the plan |
| --- | --- | --- |
| Three reference products, later six | At least 18 Windows island apps are named across the pages reviewed (section 3) | Do not compete on widget count or price |
| Airlock Plus €3/month, Pro €6/month on annual billing | Plus is €3.99/month or €35.99/year; Pro is €7.99/month or €71.99/year; 30-day refunds; no trial | Pricing anchor for an AI island |
| Airlock approves Claude Code and Codex requests | Claude Code only, through hooks. Codex shows status; you answer in its terminal. Airlock's source is public on GitHub and it needs a Mac with a physical notch | Approvals are simple to match; see 7.8 |
| Airlock transcribes "on the Mac" | It uses Apple's built-in speech recognition, Apple Intelligence for free notes, and sends paid notes through OpenRouter to Gemini or GPT | Their engine is the operating system's. Ours has to be chosen and shipped (7.4) |
| Syllo is the broadest Windows benchmark | True. Also: $7.99/month, $59/year or $149 lifetime; installer is unsigned; no Windows on Arm; relays approvals for nine coding agents; has its own MCP server; no call notes described; a "dictation" shortcut is listed but not described | Syllo is the feature-breadth rival, not the call-notes rival |
| Nusa uses about 30 MB and 0% idle CPU | Could not be verified: the numbers sit inside an image. Treat as the developer's claim | Use as a rough bar only |
| Notchify is the polish benchmark | Version 5.8, Store only, no AI features at all, price reported between $1.99 and $3.49 | Polish alone sells for about $3 |
| Rive characters after a C++ bridge spike | Rive documents only a community-maintained C# runtime for UWP, last updated January 2025. The one WinUI package is a 0.1.0-alpha | Use Composition shapes and Lottie-Windows instead (6) |
| Glass is a theme option | Windows App SDK 2.0 added `SystemBackdropElement`, which draws acrylic inside a rounded region of a transparent window | Glass is feasible without a second compositor; confirm in a spike |
| Store first, signed direct download as the alternative | Store registration is free for individuals (September 2025) and companies (May 2026). Azure Artifact Signing accepts individuals only in the US and Canada | Store only at first (9) |
| One process first; split later | Speech and language models are hundreds of megabytes to several gigabytes and run native code that can crash | Worker process from the start (6) |
| Four agent personas plus a router | Nothing in the research argues for this at launch | One companion with scoped skills first (5.4) |
| Our own encrypted transfer between our own clients | LocalSend has an open protocol with an HTTPS mode, and phones already run it | Speak LocalSend's protocol (7.9) |
| ASP.NET Core, PostgreSQL and a durable worker | Not needed until hosted AI is sold | Deferred; a thin proxy later |
| Composition gives smooth motion | Microsoft's guide describes a 60 FPS design target. Behaviour on 120/144 Hz screens has to be measured, and this laptop's panel is 60 Hz | High-refresh testing needs an external monitor |

Things the brief got right and the plan keeps: WinUI 3 with C#, Windows Composition for motion, Next.js for the website only, package identity from the start, no Windows Service, no Electron, the consent-first rules for the microphone, and the list of things not to promise.

---

## 3. The market, as it stands today

### 3.1 Windows island apps

| App | Price | Built with | AI features | Worth knowing |
| --- | --- | --- | --- | --- |
| **Syllo** 1.2.0 (also Mac) | $7.99/mo, $59/yr, $149 lifetime | Uses WebView2 | Assistant with local or hosted models; nine coding agents; MCP server; a beta that operates the PC | Unsigned; x64 only; nearby transfer described as unencrypted |
| **Notchify** 5.8 | Paid, about $2–3.50 | "Not Electron", GPU-rendered | None | 20+ widgets, 14 themes, lyrics, screen recording |
| **NotchPixel** | $9.99 once | DirectComposition (per a rival's table); about 13 MB idle (its own figure) | Status panel for Claude Code and Codex | Store-signed; phone controller |
| **Nusa** 1.2.0 | Free; widgets are "Nusa Pro" | Native, not a web view | None | Notifications only in the Store build |
| **D-Island** 1.0.0 | Free, Apache 2.0 | Not stated | None | Unsigned; 50 MB installer |
| **Pillar** | Free, MIT | Tauri 2, React, Rust | "Prism" assistant on Groq's cloud; sees only app name and window title | Unsigned |
| **BuildNotch** | Paid once | WinUI 3 | "AI usage limits" display | Unsigned; not on the Store |
| Dynamic Edge, Smart Notch, Isle Notch, Halo DynamicWin, Up A Notch, V-Notch, WinIsland, eIsland, Cosmic-light, DynamicWin, a Windhawk mod | Free to $5 | Mixed, some Electron | eIsland has an "AI agent screen"; Cosmic-light has a chat box | Mostly media, timers, lyrics |

Most of this comes from vendor pages, two of which are comparison pages written to promote the vendor's own app. Treat prices and technology claims as approximate.

**The gap.** None of these pages describes meeting transcription, speaker labels, searchable meeting history, push-to-talk dictation into other apps, a daily brief, a log of what the assistant did, or a per-source privacy view. One partial exception: Syllo lists a dictation shortcut without saying what it does.

**Complaints that recur across the pages:** unsigned installers and SmartScreen warnings; the pill covering browser tabs and title bars; overlays not showing in exclusive full-screen games; inconsistent multi-monitor behaviour; abandoned projects.

### 3.2 Mac references

- **Airlock** 1.2.2 (4 November 2026): the closest product in spirit. Five closed states (Resting, Listening, Working, Needs you, Done), call notes with consent, hold-to-talk dictation, Claude Code approvals with a readable rules file. Its site has an "On Windows? Tap here" button that counts demand.
- **NotchNook**: Classic $15 or Pro $19 lifetime. The widget and file-handling benchmark: Tray, Pockets opened by shaking the pointer mid-drag, teleprompter, Liquid Glass style.

### 3.3 Products that own one of our jobs

- **Wispr Flow** (dictation): about $15/month, with an India price about a third of that since July 2026. A notetaker is now bundled.
- **Granola** (meeting notes): on Windows in 2026; $14 per user per month for the business tier.
- **Microsoft**: Windows is adding agents on the taskbar, an on-device registry of "agent connectors" (MCP servers for File Explorer and Settings), and Copilot entry points in search and File Explorer.

None of these is an ambient island. Each proves people pay monthly for one of the three jobs.

### 3.4 What this means

1. A polished island with widgets is a $3 product in a field of 18. Do not build that.
2. Dictation and call notes are $14–15/month products elsewhere, and no island on Windows has them. Build that, inside an island that feels better than the $3 ones.
3. Being Store-signed, ARM64-ready and honest about data is a real advantage over this field. It costs nothing but discipline.
4. Two clocks are running: Airlock is measuring Windows demand, and Microsoft is building agent surfaces into the taskbar. Getting the three core jobs out in about three months matters more than breadth.

---

## 4. Product definition

**Promise:** a calm Windows companion that keeps your day in view, types what you say, remembers the calls you choose to capture, and turns them into next actions you approve.

**Who it is for first:** people who spend the day in calls and text boxes on a Windows laptop — developers, students, founders, freelancers. Developers are the natural early adopters because coding-agent approvals give them a reason to keep it open.

### 4.1 The three jobs, in build order

1. **Glance.** The island shows one thing at a time, clearly: now playing, timer, charging, device connected, next meeting, a file being held. It never moves when nothing is happening.
2. **Talk.** Hold a key, speak, release: the text appears where the cursor is, in any app. Works offline with a local model. The same key with a modifier asks the companion a question about what you have selected.
3. **Remember.** When a call starts, the island asks whether to take notes. If you say yes, it captures the call app's audio and your microphone, shows the words as they come, and ends with a summary, decisions, open questions and to-dos, each linked to the moment it was said.

Then the loop that ties them together (**Day loop**): a morning brief from your calendar and unfinished follow-ups, a prompt before each meeting with the last decision, and after it a drafted task or event that waits for your approval.

### 4.2 Scope by release

| Release | In | Out until later |
| --- | --- | --- |
| **Alpha** (you only) | Island shell, media, volume and brightness, battery, device connect, timer, file Shelf, settings | Everything else |
| **Public beta** | Dictation, Ask with chosen context, call notes with evidence links, meeting list and text search, privacy view, model settings | Calendar writing, notifications |
| **1.0** | Calendar (read, join, confirmed create), daily brief, follow-up drafts, notification history with rules, clipboard history with exclusions, Claude Code approvals, action log, themes, Hindi and English | Nearby transfer, widgets |
| **After 1.0** | Nearby transfer, semantic search across meetings, our own MCP server, Codex and other agents, teleprompter, a handful of widgets, optional wake word, phone companion | — |
| **Not planned** | 30 widgets, a full PC-control agent, a plugin SDK, organisation admin features | — |

### 4.3 Rules the product keeps

These come from Apple's eight design principles, applied to this product.

- **Purpose.** Every feature has to remove an app switch. A widget that only displays something available one click away does not qualify.
- **Agency.** Nothing external happens without a preview and a yes: sending, inviting, deleting, changing settings. Reversible local actions show an undo instead of a question.
- **Responsibility.** The microphone is live only during a held key or a call you accepted, and the island shows it the whole time. Context is chosen by you for each request, never gathered in the background.
- **Familiarity.** Follow Windows conventions: Esc closes, right-click opens options, Tab moves focus, system colours in contrast themes.
- **Simplicity.** One thing in the island at a time, a second as a small satellite, the rest queued.
- **Craft.** Every spring, size and delay is a named token in MOTION.md that can be defended.
- **Honesty about AI.** Notes are a draft tied to evidence. Uncertain speakers and owners are marked uncertain.

### 4.4 Do not promise

"First AI island on Windows" (it is not), "AirDrop for Windows", "always listening", "works with every headset's battery", "private AI" without saying what leaves the PC, or meeting notes described as a verified record.

---

## 5. Experience design

The full spec is in [MOTION.md](MOTION.md). The decisions that shape the build:

### 5.1 Four surfaces, one body

Resting pill → activity capsule → expanded panel → full page. They are one black shape that changes size and corner radius; it is never destroyed and recreated. Content materialises inside it. Everything grows from the top centre and leaves the way it came.

### 5.2 Adaptive rest (answers the "covers my tabs" complaint)

When the window in front is maximised, the pill rests as a thin sliver at the top edge and is click-through. It becomes the full pill when something happens, when the pointer dwells on it, or when the desktop or a non-maximised window is in front. The island's window accepts the pointer only over the shape itself, so clicks beside it go to the app underneath (verified in spike S1).

### 5.3 Attention rules you can read

Four modes: Normal, Focus, Meeting, Presenting. Four priorities: act now (approval needed, timer done, incoming call), timely (meeting in five minutes), ambient (track changed), digest (routine notifications). The rules for what interrupts in each mode sit on one settings page in plain sentences. No model decides silently what matters.

### 5.4 One companion first, a roster later

> **Changed 12 November 2026:** your feature list puts persistent agents at the centre, so [ROADMAP.md](ROADMAP.md) builds the runtime for several agents in phase 2 and introduces each agent the day its first job works (Scribe, Scout and Operator in phase 2, Planner in phase 3). The original reasoning is kept below.

The brief proposes four named agents (Planner, Scribe, Scout, Operator) plus a router. I recommend launching with **one companion** that has scoped skills, each with its own list of allowed tools and data sources. Named characters only help when two or more things run at once, which will not be common until after 1.0. The permission scoping from the brief is kept in full; only the cast is postponed. This is your call (section 11).

The companion has nine states, used identically in the animation, the run log and screen-reader labels: idle, listening, working, using a tool, waiting for approval, needs input, done, paused, failed.

### 5.5 A privacy view that shows the data path

One page: each source (microphone, call audio, notifications, calendar, clipboard, screen, chosen folders) with who can read it, which model sees it, what is kept and for how long, and buttons to pause, clear and export. A single **Local only** switch turns off every network model call and every online lookup.

---

## 6. Technology choices

| Layer | Choice | Why, and what it replaces |
| --- | --- | --- |
| Language and runtime | C# on .NET 10 (long-term support; already installed in `~/tools/dotnet10`) | Move to .NET 11 (scheduled for release today) once Windows App SDK and CsWinRT confirm support |
| UI framework | WinUI 3 on Windows App SDK 2.x, published with Native AOT and trimming from the first commit | An independent test showed a blank WinUI 3 window appearing in 0.09–0.18 s with AOT against 0.5–0.7 s without. My notes put the earlier, unoptimised build at about 160 MB |
| Motion | `Microsoft.UI.Composition` plus a small motion engine of our own | Springs with damping ratio and response, velocity carried through every retarget (7.2) |
| Glass theme | `SystemBackdropElement` in a transparent window; solid black is the default | New in Windows App SDK 2.0 |
| Companion and icons | Composition shapes driven by springs; Lottie-Windows for authored one-shot flourishes | Replaces Rive. Lottie-Windows is Microsoft's, generates C# and runs on the compositor |
| Speech | sherpa-onnx 2.x (Apache 2.0; NuGet for win-x64 and win-arm64) | One runtime for voice detection, speech-to-text, speaker separation, punctuation, noise reduction and wake word. It has .NET examples for each |
| Speech models | Local Parakeet v3 for English; Sarvam's API as the opt-in engine for Hindi and other Indian languages (7.4) | Chosen by measurement in spike S4 |
| Model access | Microsoft.Extensions.AI (`IChatClient`) | One interface over any OpenAI-compatible endpoint: Ollama, LM Studio, Foundry Local, or a hosted service. Phi Silica on Copilot+ PCs as an extra |
| Assistant loop | Plain function calling over `IChatClient`, plus our action log | Microsoft Agent Framework 1.0 exists (April 2026) and builds on the same interface; adopt it only if multi-agent workflows are needed |
| Connectors | The MCP C# SDK as a client; later our own MCP server; Windows agent connectors when stable | Lets users add tools without us writing each integration |
| Storage | SQLite with full-text search; meetings also saved as Markdown files in `Documents\Forty\Meetings`; secrets in Windows credential storage | Plain files mean your notes outlive the app |
| Processes | `Forty.exe` (island, always on), `Forty.Brain.exe` (speech and models, on demand), `forty-hook.exe` (bridge for coding agents) | Replaces the single process |
| Website | Next.js and TypeScript, with a live demo using the same spring tokens | As in the brief |
| Cloud | None until hosted AI is sold; then a thin proxy that holds the provider key and enforces quotas | Replaces ASP.NET Core + PostgreSQL + worker for now |
| Distribution | Microsoft Store (MSIX), x64 and ARM64 | Section 9 |
| Minimum Windows | Windows 11 22H2 | Windows 10 is out of support and lacks APIs the capture and overlay work relies on |

**Where WinUI 3 will not win:** memory. NotchPixel claims about 13 MB on DirectComposition. A WinUI 3 app will not get there. The target is 90 MB or less for the island with zero idle CPU, and spike S3 measures it. If buyers turn out to care, the fallback is to draw the resting pill in a tiny DirectComposition shell and load WinUI only when the panel opens.

**Why not something else.** Rust with raw composition (what the lightest rivals use) means writing text editing, scrolling lists, drag-and-drop and screen-reader support by hand, and this product is full of text. Tauri and Electron keep a browser engine running for an overlay that should cost nothing at rest. Flutter and Avalonia are worth it only if Mac or Linux versions are planned soon.

---

## 7. Architecture

```
┌──────────────────────── Forty.exe  (always on) ────────────────────────┐
│ WinUI 3, Native AOT, one small topmost window per display               │
│                                                                         │
│  Island surface ── Motion engine ── Activity queue and attention rules  │
│  Event adapters:  media · power · audio devices · Bluetooth · network   │
│                   notifications · calendar cache · full-screen state    │
│  Input:           push-to-talk key · global shortcut · drag and drop    │
│  Local store:     SQLite (settings, activities, action log)             │
└──────────────┬──────────────────────────────────┬───────────────────────┘
       named pipe, current user only       named pipe, current user only
┌──────────────▼───────────────┐    ┌─────────────▼──────────────┐
│ Forty.Brain.exe  (on demand) │    │ forty-hook.exe  (per call) │
│ audio capture · voice        │    │ started by a coding        │
│ detection · speech-to-text   │    │ agent's hook; waits for    │
│ speaker separation · model   │    │ approve or deny, then      │
│ calls · tool runs            │    │ exits                      │
│ exits after a few idle min   │    └────────────────────────────┘
└──────────────────────────────┘
```

Suggested layout:

```
forty/
  PLAN.md  MOTION.md
  src/
    Forty.App/         island, panels, settings (WinUI 3)
    Forty.Motion/      springs, tokens, choreography
    Forty.Platform/    Windows adapters: media, power, audio sessions, notifications
    Forty.Brain/       worker: capture, speech, models, tools
    Forty.Contracts/   pipe messages
    Forty.Hook/        console bridge for coding agents
  spikes/              throwaway proofs S1–S6
  tests/
  site/                website, later
```

### 7.1 The overlay window

- One window per display, with a fixed size large enough for the full page. What changes is its window region, which is kept to the island's bounds: outside the region the window neither draws nor receives the pointer. The region grows before a morph and shrinks after it settles. (Spike S1 showed this passes every click-through check, and avoids resizing the window mid-animation.)
- See-through needs two things together: a backdrop that paints nothing, and `DwmExtendFrameIntoClientArea`. Without the second, the window is an opaque black rectangle. The colour-key method is ruled out because it makes a black island click-through.
- The window starts two pixels above the monitor and those rows are left out of the region. Windows draws a one-pixel frame line there otherwise.
- Topmost, never activated by hover, no taskbar or Alt-Tab entry. It takes keyboard focus only when you click into a text field or press the shortcut.
- Always-on-top is requested from Windows directly, as the last step of creating the window, and then read back. Windows occasionally accepts the request and does nothing; asking for "not always-on-top" and asking again fixes it. The check repeats whenever the window is shown or the app in front changes. Do not rely on the framework's always-on-top setting alone.
- Position and size are computed per monitor at that monitor's scaling.
- Hidden for exclusive full-screen apps and, by rule, for chosen apps. Windows reports presentation and full-screen states through `SHQueryUserNotificationState`.
- A "hide from screen sharing" switch uses `SetWindowDisplayAffinity`, so notifications and transcripts do not appear in a shared screen. The capture indicator always stays visible to you.

### 7.2 The motion engine

Composition runs animations off the UI thread, which keeps the hero morph smooth even while the panel's content is loading. Spike S2 measured what it does not do: a spring that replaces a running one does not inherit its speed, and a change made by the app reaches the screen about 43 ms later, give or take 6–9 ms.

The engine therefore uses **additive springs**, the technique behind iOS's interruptible animations. A change of target never replaces what is running: the target moves at once, and a new decaying term is added that starts out exactly cancelling the move. Earlier terms keep running untouched, so position and velocity stay continuous on screen whenever the change lands. Of the three designs measured, this and Windows' built-in spring showed no position jump at an interruption; the design that restarts a formula from an app-computed position jumped by a median of 4 px and up to 114 px. Details and numbers are in MOTION.md section 4.

### 7.3 Event adapters

Everything is driven by events from Windows; nothing polls. Media comes from the system media session manager. Power, device and network changes arrive as window messages and WinRT events. Notifications need the User Notification Listener capability and your consent, which is why the app is packaged from day one. If Windows or a device does not expose a value (many headsets do not report battery), the island shows nothing instead of guessing.

### 7.4 Speech

Every engine sits behind one interface (audio in, text out), so local and cloud engines are interchangeable. Local engines run through sherpa-onnx inside the Brain process. It is P/Invoke over a native library, so it should work with AOT; that is still untested.

| Need | Engine | Why (measured in S4 unless noted) |
| --- | --- | --- |
| English dictation; call audio with `--local` or when ElevenLabs fails | Parakeet TDT 0.6b v3, local | 0.7% word errors, 0.53 s for a 10-second sentence on four threads, punctuated output, nothing leaves the PC |
| Hindi, Hinglish and other Indian languages | Sarvam Saaras v4, cloud, opt-in | 2.5% word errors in Hindi in about half a second through its standard endpoint; has code-mixed and Roman-script modes. Costs ₹0.49 a minute and sends audio off the PC. Its streaming endpoint is faster but refused about a third of sessions with a rate-limit error on this key |
| Other languages in the cloud; a second option for Hindi | ElevenLabs, cloud, opt-in: its real-time model for dictation, Scribe v3 for recordings | Real-time: 1.3% word errors in English, 3.1% in Hindi, text about 0.4 s after you stop. Scribe v3: 1.3% and 2.5%, but 1.4–1.6 s. 90+ languages, word timestamps and speaker labels, about $0.20 an hour |
| Hindi with no internet or in Local only mode | Omnilingual 300M, local | 16.2% word errors, about 1 s, no punctuation. A fallback, not a match for Sarvam |
| PCs with four cores or fewer | Moonshine base v2, local | 5.9% word errors but 0.30–0.45 s and only 171 MB |
| Words appearing live in a call | Not chosen yet | A streaming variant of Parakeet exists; test in S5 |
| Who spoke | ElevenLabs Scribe with voice labels | Told two Hindi readers apart on all four lines of a test stream; sherpa-onnx diarization on the PC is the untested local alternative |

Whisper large-v3-turbo was the original pick for Hindi and is dropped for the CPU path: 4–6 seconds a sentence, and broken Hindi output in this build.

**Who does what, decided 12 November 2026** (your call: keep Sarvam, use ElevenLabs too; measurements are in section 8 under "Routing"):

| Job | First choice | If it is slow or fails |
| --- | --- | --- |
| English dictation | Parakeet on the PC | Nothing needed: no network is involved |
| Hindi and Hinglish dictation | Sarvam | ElevenLabs is asked as well after a head start of about a second and the first answer wins; if neither answers, Omnilingual on the PC |
| Call transcript | ElevenLabs Scribe, because it tells the other voices apart | The standby engine (Parakeet for English, Sarvam for Hindi) writes it without telling voices apart |
| Call summary and to-dos | Sarvam `sarvam-105b-conversations` | Asked a second time; then the transcript is kept without a summary |

Two things to know about the ElevenLabs choice for calls. Its API charges by the length of audio and counts silence, so silence is cut out before sending. And it keeps the audio it is sent: switching that off is offered to enterprise accounts only. `--local` keeps every call on the PC.

Keys are typed once into `spikes/setup-keys.cmd`, checked with each service, and stored encrypted by Windows for the user account.

The scores come from twenty clean public clips. Your own voice, Hinglish and a noisy room are still untested.

### 7.5 Dictation

1. Hold the key (default Right Ctrl, configurable; double-tap to lock for long dictation). The key is read through raw input, which only listens. No keyboard hook sits in the path of every keystroke.
2. The microphone opens on key-down. The island shows "listening" only once audio is actually arriving, with a soft tick on the same frame. Prefer the laptop microphone over a Bluetooth headset's, which takes a second or two to switch modes and degrades music while it is open.
3. On release: transcribe, tidy (fillers out, punctuation, your personal dictionary of names and terms), insert.
4. Insertion: typed as Unicode input for short text, paste-and-restore-clipboard for long text. If there is no text field, or the target app runs as administrator and Windows blocks input into it, the text goes to the clipboard and the island says so.
5. Audio is never written to disk.

Target: under 0.6 s from key-up to text for a ten-second sentence, plus at most 0.7 s when tidying with a model.

A working prototype of steps 1–4 exists in `spikes/SpeechSpike` (see "Dictation prototype" in section 8). Measured against the target: Parakeet locally 0.53 s, Sarvam's standard endpoint 0.5 s, ElevenLabs streaming 0.4 s. Two things it taught: type only the final text, never a running guess, and cancel when another key is pressed during the hold so that ordinary shortcuts keep working.

### 7.6 Call notes

1. **Detect.** Windows exposes which processes have live audio sessions. If one app is both recording from the microphone and playing audio for more than a few seconds, it is probably a call. This is event-driven.
2. **Ask.** The island shows "Take notes?" with Yes, Not now, and Never for this app. "Not now" writes nothing.
3. **Capture two streams.** Other people: process-loopback capture of only the call app's audio, so your music is not recorded (proven in S5). You: the microphone, opened in communications mode so that Windows applies its own echo cancellation and speakers do not leak the others' voices into your channel (not yet tried on a real call). Both streams are brought to a normal level first, because the app stream follows the app's own volume setting.
4. **Transcribe.** Each stream is cut at pauses and transcribed, so lines appear a few seconds behind the conversation. Two streams give "you" against "others" for free; speaker separation runs only on the others' stream. A second, more careful pass runs when the call ends.
5. **Write notes.** Summary, decisions, open questions, to-dos. Every item stores the transcript lines it came from. An owner or speaker the model is not sure of is marked as unsure.
6. **Keep.** Transcript and notes as Markdown. Audio is discarded unless you turn on "keep audio", which enables jump-to-the-moment playback.
7. **Indicator.** A capsule with elapsed time, pause and stop is on screen for the whole capture and cannot be hidden.
8. **Consent.** A reminder about recording rules the first time, and a one-click "I'm taking notes with an AI tool" line to paste into the chat.

Echo cancellation and Bluetooth headsets are the hard part. S5 tests Teams, Zoom, Meet in a browser, Discord and WhatsApp with speakers, a USB headset and a Bluetooth headset, including a device change mid-call and sleep and wake.

### 7.7 The assistant

- **Models.** Local runtimes are detected automatically (Ollama, LM Studio, Foundry Local). Any OpenAI-compatible address and key can be added. On Copilot+ PCs, Phi Silica handles small jobs. This laptop has no neural processor, so that path needs another test machine.
- **Hosted default.** You said earlier you will not use Claude for the app's AI and have not picked a provider. Recommendation: develop against a model broker (one key, many models) so the model is a setting, then choose by scoring candidates on about 20 of your own recorded calls and dictations: missed to-dos, invented owners, cost per call, delay from India.
- **Context is chosen.** Each request shows chips for what it will read (selection, clipboard item, a file, a screen region, the current call, the calendar). You can remove any chip before sending.
- **Tools carry a risk class.** Read-only runs without a prompt. Local and reversible shows an undo. External or irreversible shows a preview and asks every time. Destructive asks every time and never offers "always".
- **Action log.** Every run records what was read, what was changed, the result, and how to undo it.

### 7.8 Coding-agent approvals

Claude Code calls a hook before it shows a permission prompt, and the hook can answer allow, deny or pass. `forty-hook.exe` is that hook: it sends the request to the island over a named pipe, waits for your answer, and passes back to the terminal after a timeout or if Forty is not running. A pipe restricted to your user account is safer than a listening network port. Rules you create with "Always" are written to a readable file. Force-pushes, `sudo`, recursive deletes, database drops and piping a download into a shell never get an "Always" option.

Codex now has hooks too, but public write-ups disagree on what its pre-tool hook covers, so start with status only, as Airlock does.

### 7.9 Nearby transfer (after 1.0)

Implement LocalSend's protocol in HTTPS mode: discovery by multicast, a PIN for first contact, the other device's certificate fingerprint remembered as "trusted" and revocable, a hash check after each file. The day it ships, it works with the LocalSend apps people already have on Android, iPhone, Mac and Linux, which avoids the problem of nobody else having our app. Clipboard and link handoff between two Forty installs can sit on top later.

### 7.10 Windows' own agent platform

Two things to track, both still moving: the on-device registry of agent connectors (so the companion could use File Explorer and Settings with Windows' own consent prompts), and agents on the taskbar (one secondary source names a `Windows.UI.Shell.Tasks` API; unverified). Neither is a dependency. Revisit both at 1.0.

### 7.11 Budgets

| Measure | Target |
| --- | --- |
| Frames drawn at rest | 0 |
| CPU at rest, averaged over a minute | 0.0% |
| Island memory after an hour | 90 MB or less (stretch: 60) |
| Brain memory when idle | 0 (process not running) |
| Cold start to visible pill | 0.4 s or less |
| Shortcut to first expanded frame | 50 ms or less |
| Pointer-down to visible response | Within one frame |
| Expand and collapse | No dropped frames over 200 runs, at 60 Hz and on a 120 or 144 Hz monitor |
| Music visualiser | 2% of one core or less; off in battery saver |
| Dictation, key-up to text | 0.6 s for ten seconds of speech |

My notes say the earlier visualiser cost about 8% of a core. MOTION.md section 12 describes the cheaper design.

---

## 8. Roadmap

Each milestone ends with a test you can run yourself.

> **12 November 2026:** milestones M1–M5 below are replaced by [ROADMAP.md](ROADMAP.md), which places all 150 features from your list in five phases (The Island, Voice and the agents, Meetings and the day, Reach, Automate and extend). M0 and its results stay here.

### M0 — Proof spikes (about 2 weeks)

Six throwaway prototypes. Each has a pass line and a fallback.

| Spike | Question | Pass | If it fails |
| --- | --- | --- | --- |
| **S1 Overlay** | Can a small, transparent, topmost window stay out of the way? | No focus theft, no Alt-Tab entry, sharp at 100–200% scaling, correct on two monitors and all virtual desktops, hidden in full-screen games | Adjust window styles; this must pass |
| **S2 Motion** | Can a morph be interrupted at any instant with no jump? | 200 random retargets with no visible discontinuity in slow-motion capture; drag release continues at finger speed | Drive the hero springs per frame ourselves |
| **S3 Footprint** | What does an AOT, packaged WinUI 3 island cost? | 90 MB, 0.4 s, 0% idle | Resting pill in a DirectComposition micro-shell |
| **S4 Speech** | Which models, at what delay, from C#? | English and Hindi dictation under the 0.6 s target on CPU; AOT build works | A second engine (whisper.cpp) behind the same interface |
| **S5 Call capture** | Can we get two clean streams from real calls? | Ten-minute captures from four call apps on speakers and headsets, no echo of others in your channel | Ask for headphones; single-stream mode |
| **S6 Glass** | Does acrylic follow the morphing shape? | No lag or tearing at the edge during a morph | Glass waits; ship ink only |

S1 also checks the notification-access prompt and event delivery from a packaged build.

#### Results so far (11 November 2026)

Code is in `spikes/IslandSpike`. To try the island by hand, run `spikes/run-island.cmd` (right-click the island to quit). All measurements below are from this laptop: 1920×1080 at 125% scaling, 60 Hz, with other apps running.

| Spike | Status | Outcome |
| --- | --- | --- |
| **S1 Overlay** | Passed everything that can be checked on one screen | 14 of 14 automated checks pass; four checks need you or more hardware |
| **S2 Motion** | Engine chosen; frame pacing still open | Additive springs: no position jump at an interruption. About 1 frame in 8 lands late during fast motion, cause not isolated |
| **S3 Footprint** | Blocked | There is no Microsoft C++ linker on this PC, which ahead-of-time compilation needs. Install Visual Studio Build Tools with the C++ workload |
| **S4 Speech** | English passes on this CPU; Hindi passes only through the cloud; your own voice not yet tested | Parakeet v3 locally for English (0.7% word errors, 0.53 s). Sarvam's API for Hindi (2.5%, 0.54 s). ElevenLabs matched that accuracy but took 1.4–1.6 s. The best local Hindi model made 16.2% word errors in about 1 s |
| **Dictation prototype** | Built and shown in the island. Typing, start-up and the island display are verified; the key press and the microphone have not been tried by anyone yet | `spikes/start-forty.cmd`: hold Right Ctrl in any app, speak, let go, and the text is typed at the cursor while the island shows listening, working and done |
| **S5 Call capture** | The capture technique is proven; no real call has been tried yet | One app's audio is captured with nothing from other apps (0% word errors on the captured sentence, none of the other). `spikes/call-notes.cmd` records a call as two streams and writes a transcript. The island asks before recording and shows a recording capsule with Stop |
| **S6 Glass** | Not started | — |

**S1 in detail**

- Showing the window and morphing it never took focus from the app in front. The window is a tool window (absent from Alt-Tab and the taskbar), topmost, and not activated by the pointer.
- Pointer checks, made with `WindowFromPoint` and no simulated input: the pill, the panel's middle and the sliver receive the pointer; 60 DIPs beside the pill, 100 DIPs below it, 40 DIPs beside the panel and 30 DIPs below the sliver all belong to the app underneath.
- See-through was confirmed by reading screen pixels: inside the window's region but outside the pill, the colour matches its neighbour outside the region. The first attempt failed this check, which is how the `DwmExtendFrameIntoClientArea` requirement was found.
- **Always-on-top needed its own check (found and fixed).** In about 12 of 500 starts the window came up without the always-on-top flag and sat behind the app in front. Windows reported success for the request, and for every later one, and left the flag off.
  - *What it is not.* Windows was not refusing the program: in a failing start, a bare window made by the same process got the flag, and so did a second island window. Nothing in the app was undoing it: a request that bypasses the window's own message handlers failed the same way. It did not track how recently there was keyboard or mouse input.
  - *What works.* Asking for "not always-on-top" and then asking again took in all 6 failing starts where it was tried.
  - *The fix.* The island no longer uses the framework's always-on-top setting. It asks Windows directly as the last step of creating the window, reads the flag back, clears and re-asks if it is off, and repeats the check whenever the window is shown or the app in front changes. Relaunching the process remains as a last resort and has never triggered.
  - *Proof.* Over 600 hidden starts, none ended without the flag: 300 using the framework's setting, of which 2 needed the repair, and 300 asking directly, of which none did. Twelve full self-checks afterwards passed 14 of 14.
  - *Still unknown.* Why Windows ignores the first request. Zero failures in 300 direct requests against 2 in 300 is too few to say that asking directly avoids the problem, which is why the check stays.
- Not checked: a second monitor, other scaling factors, virtual desktops, a real full-screen app, and notification access from a packaged build. A quick manual test for full screen: with the island running, press F11 in a browser; the island should disappear and come back.

**S2 in detail**

Three engines ran the same script: 200 changes of target at random moments 30–300 ms apart, between random shapes with random springs, then five drags released at 900 DIPs per second. A capture of the monitor recorded the body's width and height in every composed frame, and each run was compared with what the engine's maths says should have been on screen.

| Engine | Position jump at an interruption | Screen against model, median error |
| --- | --- | --- |
| A. Windows' built-in spring, speed supplied from an app-side mirror | None (max 0.15 px) | 2.6 px |
| B. A closed-form spring formula, restarted on each change | Median 4 px, 95th percentile 45 px, max 114 px | 1.3 px |
| C. Additive springs | None (max 0.06 px) | 1.4 px |

The jump figures come from the model fitted to the capture, not from the pixels directly; the median error shows how well that model explains the pixels. A and C are continuous by construction and the capture agrees with them. C is chosen over A because A's speed handoff still depends on an app-side estimate, and A took about 10 ms longer to reach the screen.

Other things measured:

- Windows' built-in spring follows the textbook formula. Its `Period` is 1/ω (not a full cycle), `DampingRatio` is the true damping ratio, and `InitialVelocity` is in units per second. A spring that replaces a running one starts at zero speed.
- A change takes about 43 ms to reach the screen when made immediately, and about 65 ms when made inside the frame callback. So: act on input at once, never defer to the frame tick.
- Baseline cost of the spike, which is not ahead-of-time compiled and not packaged: 83 MB private memory, 118 MB working set. It used about 0.6% of one core in its first idle seconds, so "zero at rest" is not yet shown.

**Open after S2**

- **Frame pacing.** About 70% of fast-moving frames were within ±2 ms of where the model says they should be. About 1 in 8 was late by 4–24 ms, and 2–6% of frame intervals missed a refresh, varying a lot between runs. The capture harness, the busy desktop and WinUI's in-app compositor are all possible causes. This does not meet the "no dropped frames" line. Next: measure with PresentMon on a quiet desktop, repeat on a 120 Hz or 144 Hz display, and run the same motion on the system compositor for comparison.
- **Feel.** Nothing here replaces trying it by hand. Hover, dwell, click and leave are implemented in the spike but were not exercised by me.
- **Content.** The spike morphs an empty body. Content choreography (MOTION.md section 5) is not built.

**S4 in detail**

Code is in `spikes/SpeechSpike`. The test used twenty public clips with reference text: ten English sentences from LibriSpeech and ten Hindi sentences from the IndicTTS corpus, each 4–12 seconds long. Local models ran on the CPU only, through sherpa-onnx 2.0.1. Sarvam and ElevenLabs ran through their standard REST APIs from this PC. Times run from handing over the audio to having the text; the cloud engines' times include the network.

| Engine | English word errors | Hindi word errors | A 10-second sentence | Memory while loaded |
| --- | --- | --- | --- | --- |
| Parakeet TDT 0.6b v3 (local) | 0.7% | No Hindi | 0.53 s on 4 threads, 0.76 s on 2 | 723 MB |
| Moonshine base v2 (local, English only) | 5.9% | No Hindi | 0.30 s on 4 threads, 0.45 s on 1 | 171 MB |
| Omnilingual 300M (local) | 5.9% | 16.2% | 1.0 s on 8 threads, 1.7 s on 4 | 430 MB |
| Whisper large-v3-turbo (local) | 1.3% | 75.6% (it broke) | 4.3–6.5 s | 1,420 MB |
| Sarvam Saaras v4 (cloud) | 2.0% | 2.5% | 0.49–0.54 s | None |
| ElevenLabs Scribe v3 (cloud) | 1.3% | 2.5% | 1.4–1.6 s | None |

What this says:

- **English passes locally.** Parakeet v3 meets the 0.6 s target on four threads and was as accurate as anything tested. On two threads it takes 0.76 s, so a four-core laptop will miss the target and should use Moonshine or the cloud.
- **Hindi does not pass locally.** The best local model made 16.2% word errors in about a second and returns bare text with no punctuation. Many of those errors are spelling variants (its character error rate is 3.7%), but Sarvam made 2.5% word errors in half a second, with punctuation.
- **Whisper turbo on the CPU is out.** It took 4–6 seconds a sentence, and on Hindi it fell into repetition loops. That is a result for this int8 build through sherpa-onnx on a CPU, not for Whisper on a GPU.
- **Sarvam met the delay target** from India: 0.31–0.59 s per clip over the network.
- **ElevenLabs is as accurate but slower.** Scribe v3 matched Sarvam on Hindi (2.5%) and its transcripts were near perfect in both languages, but its standard endpoint took 0.8–1.6 s per clip from here, about three times Sarvam's. It is cheaper ($0.20 an hour of audio against Sarvam's ₹29.40), covers 90+ languages including the European ones Sarvam lacks, and gives word-level timestamps and speaker labels in the same call. It keeps audio by default; its zero-retention mode is for enterprise plans.
- **Streaming was measured afterwards** and changes the picture for ElevenLabs; see the streaming table below.
- **Loading matters.** Parakeet takes 2.9 s to load, so the Brain process has to stay loaded while dictation is switched on and unload only after idle minutes.

Limits of this test:

- Ten clips per language is 150–200 words, so one wrong word is about 0.6 percentage points. Parakeet, Whisper and Sarvam cannot be ranked against each other on English from this.
- The clips are clean studio and audiobook recordings from public corpora. Those corpora may be in the models' training data, which would flatter the scores, most plausibly Sarvam's Hindi score.
- No Hinglish, no accents, no background noise and no real microphone. `spikes/speak.cmd en` and `spikes/speak.cmd hi` run your own voice through every engine. That is the real test and it has not been done.
- Ahead-of-time compilation is untested (blocked like S3). Nothing was measured on battery or on a weaker CPU.
- Each cloud run used about 2.5 minutes of audio: roughly ₹1.2 of Sarvam credit and about one US cent at ElevenLabs.
- Both cloud services scored the same on Hindi and made the same two "errors", which are a spelling variant and a word the speaker barely says. The test set is too easy to separate them.

**Streaming, measured as dictation would use it**

Each clip was sent in small pieces at speaking speed, then the utterance was ended. The wait is from that moment to the final text.

| Engine | English word errors | Hindi word errors | Wait after you stop | Sessions that worked |
| --- | --- | --- | --- | --- |
| Sarvam Saaras v4, streaming | 2.0% | 2.5% | 0.26–0.31 s | 39 of 60 |
| ElevenLabs real-time (scribe_v2_realtime) | 1.3% | 3.1% | 0.38–0.41 s | 20 of 20 |

- **ElevenLabs streaming meets the target.** Its text arrives about 0.4 s after you stop, against 1.4–1.6 s for its standard endpoint, with the same accuracy. A session takes about 0.5 s to open, which overlaps with you starting to speak.
- **Sarvam streaming is the fastest when it works, and unreliable on this key.** About a third of sessions were closed with "Rate limit exceeded" in each of three runs. Closing connections properly, pausing between clips and sending fewer, larger messages made no difference. The cause is not identified; a limit on new sessions per minute for this plan is my best guess. The clips that went through returned exactly the same text as its standard endpoint. Keeping one session open across several utterances was not tried.
- **Do not type the running guess.** ElevenLabs' interim text sometimes invents an ending that the final text then drops. Only the final text should reach the cursor.

**Decision from S4:** one interface, several engines. Parakeet v3 locally for English and for all call audio. Sarvam through its standard endpoint as the opt-in engine for Hindi, Hinglish and other Indian languages. ElevenLabs' real-time model as the second cloud option, for languages Sarvam lacks and as the alternative for Hindi. Omnilingual as the offline Hindi fallback. Moonshine for PCs with four cores or fewer.

**Dictation prototype**

`spikes/dictate.cmd` runs push-to-talk dictation from a console window: hold Right Ctrl in any app, speak, let go, and the text is typed where the cursor is.

- The key is watched through raw input, so nothing sits in the path of your keystrokes. The microphone opens on key-down and closes on key-up. A short quiet sound marks each.
- Pressing any other key while the talk key is held cancels, so Right Ctrl shortcuts still work. A hold shorter than 0.3 s is ignored.
- The text is typed as Unicode input, with a trailing space. No clipboard is used.
- English runs locally by default. Hindi uses Sarvam if its key is set, then ElevenLabs, then the local model. `--engine` picks any engine, including the two streaming ones.

Verified: a 104-character test in English and Hindi, with digits and punctuation, arrived exactly in a test window of the spike's own. Dictation mode starts, loads Parakeet (715 MB) and waits for the key using no CPU.

Not verified by me, because I do not press keys or open the microphone on your PC: detecting the real key, recording, and typing into your actual apps. Windows blocks typing into apps that run as administrator; the prototype reports that case and does nothing else about it.

Missing on purpose: removing filler words, and switching language without restarting.

**Dictation in the island**

The speech process and the island are separate programs, as section 7 describes, joined by a named pipe that only the signed-in user can open. The speech process writes one short line per event (listening, the voice level twenty times a second, working, done, failed, idle) and the island shows it:

- **Listening:** the body becomes a 168 × 36 capsule with a five-bar level meter, each bar on its own spring.
- **Working:** three dots circle once a second.
- **Done:** a green check draws once, and the island returns to rest 0.8 s later.
- **Cancelled** returns to rest at once. **Failed** shows a red dot briefly.

Dictation works the same when the island is not running. `spikes/start-forty.cmd` starts both.

Verified with a scripted run of three utterances (one done, one cancelled, one failed), with a picture saved of each state: `docs/island-listening.png`, `docs/island-working.png` and `docs/island-done.png`. With this display and the pipe, the island used no CPU at rest (0 ms over ten seconds) and about 102 MB. Only stills were checked; how the motion between states looks has not been judged by anyone.

In one test run the panel opened by itself after a cancelled utterance. The likely cause is the mouse pointer resting where the island appeared, which counted as a hover; a rerun with the pointer elsewhere did not reproduce it. Hover now needs the pointer to move at least 3 DIPs after entering, so an island that changes under a resting pointer opens nothing.

**S5 in detail**

Code is in `spikes/SpeechSpike` (`CoreAudio.cs`). To take notes on a real call, start the call and then run `spikes/call-notes.cmd`.

Built and checked without a real call and without opening the microphone:

- **Call detection.** Windows lists which programs have the microphone open and which are playing sound, and reading that list records nothing. A program doing both at once is treated as a call. On this PC the list currently shows nothing using the microphone, so no call. Detection of a real call has not been tried.
- **One app's audio only.** Two copies of a test player each played a different known sentence at the same moment, and only the first was captured. The capture matched the first sentence's waveform at 0.77–0.83 (1 is identical) and the second's at 0.06, which is what two unrelated recordings score. Its transcript was the first sentence with no word errors and nothing of the second. So music or another app playing during a call stays out of the notes.
- **Format.** Windows delivers an app's audio as 16 kHz mono directly, which is what speech recognition wants. No conversion code is needed.
- **Volume.** The capture comes after the app's own volume control: at 0% it is silent, at 4% it is 4% of full scale. Each stream is therefore brought to a normal level before pauses are found and words recognised. Without that step a quiet capture lost words.
- **The recorder, their side, end to end.** Pointed at the test player as a pretend call app, it found the app, recorded it, split it at pauses, transcribed it with timestamps and wrote a Markdown file. The text matched what was played.
- **The microphone side's plumbing** was exercised against the speaker output for a second and a half: the stream opened at 16 kHz mono and delivered audio. The microphone itself was never opened.

Only a real call can show the rest, and nobody has tried one yet:

- Whether a call is detected in each app: Teams, Zoom, Meet in a browser, Discord, WhatsApp.
- The microphone stream, and whether the others' voices stay out of it when you use speakers. The recorder opens the microphone in communications mode, which on Windows 11 24H2 and later applies Voice Clarity (echo cancellation and noise suppression) to any app that asks for it. Each transcript reports a rough echo score. The score was checked on synthetic audio (0.05 for an unrelated pair, 0.29 with a 30% leak mixed in) and not yet on a real room.
- Headsets, especially Bluetooth; a device change mid-call; sleep and wake.
- Calls longer than a few minutes.

Not built yet: lines appearing while the call runs (the transcript is written at the end), telling the other speakers apart (needs a model download), and detection driven by events (the spike looks once a second). The summary and to-dos were added on 12 November; see "Summary and to-dos" below.

**Call notes in the island**

`spikes/start-forty.cmd` now also watches for calls. When an app is both using the microphone and playing sound, the island asks "Take notes on Chrome?" with Yes and Not now.

- **Nothing is opened or recorded before Yes.** Not now, or no answer within 15 seconds, means nothing is written, and the same call is not asked about again.
- **While recording,** the island shows a capsule for the whole capture: a steady red dot, the elapsed time and Stop. Recording also ends by itself when the call ends.
- **Afterwards,** the working indicator shows while the transcript and the notes are written, then "Notes ready" with an Open button when the file is saved in `spikes/out`.
- **How it is built.** The pipe between the two programs now carries lines both ways; the island sends accept, decline and stop. Text and buttons are ordinary XAML laid over the compositor-drawn body.

Verified with a scripted pretend call, using a test player as the "call app" and recording their side only. The watcher noticed it, the island asked, the test itself gave the yes and the stop, the notes were written, and a picture of each state was saved: `docs/island-call-ask.png`, `docs/island-call-recording.png` and `docs/island-call-done.png`. A separate run confirmed that the words spoken right before Stop are kept.

Not verified: pressing the buttons with a real mouse (the test called the same code the buttons call), a real call being noticed, and the microphone stream.

Found on the way:

- If the speech process disconnected right after finishing, the island skipped its "done" state. Fixed.
- Right after a restart, loading the English model took about 35 seconds instead of 3, because its files were not yet in memory. The speech process should load its model in the background at sign-in, not on first use.

Limits of this version: dictation is not shown in the island while a call is being recorded; there is no pause and no "never for this app".

**Summary and to-dos (12 November 2026)**

After the transcript is saved, a language model writes a summary, decisions, open questions and to-dos, and they go above the transcript in the same file. The code is `spikes/SpeechSpike/Summary.cs`. Try it with `spikes/summarize.cmd`.

- **Any model, chosen by settings.** One small client speaks the chat-completions format that Ollama, LM Studio, Sarvam and most hosted services share. It uses, in order: a service you name in `FORTY_LLM_URL`, `FORTY_LLM_KEY` and `FORTY_LLM_MODEL`; a model already running on this PC; and, only when started with `--cloud-notes`, Sarvam's chat model.
- **A call does not leave the PC unless you say so.** A hosted model gets the transcript text, never the audio, and only with `--cloud-notes`. The notes file names the model that wrote it.
- **The transcript is saved first.** A slow or failing model costs the summary, never the transcript.
- **The model's answer is checked, not trusted.** Every item must point at transcript lines that exist, or it is removed. A to-do's owner counts as certain only if that person speaks in the lines it rests on; otherwise it is marked unsure. A due date must appear word for word in those lines, or it is dropped. Whatever was removed is listed in the notes. Each item shows the times of its lines, so you can check it.
- **Island.** When the notes are saved it shows "Notes ready", what they hold ("3 to-dos") and an Open button, for seven seconds: `docs/island-notes-ready.png`.

Measured with Sarvam's `sarvam-105b-conversations` on two made-up calls, one of 20 lines in English and one of 13 lines in Hindi mixed with English:

| | English sample | Hindi sample |
|---|---|---|
| To-dos found | 5 of 5, in each of 5 runs | 3 of 3, in each of 3 runs |
| Owners and due dates | all correct | all correct, dates kept as spoken |
| Invented items | none | none |
| Time per call | 1.4–1.6 s | 1.2–1.3 s |

A scripted pretend call also ran the whole chain: the island asked, the recording was transcribed, the model wrote the summary, and the island showed "Notes ready". The audio was three audiobook sentences, and the notes correctly listed no decisions and no to-dos.

Cost: the English sample was 912 tokens sent and 200–370 written, measured before the instructions grew by roughly 130 tokens. At Sarvam's listed price (₹15 per million tokens sent, ₹60 written) that is about ₹0.03. An hour-long call should come to well under ₹1; that is an estimate, not a measurement.

Faults seen, and what was done:

- **Missed to-dos.** The first instructions found 4, then 2, then 1 of the to-dos across three runs on the same call. Telling the model to walk the transcript line by line fixed it on these samples.
- **Wrong details.** In 2 of 5 runs an open question said the demo video drops frames; the call said the island does. In 1 of 5 runs a proposal nobody agreed to was folded into a decision. Not fixed. The checker only confirms that the cited lines exist, not that the item says what they say. The times beside each item are how a reader catches this.
- **Wrong language.** Asked to use the call's language, the model wrote English notes for the Hindi call and translated its dates. The program now decides the language itself by counting letters, and tells the model.
- **Sarvam's larger model cannot be used for this.** On the same 20 lines, `sarvam-105b` spent 2,500, 3,000 and 8,000 tokens thinking and returned no answer each time.
- **Headings stay English.** "Summary", "To-dos" and "due" are English even in Hindi notes.

Not verified:

- A model on this PC. No local model program is installed here, so that path has never run.
- A real call. Real transcripts have speech errors, run long, and put several people under "Others".
- Long calls. The Sarvam model takes about 32,000 tokens, roughly two hours of English and less of Hindi. Nothing splits a longer call yet.
- The Open button pressed with a real mouse, and the step that opens the file.

**Routing: Sarvam and ElevenLabs together (12 November 2026)**

You asked to keep Sarvam, use ElevenLabs as well, and make the flow smooth with no lag or errors. The table in section 7.4 says which engine does which job. The code is `spikes/SpeechSpike/Router.cs`, `CloudEngines.cs`, `Keys.cs`, and `ScribeTranscriber` in `Calls.cs`.

- **No single service is something you wait on.** For Hindi dictation, Sarvam is asked first. If it has not answered after a head start (1.2 s for a sentence, growing with longer speech), ElevenLabs is asked as well and the first answer is typed. If both fail, the model on this PC answers. A service that just failed gets no head start for a minute, so a dead service costs one delay, not one per sentence.
- **Connections open while you speak.** Key-down opens the connection to the service, so the secure handshake is done before you let go.
- **Long dictation is worked on while you talk.** After twelve seconds, each pause of half a second hands what came before to the engine in the background. When you let go, only the last stretch is left.
- **Every request has a time limit,** sized to the length of the audio, and an unreachable service is noticed within four seconds.
- **Calls tell the other voices apart.** ElevenLabs returns each word with a voice label, so the others' side reads "Speaker 1", "Speaker 2". Silence is cut out before sending, because their API bills by audio length including silence. If ElevenLabs fails, the standby engine writes the transcript and the notes file says so.
- **The summary can name the voice.** A to-do's owner can be "Speaker 2", and is certain only if that voice speaks in the cited lines. A failed or unreadable summary is asked for once more.
- **Keys are entered once.** `spikes/setup-keys.cmd` asks for each key with typing hidden, checks it with the service, and stores it encrypted for your Windows account. Invisible characters picked up in a paste are removed. A wrong key is refused with a plain reason and is not stored.
- **One health check.** `spikes/check.cmd` checks the keys, both services, the models on this PC and the island, and prints who does what.

Measured on this PC, from India, with your keys held only in the test process:

| Hindi dictation, 10 test clips | Wait after speaking | Answered by | Word errors |
| --- | --- | --- | --- |
| Normal | middle 0.53 s, longest 1.45 s | Sarvam, all 10 | 2.5% |
| Sarvam down | middle 1.8 s | ElevenLabs, all 4 | 1.3% |
| Sarvam not answering | first 2.7 s, then 1.6 s | ElevenLabs, all 3 | 1.7% |
| Both services down | 1.6–3.7 s | Omnilingual on this PC, all 3 | 15.3% |

| Other measurements | Result |
| --- | --- |
| First Hindi sentence after start, connection opened at key-down vs not | 0.7–1.5 s vs 1.3–2.2 s (three runs each) |
| 49 s of English dictation | 4.6 s wait if done at the end; 1.1 s when worked on while talking; 0% word errors either way |
| 39 s of Hindi dictation through Sarvam | 1.5 s at the end; 0.7 s while talking; 1.3% word errors. Before this change Sarvam refused anything over 30 s |
| Two Hindi readers in one "call" stream (pitch about 197 Hz and 170 Hz) | ElevenLabs found 2 voices and labelled all 4 lines correctly; 35 s of stream, 29 s sent after cutting silence; 3.7 s |
| The same with ElevenLabs forced to fail | Transcript written by the standby engine, and the file says so |
| Summary of a made-up call with Speaker 1, Speaker 2 and You (3 runs) | The 3 owned to-dos went to the right voice every time; the fourth, which nobody took, was "Unsure who" in 2 runs and an open question in 1; 1.2–1.4 s |
| Full pretend call through the island (two Hindi readers, ElevenLabs, then Sarvam summary) | 19 s from the start of playback to "Notes ready"; Speaker 1 and Speaker 2 in the transcript; Hindi summary |
| Key store self-test | 6 of 6 checks: the file is unreadable as text, survives a restart, a damaged file is treated as empty, removing the last key removes the file |
| Wrong keys given to setup | Both refused as "the service rejected this key"; nothing stored |

Found and fixed on the way:

- A pasted key with an invisible character made the network library fail with a confusing message. Setup now strips such characters.
- The English test clips all turned out to be one reader, so they cannot test telling voices apart. The test uses the Hindi set, which has two.
- A transcript that fell back to the standby engine still named ElevenLabs as its engine. It now names who actually did the work.

Limits, plainly:

- **"No lag or error" is not something any setup can promise.** What this does is stop any single service from being the thing you wait on, and keep a transcript even when the services fail. The worst wait seen in these runs was 3.7 s, with both services down.
- **ElevenLabs keeps call audio.** Turning that off is for enterprise accounts. If that matters for a call, start with `--local`.
- **Your 40,000 credits.** ElevenLabs' pricing page says API use is billed in US dollars, not credits, at $0.20 an hour for Scribe. I could not read the account balance: the key lacks the permission to read the subscription. Check in the ElevenLabs dashboard whether API speech-to-text draws on the credits.
- **The first part of a pretend-call recording was clipped** ("वाल का यह…" instead of the full first sentence). That is the recording starting a moment after the audio, not the transcription; a real call starts long before you press Yes.
- **Not tried:** a real key press, your microphone, your voice, typing into your apps, a real call, Hinglish through the route, and calls longer than a few minutes through ElevenLabs.

### M1 — Island core (about 4 weeks)

Rest, sliver, capsule and panel; the activity queue; media; volume and brightness; battery and charging; device connect; timer; file Shelf; settings; tray; start at sign-in; full-screen rules; keyboard access; reduced motion.
**Done when:** you run it for a week without quitting it, and the budgets hold.

### M2 — Talk (about 3 weeks)

Dictation everywhere; Ask with context chips; model settings; the companion and its nine states.
**Done when:** you dictate 500 words a day for a week, the delay target holds, and text lands correctly in ten apps (Chrome, Edge, VS Code, Word, Notepad, Slack, WhatsApp, Teams, Terminal, a File Explorer rename).

### M3 — Remember (about 5 weeks) → public beta

Call detection, consented capture, live lines, notes with evidence links, the meeting list, text search, retention settings, the privacy view, Store submission.
**Done when:** over 20 real calls in four apps, at least 18 are detected, nothing is ever captured without a yes, and you judge at least 80% of to-dos correct.

### M4 — Day loop (about 4 weeks)

Calendar for Microsoft and Google (read, join, confirmed create); next-meeting capsule; daily brief; follow-up drafts; notification history and attention rules; clipboard history with exclusions; the approval surface shared by assistant actions and Claude Code; the action log.
**Done when:** for one work week, every follow-up you actually made started from a Forty draft.

### M5 — 1.0 (about 4 weeks)

Onboarding; ink, glass and theme-aware looks; Hindi and English; accessibility pass; crash-safe recovery; pricing and entitlements; website with live demo; Store listing.

### After 1.0

Nearby transfer; semantic search across meetings; our MCP server; more coding agents; teleprompter; a few widgets; optional wake word; phone companion; Windows agent connectors.

### Next steps

Done: `~/forty` has its own git repository (nothing committed yet), and S1, S2 and S4 have run.

1. Try the island by hand with `spikes/run-island.cmd` and say how it feels. That judgement is the real S2 pass line.
2. Speak into `spikes/speak.cmd en` and `spikes/speak.cmd hi`, including a Hinglish sentence. That is the real S4 test.
3. Install Visual Studio Build Tools with the C++ workload. S3 cannot start without it.
4. Settle frame pacing: PresentMon on a quiet desktop, then a 120 Hz or 144 Hz monitor. This laptop's panel is 60 Hz.
5. Try `spikes/start-forty.cmd` in your real apps. The key press, the microphone and typing into your apps are the parts nobody has tried yet.
6. With `spikes/start-forty.cmd` running, start a real call. Check that the island asks, press Yes, press Stop, and read the transcript and its echo score. Try speakers and a headset, and more than one call app.
7. Done 12 November: new Sarvam and ElevenLabs keys are stored encrypted for your Windows account, and `spikes/check.cmd` reports both working. Re-run the Hindi route on them: middle wait 0.63 s, longest 1.05 s, 2.5% word errors; two voices told apart; summary owners correct.
8. Check in the ElevenLabs dashboard whether API speech-to-text draws on your 40,000 credits or is billed separately. The new key also lacks the "user read" permission, so the program cannot see the balance.
9. On a real call with `start-forty.cmd`, press Yes and Stop, then mark each to-do in the notes right or wrong. The M3 pass line is 80% correct.

---

## 9. Distribution, pricing and website

**Distribution.** Microsoft Store only until there is revenue. It is free to register, Microsoft signs the package, updates are handled, and winget can install Store apps. A direct download needs a code-signing certificate from a commercial authority, because Azure Artifact Signing is closed to individuals outside the US and Canada. Never tell users to click through SmartScreen. Build x64 and ARM64.

**Pricing (recommendation; your decision).**

- **Free:** the whole island, plus dictation and call notes on local models or your own endpoint. Local AI costs nothing to serve, so giving it away buys trust and reach.
- **Plus, about $5/month with an India price near ₹149:** hosted models with no setup, search across all past meetings, the daily brief and follow-up loop.
- Billing through the Store, which handles payments, tax and payouts.

Reference points: island utilities sell for $2–10 once; Airlock charges €4–8/month; Syllo $7.99/month; Wispr Flow about $15/month and about a third of that in India.

**Website.** Next.js. A live island demo built with the same spring tokens, a page per job (Talk, Remember, Glance), a plain-language page on what leaves the PC for each feature, pricing, release notes, and one Store button.

---

## 10. Risks

| Risk | What reduces it |
| --- | --- |
| Airlock ships a Windows version | Be out first with the three jobs; go deeper on Windows specifics (per-app capture, ARM64, Store signing) |
| Microsoft builds the same into the taskbar | Stay complementary: local-first, any model, a calmer surface; plug into Windows connectors instead of competing with them |
| Echo and speaker separation are poor on speakers | S5 before any UI work; headphone prompt; mark uncertain speakers |
| WinUI 3 memory or AOT friction | S3; micro-shell fallback |
| Local models are weak on modest PCs | Dictation works with no language model; small-model tier; hosted option |
| Recording law, clipboard and notification privacy | Consent on every capture, visible indicator, exclusions on by default, the privacy view, a lawyer's read before 1.0 |
| Games and anti-cheat | No keyboard hooks, hidden in exclusive full screen, per-app exclusions |
| Scope creep toward 20 widgets | The "removes an app switch" rule in 4.3 |
| One person building everything | Milestones that each end in something you use daily |

---

## 11. Decisions that are yours

1. **Name.** "Forty" is a placeholder. Check trademarks before the Store listing.
2. **Hosted model provider.** Recommendation: a model broker during development, then choose by scoring on your own recordings. For call notes, Sarvam's `sarvam-105b-conversations` already works with the key you have (section 8, "Summary and to-dos"); the code takes any other service through three settings, so nothing is locked in.
3. **Pricing shape.** Recommendation in section 9.
4. **One companion or four agents at launch.** Recommendation was one; superseded by your 150-feature list. ROADMAP.md section 9 now asks about the cast instead.
5. **Hindi and Hinglish dictation as a launch feature.** S4 says it is feasible today only through Sarvam's API, as an opt-in. Recommendation: yes on that basis, once your own voice confirms the scores.
6. **Open source.** Airlock's source is public, as are D-Island and Pillar. Recommendation: decide before the public beta; opening the hook bridge and the meeting file format costs little either way.
7. **The earlier code.** If `xero AI` survives somewhere, tell me where.

---

## 12. Sources

Product pages (read 10 November 2026; vendor claims unless noted):
[Airlock](https://useairlock.app/) · [pricing](https://useairlock.app/pricing) · [changelog](https://useairlock.app/changelog) · [call notes](https://useairlock.app/features/call-notes) · [dictation](https://useairlock.app/features/dictation) · [coding agents](https://useairlock.app/features/coding-agents) · [privacy](https://useairlock.app/privacy) ·
[NotchNook](https://lo.cafe/notchnook/) · [NotchNook Pro](https://lo.cafe/notchnook/pro/) ·
[Nusa](https://rama2555.github.io/nusa/) ·
[Syllo](https://trysyllo.app/) · [docs](https://trysyllo.app/docs/) · [privacy](https://trysyllo.app/privacy/) ·
[Notchify](https://notchify.app/) · [Notchify comparison](https://notchify.app/en/compare/) ·
[D-Island](https://software.ibuildrigs.com/open-source/d-island/) ·
[NotchPixel guide to 13 apps](https://notchpixel.com/guides/dynamic-island-for-windows) ·
[notchy.dev overview](https://notchy.dev/dynamic-island-windows/) ·
[Pillar](https://github.com/warpirate/pillar-dynamic-island-for-windows) ·
[Windows Central on Notchify](https://www.windowscentral.com/software-apps/notchify-brings-macos-style-dynamic-island-flair-to-windows-11)

Adjacent products:
[Wispr Flow pricing](https://www.getvoibe.com/resources/wispr-flow-pricing/) · [Wispr India price change](https://usagepricing.com/blueprint/activity/wisprflow-2026-07-22-price-change) · [Granola pricing](https://www.activepieces.com/blog/granola-ai-pricing-2026-plans-costs-and-limits.md)

Windows platform:
[Windows App SDK 2.0 notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0) · [release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels) · [system backdrops](https://learn.microsoft.com/en-my/windows/apps/develop/ui/system-backdrops) · [WinUI Gallery 2.9 and SDK 2.0](https://visualstudiomagazine.com/articles/2026/05/01/winui-3-gallery-2-9-highlights-windows-app-sdk-2-0-for-app-developers.aspx) · [Composition animations](https://learn.microsoft.com/en-us/windows/uwp/composition/composition-animation) · [spring animations](https://learn.microsoft.com/en-us/windows/apps/develop/composition/spring-animations) · [WinUI 3 AOT start-up timing](https://zenn.dev/suusanex/articles/f642d91df412c9) · [Microsoft on WinUI memory](https://windowslatest.com/2026/07/27/microsoft-admits-windows-11-native-apps-hog-ram-promises-a-winui-performance-boost-before-start-menu-rewrite) · [.NET 11 schedule](https://visualstudiomagazine.com/articles/2026/08/19/net-11-preview-7-turns-on-nativeaot-cli-and-msbuild-server-by-default.aspx) · [notification listener](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener) · [WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) · [application loopback capture sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/) · [Voice Clarity on all Windows 11 PCs](https://windowscentral.com/software-apps/windows-11/all-windows-11-pcs-will-soon-support-this-ai-feature-once-exclusive-to-surface-no-npu-required)

Distribution:
[free Store registration, individuals](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/) · [free Store registration, companies](https://blogs.windows.com/windowsdeveloper/2026/05/07/publish-to-microsoft-store-as-a-company-now-with-free-registration-and-faster-onboarding/) · [code-signing options and Artifact Signing eligibility](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) · [Artifact Signing quickstart](https://learn.microsoft.com/azure/artifact-signing/quickstart)

AI and speech:
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) · [its .NET examples](https://github.com/k2-fsa/sherpa-onnx/tree/master/dotnet-examples) · [NuGet package](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx) · [open speech models compared](https://northflank.com/blog/best-open-source-speech-to-text-stt-model-in-2026-benchmarks) · [local speech models on the same audio](https://localaimaster.com/blog/best-local-speech-to-text-models) · [Foundry Local general availability](https://techcommunity.microsoft.com/t5/azure-arc-blog/bringing-ai-to-the-factory-floor-with-foundry-local-now-in/ba-p/4509951) · [Foundry Local on more silicon](https://devblogs.microsoft.com/foundry/foundry-local-meets-more-silicon/) · [Microsoft Agent Framework 1.0](https://startdebugging.net/2026/04/microsoft-agent-framework-1-0-ai-agents-in-csharp/) · [small local models](https://localaimaster.com/blog/small-language-models-guide-2026)

Agents and transfer:
[Windows agent connectors, Insider build notes](https://blogs.windows.com/windows-insider/2025/12/05/announcing-windows-11-insider-preview-build-26220-7344-dev-beta-channels) · [MCP on Windows](https://www.windowscentral.com/software-apps/windows-11/windows-11-model-context-protocol-mcp-ai-announcement-build-2025) · [agents on the taskbar (secondary source)](https://enterprisedna.co/resources/news/microsoft-windows-11-agentic-taskbar-mcp-2026) · [Codex hooks guide](https://codex.danielvaughan.com/2026/04/15/codex-cli-hooks-complete-guide-events-policy-patterns/) · [LocalSend protocol](https://github.com/localsend/protocol) · [Rive C# runtime status](https://rive.app/docs/runtimes/community-runtimes/c-sharp) · [the alpha WinUI Rive package](https://www.nuget.org/packages/XamlToolkit.WinUI.Rive.Native)

Not re-checked today, from my own knowledge: the process-loopback capture API and its Microsoft sample, raw input for key detection, `SHQueryUserNotificationState`, and `SetWindowDisplayAffinity`. Each is exercised by a spike before anything depends on it.
