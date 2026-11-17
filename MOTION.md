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

**Form: eyes (decided 17 November, by you).** The earlier rule ("not a pair of eyes") is replaced: the island and every agent are faces made of two eyes, because you asked for characters with expressions. Our own design, our own code: each character's eyes have their own shape, and expressions are spring targets (section 14g).

The table below shows the meaning of each state; section 14g shows how a face expresses it.

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

Idle motion is a reaction to you, with one exception: a blink, now and then, while Forty is awake. Blinks stop when you're away (Forty sleeps), in battery saver and with reduced motion. No pulsing halos, no radar rings, no breathing loops.

---

## 10. Sound and haptics

- Five sounds: listen start, listen stop, needs you, done, timer alarm. All but the alarm are under 150 ms. The timer adds two of its own (14i): the ruler's tick and the start chime, both under the one "Ticks and chime" switch. The alarm always sounds.
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
- **Its colour:** with album colours on, the neck and the drop carry the capsule's glows, in the same places, so the colour pulls out of the capsule with them. From 60% to 95% out they fade, and the separated drop rests in the body's own tint. Before this, the neck and drop were the flat tint from the first frame, a visible seam against the glowing capsule for about 100 ms.
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

## 14c. Sixth slice (13 November)

**Blur (F2).** XAML has no blur, so a changing element is drawn by the compositor into a live surface, and a blur reads that surface. The blurred copy stands in for the element while the element's own entrance or exit plays inside it.

| Change | Blur | Over | Starts |
| --- | --- | --- | --- |
| Capsule content leaving (swap) | 0 → 6 | 120 ms | at once |
| Capsule content leaving (to rest or the dot) | 0 → 6 | 120 ms | at once |
| Capsule content arriving | 8 → 0 | 260 ms | with its entrance (40 or 95 ms) |
| Lyric line leaving | 0 → 4 | 100 ms | at once |
| Lyric line arriving | 6 → 0 | 220 ms | with its entrance |

Not blurred: the panel (its shared elements travel crisp), the satellite, the dot.

**Rule from the review:**
- **A stand-in stays until what it stands in for has finished fading:** 200 ms on the clearing spring, 380 ms on `quick`. Otherwise the sharp original reappears faintly when the stand-in goes.
- **Content arriving within 170 ms of the panel closing waits for the panel's content to clear;** that content leaves on the clearing spring.
- **The panel's content waits 115 ms after a lyric line, instead of 70.**

The full review is in docs/transitions.md.

**Touch and pen (F12, section 6).**
- **Tap:** acts on release.
- **Drag:** commits after 10 DIPs. The body's height follows the finger 1:1, and width, corners and top follow the same progress.
- **Past either end:** the body resists (rubber band, c = 0.55).
- **Release:** it goes by the release velocity's direction (by distance if the finger had paused), on `thrown`, carrying the finger's speed.
- **Mouse:** a click still acts on the press.

**Theme changes (F11).** The body's fill and hairline ease over 300 ms. The content's layer dips to 0 over 120 ms, is redrawn in the new colours, and returns over 180 ms. Text on a filled button uses the "on primary" role (black, or the window colour in a contrast theme), never a fixed black.

**Every display (F7).** Each other display shows a live copy of the island: the compositor draws the island's tree into a surface, and a window there shows that surface. There is nothing to choreograph. Every frame is the island's own frame.

## 14d. Buttons, icons and centring (13 November)

**One button.** Every button in the island is an `IslandButton`. WinUI's own button paints hover and pressed from theme resources, which is where the grey hover square came from. Here those are all transparent, and the button carries its own plate.

| Look | Rest | Hover | Pressed | Used for |
| --- | --- | --- | --- | --- |
| Quiet | none | 9% white | 16% white | previous, next, back, footer links |
| Soft | 10% white | 16% | 22% | toggles that are off, secondary answers, chips |
| Filled | white | 90% | 80% | play, primary answers, toggles that are on |
| Tinted | accent 18% | 26% | 34% | shuffle or repeat when on |

- **States:** the plate eases between states in 120 ms, and text or icon colours ease with it.
- **Press:** the button sinks on pointer-down, on `press` (critically damped, 0.15 s), by the same depth whatever its size. The longer side gives way about 3 DIPs: 0.95 for the 56 DIP play button, 0.985 for a wide chip, never below 0.94. On release it comes back on `release` (damping 0.75, response 0.30), the smallest give.
- **Disabled:** 35% opacity.
- **Keyboard focus:** a 2 DIP white ring 3 DIPs outside the plate, following its shape. The pointer never shows it.

| Button | Size | Corner |
| --- | --- | --- |
| Play / pause | 56 × 56 | round |
| Previous / next | 44 × 44 | round |
| Shuffle / repeat | 36 × 36 | round |
| Toggles | 30 tall, 12/14 padding | pill |
| Answers in the panel | 32 tall | pill |
| Capsule answers | 26 tall | pill |
| Footer links | 24 tall | pill |

**Icons.**
- **Transport and recording mark:** geometry, filled, centred on visual weight. The play triangle is centred on its centroid, so it looks centred rather than measuring centred. The recording mark is a ring (11% of its size) and a dot (50%), exactly concentric.
- **Font glyphs:** shown as `FontIcon`, which centres the glyph's em box. A glyph in a `TextBlock` rides on the font's line metrics and sits high or low.

**Text.**
- **A label in a fixed-height box** (capsule label and value, button text, a single lyric line, the footer) is centred on its letters: the box runs from cap height to baseline (`TextLineBounds.Tight`), and descenders hang below. Centring the line box instead leaves text riding high, which is what made lyric lines look a little upward.
- **Two-line lyrics** keep normal line bounds on an 18 DIP rhythm, because letter-tight bounds pack the lines on top of each other.

**Checked, not eyeballed.** `FORTY_DEMO=layout` runs the island off every screen. It renders each icon and label and measures its ink against its box, and captures the island's own window for the composed result.
- **Result:** within one device pixel (0.8 DIP at 125%) everywhere. Positions snap to whole pixels, so half a pixel is the limit.
- **Exception:** text can sit up to 1.2 DIP off sideways, from its letters' side-bearings.

## 14e. The switch, the play/pause morph and the agents' marks (13 November)

**The switch** (`IslandSwitch`, 42 × 24) is drawn by the compositor and behaves like a physical one.
- **Press:** the knob stretches from 20 to 26 DIPs toward the side it will travel to, before anything is decided.
- **Drag:** after 4 DIPs of movement the knob follows the finger 1:1 from where it was grabbed, and stops at either end.
- **Release after a drag:** the switch decides by where the throw would carry the knob (projected with deceleration 0.99), not by where the knob happens to be. The knob leaves at the finger's speed on damping 0.82, response 0.30.
- **Tap:** a tap released on the switch (10 DIPs of slack) flips it on a critically damped 0.30 s spring.
- **Track colour:** it follows the knob's position on every frame, so half-way is half-way.
- **Knob colour:** it inverts within a narrow window around the middle, so it never sits grey on grey.
- **On:** filled white with a dark knob, like every "on" control in the island. The state greens stay reserved for done.
- **Keyboard:** Space or Enter flips it.

**Play and pause** are one shape that changes, not two icons that swap.
- **The morph:** the triangle is cut down its middle, and each half squares up into a bar.
- **Corners:** they round as the halves become bars. The cut starts square, so the triangle shows no notch.
- **The dip:** the glyph draws in to 86% at the half-way point, so the change reads as one gesture.
- **How it runs:** 25 precomputed frames played by the compositor on a spring (damping 0.9, response 0.30). Pressing again mid-change turns it around.
- **Timing:** it changes on the press, not when the player answers. If the player disagrees, the rebuilt panel turns it back.
- **Persistence:** one glyph outlives the panel and moves into each rebuilt transport, so a change runs from wherever it was.

**The agents' marks** (F14, F15, F38):
- **Drawing:** each silhouette is two or three compositor shapes in a 20 × 20 box, in the agent's hue, crisp at any size. The roster shows them at 22 DIPs.
- **Poses:** every state is a set of spring targets (response 0.30), so states interrupt one another like everything else.

| State | Pose |
| --- | --- |
| Idle | still, 85% |
| Listening | opens to 108%, growing with the voice up to 122% |
| Working | the agent's own motion, at most one cycle a second (see below) |
| Using a tool | the working motion, with the tool's glyph on a white badge |
| Waiting for approval | still, with an amber badge |
| Needs an answer | still, with an amber badge and a question mark |
| Done | the silhouette shrinks away and a green check draws itself once (0.36 s); two seconds later, back to Idle |
| Paused | 40%, with a grey pause badge |
| Failed | a red cross draws itself, one stroke after the other |
| Speaking | the silhouette draws in and lifts, and a level line in the hue follows the reply beneath it |

- **Working motions:**
  - **Scribe:** the stroke writes itself, then lifts off from where it began (1.3 s).
  - **Scout:** the ring sweeps a quarter turn and back about its centre (1.4 s).
  - **Operator:** the square makes a quarter turn with a touch of overshoot, then rests (1.2 s).
  - **Planner:** the bars trade places (1.6 s).
- **When work stops:** the motion settles from wherever it is in 260 ms. A square part-way through a turn finishes the turn.
- **Badges:** ringed in the body's colour so they sit apart from the silhouette. They pop in on `pop` and leave at once.
- **Reduced motion:** nothing loops, every pose is reached in one step, and the check and cross appear whole.
- **Lifetime rule (learned the hard way):** a mark owns the springs its shapes follow. If the mark object is collected while its shapes are still on screen, the expressions stop and every hidden layer shows at full opacity. Marks are always held (the roster keeps them for the island's life).

## 14f. Asking and answering (13 November)

- **The ask field:** a 34 DIP pill plate at 9% white, brightening to 14% when focused (120 ms), with a search glyph and the hint "Ask Forty. Try “open Notepad”". WinUI's own text-box chrome is all transparent; the plate is the only chrome.
- **Keyboard:** the shortcut opens the panel with the cursor in the field. Enter asks. The first Esc clears what was typed, and the next closes the panel. When the panel is rebuilt while you're typing, the cursor stays in the field.
- **The answer:** under the field, like a reply.
  - **Layout:** a glyph in the accent colour, the title in 13.5 semibold, and a sentence in secondary.
  - **Buttons:** up to three, 28 tall: the first filled, the others soft.
  - **Narrator:** questions are announced assertively, everything else politely.
- **After an app opens:** like Spotlight, the panel closes and doesn't hand focus back, so the new app keeps it. "Opened Notepad" shows in the capsule for 2.5 s with a green check.
- **Pause:** every mark dims with a pause badge, the answer reads "Everything paused" with Resume, and the footer's "Pause all" becomes "Resume".

## 14g. Forty and the cast: faces (17 November)

**The characters.** Each face is two eyes in a 24 × 24 box, drawn by the compositor.

| Who | Job | Eyes | Hue | Manner while working |
| --- | --- | --- | --- | --- |
| **Forty** | The island itself; on show when nothing else is | Two tall soft capsules | White | — |
| **Echo** (was Scribe) | Words: dictation, calls, notes | Round | Blue `#6AA8FF` | Its eyes read left to right |
| **Iris** (was Scout) | Finding: questions, search, files | Tall, tilted in | Teal `#45D6CF` | Looks around |
| **Bolt** (was Operator) | Doing: apps, media, settings | Square | Coral `#FF8FA3` | Narrows its eyes and nods into it |
| **Tempo** (was Planner, phase 3) | Time: calendar, brief | Wide | Violet `#B394FF` | Drifts up, thinking ahead |

The old role names still work when you address an agent ("Operator, open Spotify" reaches Bolt). The role (`Id`) never changes, so history survives a rename.

**Expressions are spring targets.**
- **Eye springs:** width, height, position, tilt and a "smile" amount per eye.
- **Shared springs:** the gaze and the mouth.
- **Morphing:** any expression interrupts any other and the eyes morph to it: pose response 0.26, damping 0.92; smiles on a livelier spring (damping 0.78, response 0.30).

| Expression | Eyes |
| --- | --- |
| Neutral | Base shape |
| Perk | 8% wider, 12% taller, lifted (you came close) |
| Happy | Squash away into arcs (∩) of the same width |
| Surprised | 30% wider, rounder, lifted |
| Focused | Half height, lowered |
| Determined | 62% height, tilted inward like a frown |
| Sleepy | A quarter height, lowered |
| Asleep | Lines, lowered; a "z" drifts in |
| Sad | 80% height, outer corners drooping 13° |
| Curious | One eye squints, the other widens; looks up and right |
| Thinking | Looks up and right |
| Listening | Wider; opens with your voice (up to 22% taller) |
| Speaking | A mouth line under the eyes, its length following the voice |

**Gaze and blinking.**
- **Gaze:** the eyes move together, 2.2 units across and 1.6 up or down at most, on a quick critically damped spring (0.16).
- **The blink:** a fixed 200 ms gesture: the lids fall in 50 ms, hold 30 ms and lift in 120 ms.
  - **How often:** every 2.6 to 6.2 s, and two in a row about one time in five.
  - **Why keyframes, not a spring:** a spring would keep its clock running for seconds after each blink, so the island would never stop drawing.

**Forty at rest.** The face replaces the empty black pill.
- **Pointer:** when you come close it perks up, and its eyes follow your pointer while you hover.
- **Reactions:** something finished leaves the island and Forty smiles for 1.6 s; something failed and it droops.
- **Sleep:** after 2.5 minutes with no keyboard or pointer input anywhere (checked every 2 s, which costs nothing), it grows sleepy, then sleeps. Its eyes close to lines, a "z" drifts in, and nothing moves: no frames are drawn.
- **Waking:** your first input wakes it with a small start and a double blink.
- **Where it doesn't show:** never in the sliver over a maximised window, never while the panel is open. In battery saver it shows but doesn't blink.

**Agents appear when needed.** The roster strip is gone. An agent's face shows only:
- **In the capsule while it works.** Dictation is Echo's: its face, then "Listening…", the microphone's red mark (always shown while the microphone is on, F116), and the dot-matrix meter.
- **Beside its answer under the ask field.** For example, Bolt's happy eyes beside "Opened Notepad".

**The dot-matrix meter.**
- **Layout:** 3 rows by 9 columns of 3.4 DIP dots, 5.6 DIP apart.
- **Behaviour:** each reading is a column at the right, lit from the bottom up: one dot for a whisper, three for a raised voice. Earlier readings step left. Unlit dots stay at 22%.
- **Cost:** colours are set per reading; nothing animates between readings.

**Music with lyrics beside it.** With Lyrics on and synced lyrics found, the panel widens to 520 DIP.
- **Player (left):** 64 DIP cover, title, a progress row, and transport from 38 to 46 DIP, with the Lyrics and Colours toggles at its ends.
- **Divider:** a hairline.
- **Lyrics (right):** a 214 DIP column of 15 DIP semibold lines.
- **Brightness:** the line being sung is full white; the others are 42%, 26% and 16% by distance, easing over 280 ms.
- **Scrolling:** the column glides on `stream` so the current line sits a third of the way down, and lines fade where they meet the column's top and bottom edges.
- **Background:** the album's own colours, which already fill the body at any size.

## 14h. Thinking and answering (17 November)

- **Thinking:** under the ask field, Forty's face at 22 DIPs looks up and to the right, beside "Thinking…". It shows from the moment the request leaves until the model answers (0.3 s typically), and is replaced in place by the answer.
- **An answer:** the answering agent's face, then its name in 13.5 semibold.
  - **The words:** 13 DIP at 88% white on an 18 DIP line height, so a two-sentence answer reads as text, not a note.
  - **Where it came from:** in 10.5 tertiary, for example "Sarvam · cloud · 0.3 s".
- **When the panel is closed:** an answer or a failure opens it without taking the keyboard, and closes it once there has been time to read (9 to 32 s, by length). Moving the pointer into it, or a question waiting for you, keeps it open.
- **The hint:** the ask field reads "Ask Forty anything" when a model is connected and "Ask Forty. Try “open Notepad”" when only the built-in commands work.

## 14i. The timer and the mirror (17 November)

**The timer is Tempo's.** It uses Tempo's violet (#B394FF), not Apple's orange, which sits too close to the amber reserved for "needs you". In a contrast theme it becomes the highlight colour.

- **Setting it:** a page like Settings, opened from the ask field's timer button, by typing or saying "set a timer", or with a length ("set a timer for 25 minutes" starts it at once).
  - **Digits:** 58 DIP Segoe UI Variable Display Light, tabular, with the end time under them ("Ends at 4:32 PM").
  - **Ruler:** a strip of minute ticks (1 to 180) under a fixed 3 × 32 DIP violet line. Ticks are 10 DIPs apart; every fifth is long and labelled. Ticks fade toward the edges, and those within 36 DIPs of the line grow up to 1.5 times taller, like a lens.
  - **Start:** a full-width 40 DIP violet pill with dark text.
- **The ruler's physics (`Forty.Motion.RulerMotion`):**
  - **Drag:** it follows the pointer 1:1.
  - **Ends:** past either end it gives with the rubber band (constant 0.55, up to 90 DIPs) and never stops dead.
  - **Throw:** let go and the throw is projected (deceleration 0.998), snapped to the nearest whole minute, and carried there by a critically damped spring (response 0.32 s) that starts at the throw's own speed. It never swings back over a tick it already passed.
  - **Click:** a click on a tick goes there.
  - **Keys and wheel:** a wheel notch or an arrow key moves one minute, Page Up and Page Down five, Home and End to the ends. Quick presses add up.
  - **One number:** the compositor draws every tick, label and the lens from a single property, so a frame costs one write.
- **Each minute crossing the line:** a tick sound (12 ms, quiet, with a 0.1 ms attack so it stays a click) and the line's small give (1.25 × 1.12, back in 160 ms). Ticks are never closer than 28 ms apart, so a fast throw sounds like a soft ratchet, not a buzz.
- **Rolling digits:** a digit that changes slides out and its replacement slides in, 0.55 of the digit's height, with a fade, in 320 ms on (0.2, 0.9, 0.3, 1). Digits roll up when the number grows and down as it counts down; unchanged digits stay still. The countdown in the capsule doesn't roll: it changes on the second, in place, so a long timer costs one frame a second.
- **Running:** the capsule shows Tempo's face, "Timer" and the countdown in violet; Tempo's eyes perk for the last ten seconds and grow drowsy when paused.
  - **Opening the island while it runs:** the timer card leads the panel with Tempo, the live countdown at 20 DIP and round Pause and Cancel buttons. Pressing Tempo's side opens the timer page.
  - **On the timer page:** the countdown, a 4 DIP bar that drains on the compositor (one linear animation over the time left, nothing on our side while it moves), and Cancel, +1 min and Pause.
- **Time's up:** the alarm, a gentle four-note figure made here, loops for up to a minute. Tempo's eyes go wide (Surprised), then happy. The capsule offers Stop and +1 min; if the island is open, the page becomes "Time's up" with the same two.
- **The clock behind it** wakes on the second, when the digits change, and at the end; nothing runs in between. It uses the wall clock, so a timer set before the PC sleeps rings as soon as it wakes if its time has passed.

**The mirror.** A page with your camera, flipped like a mirror, in a 376 × 236 DIP frame with 18 DIP corners (a composition clip, since the video draws in its own swap chain).

- **The header:** a red dot and "Camera on" show only while frames are live.
- **One line under it:** "Only you can see this. Nothing is saved or sent, and the camera turns off when you close it, or after 2 minutes."
- **The camera runs only while the page shows.** Closing it, collapsing the island, opening another page, or two minutes passing releases it at once.
- **It stays open when the pointer leaves,** so you can look without holding the mouse still. A click on the island or Esc closes it.
- **If the camera can't be used:** "Camera access is off for desktop apps" with Open camera settings, "No camera found on this PC", or "Another app is using the camera" with Try again.
- **It never takes the camera from another app.** A free camera is opened normally; one already in use (the call you're about to join) is watched alongside it.

## 14j. Memory and the agents' pages (17 November)

- **One frame for every page under Settings.** A back button, then the page's own face (an agent's, at 34 DIPs and blinking) or nothing, then the title in 17 semibold with a line under it in 11.5 tertiary. Below is a list that scrolls under the island's rounded edge.
- **Direction follows the way you went.** Going to a page, its content fades in from 28 DIPs to the right. Coming back, the page you return to comes from 28 DIPs to the left. Opening straight from the capsule (a face, a card), the page grows with the body like Settings. The body changes height on the quick spring; nothing else moves.
- **Faces in a list hold still.** The agents' rows in Settings show each face at 24 DIPs without blinking; only a page's own face is alive. Every face on a page stops and lets go when the page goes.
- **Rows that open a page** are whole buttons: the face or glyph in a 26 DIP column, the name in 13, a line in 11 tertiary, and a chevron. The plate is quiet until hovered.
- **A skill row** adds what it risks under its sentence: a 10 DIP glyph and 10.5 tertiary words (reads only, on this PC, uses the cloud, can delete).
- **Interruption** is three chips, one filled. The sentence under them says what the chosen one does.
- **A memory** is its sentence in 13, then where it came from in 11 tertiary, then your words in italics, then Edit and Forget as quiet text buttons.
  - **Forget** turns the buttons into Forget (filled) and Keep, in place; nothing moves under you.
  - **Edit** puts the sentence in a field like the ask field, text selected: Enter saves, Esc puts it back.
  - **After forgetting,** a check and "Forgot “…” It's gone from this PC." lead the page until you leave it.
- **The suggestion card** is a reply like any other: Bolt's face, "Remember this?", the sentence, and Yes, Edit and No. It comes 2.6 s after the work it is about, so it never lands on the result, and at most once an hour. The panel opens to show it without taking the keyboard, unless that agent is set to Quietly or Brief only.
- **A refusal** (a skill or data source switched off) is one line with a lock glyph, "Turn on" filled and "Not now". Turning it on does what you asked.

## 14. Never

- Take keyboard focus because the pointer passed over the island.
- Replay an animation on each repeat of a held key.
- Bounce, shake or flash for a notification.
- Run decoration in a loop.
- Block input while something is moving.
- Show "done" while work continues.
- Hide the indicator while the microphone or a call is being captured.
- Show a battery level, speaker name or task owner the system did not actually provide.
