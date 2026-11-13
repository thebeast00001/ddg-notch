# Forty — motion and interaction spec

*Companion to [PLAN.md](PLAN.md). The principles come from Apple's design talks (chiefly "Designing Fluid Interfaces"), translated here to Windows Composition. Numbers are starting values to tune in spike S2, not final.*

Sizes are in device-independent pixels (DIPs): 1 DIP is one physical pixel at 100% display scaling and 1.5 at 150%.

---

## 1. What it should feel like

1. **It answers the instant you touch it.** Something changes on the same frame as pointer-down or pointer-enter.
2. **It can be interrupted at any moment.** Reverse a morph halfway and it turns around smoothly from where it is, at the speed it was going.
3. **It moves like one object.** The island is a single body that changes shape. Content appears inside it; nothing slides in from elsewhere.
4. **It comes from where it lives and goes back the same way.** Everything grows from the top centre and returns there.
5. **It is still when nothing is happening.** No loops, no breathing, no glow.

If a choice in this file conflicts with one of these five, the five win.

---

## 2. Surfaces and sizes

| Surface | Size (DIPs) | Corner radius | When |
| --- | --- | --- | --- |
| Sliver | 56 × 4, touching the top edge | 2 | A maximised window is in front and nothing is happening |
| Resting pill | 148 × 32, 6 below the top edge | 16 | Default rest |
| Activity capsule | 200–320 × 36 | 18 | One live activity: leading slot (art or icon, 24), trailing slot (value, level, time) |
| Satellite | 36 × 36, 8 to the right of the capsule | 18 | A second live activity |
| Expanded panel | 360–440 wide, height fits content (120–260) | 32 | Hover dwell, click, shortcut |
| Full page | 480 wide, up to 640 or 70% of the work area | 36 | Call notes, Ask, clipboard, meeting list |

- All surfaces share one horizontal centre and one top anchor.
- The pill has 8 DIPs of invisible hit padding. When the last input was touch, every target is at least 44 DIPs and the capsule is 44 tall.
- The part of the window that draws and receives the pointer is always the shape's bounding box plus a margin for overshoot. It is never a full-width transparent strip.

---

## 3. Spring tokens

Every transition is a spring described by two numbers. **Damping ratio:** 1.0 settles with no overshoot; lower overshoots. **Response:** roughly how long it takes to arrive, in seconds. A spring has no fixed duration.

| Token | Damping | Response | Used for |
| --- | --- | --- | --- |
| `press` | 1.0 | 0.15 | Pointer-down scale, hover swell |
| `quick` | 1.0 | 0.25 | Value changes in a HUD, icon swaps, sliver ↔ pill |
| `move` | 1.0 | 0.38 | Any expand or collapse started by hover, click, shortcut or a system event |
| `thrown` | 0.8 | 0.35 | Anything released from a drag or flick |
| `sheet` | 0.8 | 0.30 | The full page pulled down and let go |
| `turn` | 0.8 | 0.40 | Rotation |

**The bounce rule.** Overshoot is used only when your own gesture carried momentum. A capsule that appears because a song changed does not bounce; a panel you flicked open does. An optional "Lively" setting lowers damping to 0.85 for system-started motion, for people who want more bounce. It is off by default.

**For our own solver:** with mass 1, stiffness = (2π ÷ response)², damping = 4π × ratio ÷ response. A `move` spring settles to within 1% in about 0.37 s.

**For Composition's built-in springs (measured in S2):** `DampingRatio` is the same number. `Period` is 1/ω, which is response ÷ 2π, not a full cycle. `InitialVelocity` is in units per second.

---

## 4. The motion engine

Five rules, enforced in code and not left to each call site:

1. **Start from what is on screen.** A new animation begins at the property's current displayed value, never at the previous target.
2. **Carry velocity.** The new spring starts at the speed the old one had. Without this, a reversal looks like hitting a wall.
3. **Retarget, never queue.** A new target replaces the old one at once. Nothing waits for an animation to finish.
4. **One spring per axis.** Width, height, X and Y are separate springs, so a diagonal throw does not desynchronise.
5. **Input is never locked.** Every element stays interactive mid-transition.

### How it is built: additive springs (decided by spike S2)

This is the technique behind iOS's interruptible animations. A change of target never replaces what is running:

1. The target moves to its new value at once.
2. A new decaying term is added that starts out exactly cancelling that move, with no speed of its own.
3. Every earlier term keeps running untouched.

`value = target + sum of terms`. Each term is `e^(−ζω·t) · (a·cos(ω_d·t) + c·sin(ω_d·t))` on its own clock, where `ω = 2π ÷ response`, `ω_d = ω·√(1 − ζ²)`, `a = old target − new target` and `c = (kick + ζω·a) ÷ ω_d`. The kick is zero for an ordinary change and the pointer's velocity for a drag release. A damping ratio of 1 is evaluated at 0.9999, where the formula is indistinguishable from the critically damped one.

On the compositor each term is an expression animation driven by a linear clock. Sixteen slots are reused in rotation; by the time a slot comes round again its term has decayed to nothing. The app keeps the same terms in C# so it knows roughly where the value is for hit-testing and snapping, but nothing on screen depends on that copy being exact.

**Why this one.** A change made by the app reaches the screen about 43 ms later, and that delay varies by 6–9 ms from one change to the next. Any design that works out the new starting position in the app is exposed to that variation. Additive terms are not: the target change and its cancelling term arrive in one commit, so position and velocity are continuous whenever it lands.

Measured on the same script (200 random interruptions and 5 drag releases, 60 Hz):

| Engine | Position jump at an interruption |
| --- | --- |
| A. Built-in spring, speed supplied from an app-side mirror | None (max 0.15 px) |
| B. Closed-form formula restarted from an app-computed position | Median 4 px, 95th percentile 45 px, max 114 px |
| C. Additive springs | None (max 0.06 px) |

These figures come from the model fitted to a screen capture. A and C are continuous by construction and the capture agrees with them to a median of 1.4–2.6 px. C is preferred to A because A's speed handoff still relies on the app's estimate and it reached the screen about 10 ms later.

**Two rules that came out of the measurements**

- Act on input immediately. A change made inside the frame callback reached the screen about 22 ms later than one made straight away.
- A built-in spring that replaces a running one starts at zero speed. Use built-in springs only for one-off motion that is never interrupted.

**Direct manipulation.** While you are dragging, the target follows the pointer 1:1 and all terms are cleared. On release, one term is added with the pointer's velocity as its kick.

**Still open: pacing.** About 70% of fast-moving frames landed within ±2 ms of the model. About 1 in 8 was 4–24 ms late. The cause is not isolated (PLAN.md, "Open after S2").

**Test (the S2 pass line):** `FORTY_SPIKE=torture` retargets the body at 200 random instants and compares every composed frame with the model. The model must need no position jump to explain the screen, and you must not be able to see one by hand.

---

## 5. Transition catalogue

| Transition | Trigger | What moves | Spring |
| --- | --- | --- | --- |
| Rest → Peek | Pointer enters the hit area | Pill scales to 1.04 and leans up to 2 DIPs toward the pointer | `press` |
| Peek → Panel | Pointer has moved at least 3 DIPs inside, then dwells 80 ms moving slower than 600 DIPs/s; or click; or shortcut | Body width, height, corner radius; content swap (below) | `move` |
| Panel → Rest | Pointer outside for 300 ms, Esc, click elsewhere | The same path in reverse; content leaves first | `move`, response 0.32 |
| Rest → Capsule | A live activity begins | Body widens; leading and trailing slots fade and scale in from the centre | `move` |
| Capsule → Capsule | The top activity changes | Body retargets its width; old slots blur out, new ones blur in | `quick` |
| Capsule + Satellite | A second activity starts | A circle separates to the right with a liquid bridge that thins and snaps | `move` |
| Panel → Full page | Open notes, Ask, clipboard | Body grows downward from the same top edge; shared elements travel | `move` |
| Value change | Volume or brightness key, scroll | The bar tracks the value; a held key retargets one spring and never restarts it | `quick` |
| Sliver ↔ Pill | Front window maximised or restored; activity begins | Width, height, top offset | `quick` |
| File approaches | A file is dragged within 300 DIPs of the island | Pill widens to 220 and opens toward the cursor, showing the Shelf glyph | `move` |
| File dropped | Release over the island | The item flies to its Shelf slot at the speed it was released | `thrown` |
| Full page pulled | Drag down from the panel | Tracks the pointer 1:1, resists past the limit, settles where the throw was heading | `sheet` |
| Dismiss an activity | Swipe up or sideways on the capsule | Follows the swipe; settles on dismiss or snaps back by the direction of the release velocity | `thrown` |

### The expand, step by step

1. **0 ms.** The window grows to the panel's bounds (invisible). The body starts `move` on width, height and corner radius from the top centre.
2. **0–120 ms.** Pill content leaves: opacity to 0, scale to 0.9, blur 0 → 6. Exits are faster than entrances.
3. **From 90 ms.** Panel content arrives: opacity 0 → 1, scale 0.94 → 1, blur 8 → 0, on `move`. Children follow 25 ms apart, at most four steps; later children arrive together.
4. **Shared elements do not fade.** Album art, timer digits and the companion travel from their pill slot to their panel slot on their own springs.

Collapse runs the same steps backwards: content out first, then the body.

**Why the dwell.** A zero-delay hover-open is why rival pills get in the way of browser tabs. The swell in step "Rest → Peek" is the instant answer; the expand waits 80 ms and ignores a pointer that is merely passing through. A click never waits.

**Why the pointer must move.** The island often changes shape by itself: an activity starts, or ends, under a pointer that was simply resting there. That is not a hover. The expand is armed only after the pointer has actually moved 3 DIPs inside the island, and it is disarmed again whenever an activity ends.

### Dictation

Key down → the pill becomes a listening capsule (`quick`) and a level meter follows your voice with no added smoothing. Key up → the meter folds into the working indicator. Text inserted → a check draws once, and the capsule returns to rest 0.8 s later. A hold shorter than 0.3 s does nothing.

### Approval request

The capsule arrives on `move` with the amber accent and stays until it is answered or times out. Expanded, it shows the project, the request and the exact command in a monospace face, with Approve, Deny and (where allowed) Always. It does not bounce or flash to get attention; it plays its sound once and waits.

---

## 6. Gestures and input

| Input | Gesture | Result |
| --- | --- | --- |
| Mouse | Hover | Peek, then panel after the dwell |
| | Click | Toggle panel immediately; feedback on button-down, action on button-up |
| | Right-click | Options: pin, edit, hide for this app |
| | Wheel over the capsule | Horizontal: switch activity. Vertical: adjust the value (volume, seek) |
| | Drag down | Pull the full page |
| Precision touchpad | Two-finger swipe over the island | Same as the wheel, with momentum |
| Touch and pen | Tap, long-press, swipe | Tap toggles; long-press is right-click; swipe dismisses or switches |
| Keyboard | Global shortcut | Open the panel with focus inside |
| | Tab, arrows, Enter, Esc | Move, act, close |
| | Push-to-talk key | Dictation while held |
| Drag and drop | File, text or link over the island | Shelf opens toward the cursor |

**Feel details**

- **Hysteresis.** A drag commits to a direction after 10 DIPs of movement. Before that it could still be a click.
- **Grab offset.** A dragged item stays under the exact point where you picked it up.
- **Velocity.** Measured over the last 80–100 ms of pointer samples. A pause longer than 100 ms before release counts as zero.
- **Where a throw lands.** `projected = position + (velocity ÷ 1000) × d ÷ (1 − d)`, with `d = 0.998` and velocity in DIPs per second. The snap point nearest the projection wins, not the one nearest the release point.
- **Commit or cancel.** Decided by the sign of the release velocity, not by how far you dragged.
- **Edges.** Past a limit the surface keeps following with rising resistance: `offset = (x × size × 0.55) ÷ (size + 0.55 × |x|)`. It never stops dead.
- **Focus.** Hovering never takes keyboard focus from the app you are in. Clicking a text field or pressing the shortcut does.

---

## 7. Choreography rules

- One body moves; content materialises. Never slide content in from outside the body.
- Exits are shorter than entrances.
- Stagger no more than 25 ms per child and no more than four steps.
- Blur and scale together when content appears, so it reads as arriving and not merely fading.
- A shared element travels; it does not cross-fade with a copy of itself.
- One expanded surface and at most one satellite. Everything else queues by priority.
- A new event of lower priority never displaces what you are interacting with.
- Urgency is shown by size, persistence and a sound, never by bounce, shake or flashing.

---

## 8. Material, colour and type

**Ink (default).** A pure black body with a 1 DIP inner hairline at 10% white so it separates from dark title bars and wallpapers. No drop shadow at any size; the hairline does that job.

**Glass (after S6).** Acrylic inside the body shape, a brighter top hairline, text one weight heavier. Falls back to Ink when Windows' "Transparency effects" is off or battery saver is on. Never place one translucent layer on another.

**Colour roles (starting values on black):** primary text white 100%; secondary 60%; tertiary 40%; done `#3BD671`; needs you `#FFB020`; error and recording `#FF453A`. Accent tints come from album art for media only. Colour is never the only signal: every state also has a glyph and a text label.

**Type.** Segoe UI Variable, with its optical size left to follow the font size.

| Use | Size | Weight | Notes |
| --- | --- | --- | --- |
| Pill and capsule labels | 13 | Semibold | Tabular numerals for any number that changes |
| Panel title | 15 | Semibold | — |
| Body | 14, line height 20 | Regular | — |
| Caption | 12 | Regular, 60% white | Slightly positive tracking; never smaller than 12 |
| Hero numerals (timer) | 44 | Semibold | Tracking −2%, tabular |
| Commands | 13 | Cascadia Mono | — |

Tracking tightens as text grows and loosens as it shrinks; there is no single value for all sizes. Layout scales with Windows' text-size setting.

---

## 9. The companion

**Form is open.** Constraints for the design: built from six or fewer simple shapes so it can be drawn and animated by the compositor; readable at 20 × 20 DIPs; recognisable by silhouette and motion; not a human face, and not a cloud or a pair of sleepy eyes, which Airlock and Nusa already use. A starting concept to prototype: a single soft drop of light that squashes when it listens, circles when it works and resolves into a check when done.

**States.** The same nine names are used in the animation, the run log and the screen-reader label, so the face can never say "done" while work continues.

| State | What you see | Accent | Announced as | Sound |
| --- | --- | --- | --- | --- |
| Idle | Still. It turns slightly toward the pointer when the pointer comes near; otherwise nothing moves | — | — | — |
| Listening | Opens up; a level meter follows your voice | White | "Listening" | Soft tick |
| Working | Progress fills if it is known; otherwise one dot circles about once a second | White | "Working" | — |
| Using a tool | The tool's glyph beside it | White | "Using Calendar" | — |
| Waiting for approval | Holds still | Amber | "Needs your approval" | Two notes, once |
| Needs input | A question glyph | Amber | "Needs an answer" | Two notes, once |
| Done | A check draws once, then back to idle after 2 s | Green | "Done" | Soft note |
| Paused | Dimmed, pause glyph | Grey | "Paused" | — |
| Failed | A cross draws once; the reason is one line of text | Red | "Couldn't finish" and the reason | Low note |

Idle motion is always a reaction to you, never a timer, which keeps the "zero frames at rest" budget true. In battery saver the companion does not react at idle at all. No pulsing halos, no radar rings, no breathing loops.

---

## 10. Sound and haptics

- Five sounds: listen start, listen stop, needs you, done, timer alarm. All but the alarm are under 150 ms.
- A sound is triggered by the same event that starts its animation, so they land together.
- The listen-start tick matters most: it tells you the microphone is live without looking.
- Sounds follow system mute and Focus, have one switch and one level, and are never the only signal.
- Haptics on Windows are limited to pens and some touchpads. Treat them as an extra if a usable API turns up in S1.

---

## 11. Accessibility

| Windows setting | What changes |
| --- | --- |
| Animation effects off | Size changes happen in one step with a 150 ms cross-fade of content. No scale, blur, travel or overshoot. Lyrics swap by fading. The visualiser shows static bars or is off. The companion shows static glyphs |
| Transparency effects off | Glass becomes Ink |
| Contrast theme | System colours, a solid 1 DIP border, no translucency, no album-art tints |
| Text size | Type and layout scale together; the capsule grows instead of clipping |

- Every state and action is reachable from the keyboard. Focus is always visible.
- Narrator hears ambient changes politely and approvals assertively. The panel is a proper window with named regions.
- Nothing flashes, and no loop runs near one cycle every five seconds.
- Reduced motion still gives feedback: opacity and colour changes stay.

---

## 12. Performance rules

1. **Animate only what the compositor can change without layout:** offset, scale, opacity, clip and shape geometry, effect amounts. Animating a XAML `Width` or `Height` runs layout every frame; size the body through its geometry and lay content out once at its final size.
2. **Build the panel's content before it is needed,** while idle, so opening it does not stall the UI thread.
3. **No per-frame UI-thread work** except during a live drag.
4. **At rest, nothing animates and no timer runs faster than once a minute.**
5. **Decode images at the size they are shown.**
6. **The visualiser** computes the spectrum 30 times a second on a worker thread and gives each bar's spring a new target. The compositor interpolates to the display's rate; the UI thread draws nothing. It runs only while music is audible and the capsule is visible, and stops in battery saver.
7. **Pause everything** when the island is hidden for a full-screen app or the session is locked.

---

## 13. How to review motion

- Record the screen, or film it with a phone at 240 frames per second, and step through frame by frame.
- A debug switch slows every spring to one tenth speed.
- A scripted demo mode plays each state on command, so a review never depends on waiting for real events or touching the user's media and settings.
- Count dropped frames with PresentMon over 200 expands, at 60 Hz and on a high-refresh monitor.
- Run the S2 retarget torture test after any change to the motion engine.
- Look again the next day. Problems that were invisible on the day are obvious with fresh eyes.

---

## 14a. After your first review (12 November 2026)

You found the first build's motion flat. The surface was rebuilt in layers, following the choreography rules: one lead per event, overlap only where the order means something, never a stagger on things that happen often, exits shorter than entrances. Filmstrips of each transition are in `docs/p1-film-*.png`.

**Springs added**

| Token | Damping | Response | Used for |
| --- | --- | --- | --- |
| `pop` | 0.72 | 0.36 | Album art or a glyph arriving. Small things get more life than big ones |
| `lively` | 0.86 | 0.42 | The body growing from rest for a new activity: barely elastic, like the island on a phone |
| `lyric width` | 0.9 | 0.34 | The body following the width of each lyric line |
| `clear` | 1.0 | 0.16 | Old content getting out of the way, gone in about 90 ms |

**A new activity arriving (the music capsule, from rest)**

| Element | Role | Starts | Motion | Relationship |
| --- | --- | --- | --- | --- |
| Body | Lead container | 0 ms (the frame of the event) | width, height, corner on `lively` | Carries everything |
| Body tint and glows | Decoration | 0 ms | colour crosses over in 0.7 s; glows only fade | Together with the body |
| Cover or glyph | Lead content | ~40 ms after the content is built | fades in, grows from 0.55 on `pop` | Lands as the body settles |
| Title | Support | +40 ms | fades, rises 5 DIPs, grows from 0.97 | Overlaps the cover |
| Equaliser, value, battery gauge | Decoration | +60 ms | fade only (the gauge sweeps once on plug-in) | Together with the title |

**Changing between two rich states (a new song, a new activity)**: the old content clears on `clear` and drifts up 5 DIPs; the new content starts 95 ms later, once the old is gone. In a container that changes size, overlapping the two looked broken.

**Lyrics**: one gesture per line, never a per-word stagger, because it happens every few seconds. The body follows the new line's width at once. The old line rises 8 DIPs and clears; the new line rises 9 DIPs into place 95 ms later. Lines longer than 416 DIPs wrap to two lines and the island grows taller.

**Opening the panel**: the body starts growing on the frame of the click, to the height that kind of panel had last time. The content is built just after, at low priority, and arrives as one piece at 70 ms (fade, 0.97 → 1, 4 DIPs down to rest). It never staggers, because the panel opens many times a day. The album cover travels from its spot in the capsule to its spot in the panel, growing from 22 to 76 DIPs. If the content turns out taller or shorter than the guess, the height retargets smoothly.

**Hover depth**: the content leans up to 1.6 DIPs toward the pointer while the glows behind it stay put. **Press**: panel buttons shrink to 0.9 on pointer-down and spring back on release.

**Two exceptions to "still when nothing is happening"**, made at your request:

- **The fluid backdrop:** three glows in the album's colours drift on 13, 17 and 21 second loops.
- **The equaliser:** four bars, each on its own rhythm.

Both move only while music is actually playing and the island is visible. They stop when music pauses, when the island hides, in battery saver, and with Windows' animation effects off. Both run entirely on the compositor.

A loop also stops whenever the element that holds it hides, including fades to zero opacity. An invisible loop still makes the compositor draw every frame. A hidden equaliser left running by the start-up warm-up cost 2.7% of a core with nothing playing; its bars now come to rest as the row fades. After each UI change, check idle CPU on a live run with nothing playing.

**What deliberately doesn't move**: the glows on entrance (fade only), the panel's rows (no stagger), the words of a lyric line (no per-word stagger), the equaliser's position (it only fades in).

**Ambient motion is stepped, not animated (measured 13 November).** WinUI's in-app compositor costs about 2 ms of CPU for every frame it draws here, whatever the frame holds. A never-ending compositor animation therefore costs about 12% of a core at 60 Hz, even if it is four equaliser bars. The equaliser and the fluid colours are stepped 30 times a second through compositor values only (a property set the visuals are bound to), at about 6% of a core. Two rules follow:
- **Never step through XAML properties.** Each step wakes XAML's render pass, and at 24 Hz that cost more than the 60 Hz animation it replaced.
- **Everything that responds to you keeps running on the compositor at the display's rate:** morphs, presses, travel and the bridge. Only the slow loops that never end are stepped.

**Springs rest.** A spring that has settled stops its expressions and holds its value directly; the next move wakes it on the same frame.

## 14b. Fifth slice (13 November)

**The liquid bridge (F5).** The satellite leaves from inside the capsule's right end on `lively` (0.86, 0.42). Until it is 80% of the way out, the two are one liquid shape:
- **The neck:** the metaball join of the capsule's rounded end and the satellite's circle, in the body's own fill.
- **When it snaps:** once the gap is 70% crossed, so the satellite rests as a separate drop.
- **How it's drawn:** precomputed for 25 points of the journey and played by one path animation whose progress is bound to the satellite's spring. It cannot fall behind the drop or overshoot differently.
- **During the join:** the hairline steps back (it would draw a seam across the neck). The satellite's own edge appears from 80% out, and its glyph or cover fades in from 60%.
- **Leaving:** the reverse on `quick`. The drop shrinks as it is drawn back in and absorbed.

| Element | Role | Starts | Motion |
| --- | --- | --- | --- |
| Satellite drop | Lead | 0 ms | out of the capsule's end, `lively` |
| Neck | Carry | with the drop | bound to the drop's position |
| Satellite content | Support | at 60% of the journey | fade |
| Hairline | Static | — | steps back while joined |

**Shared elements (F4).** On opening the panel, every element the capsule and the panel share travels, on `move` like the body:
- the cover or glyph;
- the label, which becomes the title;
- the value, which becomes the header's value;
- for battery, the gauge and the percentage.

Each starts where it was in the capsule, at its capsule size, read from the laid-out panel, so nothing is guessed. The capsule's copies vanish at 70 ms, the moment the panel's copies start in, so each element is on screen once. Nothing travels back from settings, or out of lyrics (no capsule is showing then).

**Marks (F116).** A mark is red (`recording`, the error red; the highlight colour in a contrast theme). It arrives with the satellite's bridge when something else holds the capsule, as the dot at rest, or as the capsule's leading glyph while you dictate. The listening meter is four bars that ease to each new level in 90 ms: 20 readings a second read as one movement. It is stepped by the speech process's own reports, so it costs nothing when nobody speaks.

**Moving between displays (F8).** The body shrinks to the sliver on `quick` and the window fades in 160 ms. On the new display it fades in in 120 ms and grows from the sliver on `move`. It never slides across the gap between screens.

## 14. Never

- Take keyboard focus because the pointer passed over the island.
- Replay an animation on each repeat of a held key.
- Bounce, shake or flash for a notification.
- Run decoration in a loop.
- Block input while something is moving.
- Show "done" while work continues.
- Hide the indicator while the microphone or a call is being captured.
- Show a battery level, speaker name or task owner the system did not actually provide.
