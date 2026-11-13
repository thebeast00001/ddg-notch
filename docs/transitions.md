# Transition review (F2)

Every change between the island's nine states, recorded frame by frame and stepped through. The films come from `FORTY_DEMO=transitions` (72 films, each every composed frame of the first 0.52 s). The sheets in [`transitions/`](transitions/) show eight frames of each change, one sheet per starting state.

Each change was checked for: one body morphing (never a second shape), old and new rich content never visible together, no element shown twice or missing, no flash (content that fades and comes back), and blur on content that changes (out 0 to 6, in 8 to 0).

Reviewed 13 November 2026, on one 60 Hz display at 150%.

| from \ to | rest | notification | approval | music | lyrics | listening | charging | panel | dot |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **rest** | — | ok | ok | ok | ok | ok | ok | ok | ok |
| **notification** | fixed (1) | — | ok | ok | ok | ok | ok | ok | fixed (1) |
| **approval** | fixed (1) | ok | — | ok | ok | ok | ok | ok | fixed (1) |
| **music** | fixed (1) | ok | ok | — | ok | ok | ok | ok | fixed (1) |
| **lyrics** | fixed (1) | ok | ok | ok | — | ok | ok | fixed (3) | fixed (1) |
| **listening** | fixed (1) | ok | ok | ok | ok | — | ok | ok | fixed (1) |
| **charging** | fixed (1) | ok | ok | ok | ok | ok | — | ok | fixed (1) |
| **panel** | ok | fixed (2) | fixed (2) | fixed (2) | fixed (2) | fixed (2) | fixed (2) | — | ok |
| **dot** | ok | ok | ok | ok | ok | ok | ok | ok | — |

**What was fixed**

1. **Into the dot or rest:** the old content came back faintly, sharp, at about 200 ms. Its blurred stand-in ended after 140 ms while the content itself was still fading on a slower spring. Stand-ins for leaving content now stay until it has faded.
2. **From the panel into a capsule or a lyric:** the panel's title was still fading while the new content arrived. The panel's content now clears on the fast exit spring, and content arriving within 170 ms of the panel closing waits for it.
3. **From lyrics into the panel:** the outgoing line and the incoming panel were both faintly visible for about one frame. The panel's content now waits 115 ms after a lyric, instead of 70.

**States**

| State | What shows |
| --- | --- |
| rest | the resting pill |
| notification | an ambient capsule (a headset connected) |
| approval | an act-now capsule with two buttons |
| music | the music capsule with album colours |
| lyrics | a lyric line |
| listening | dictation with the live level meter |
| charging | the battery capsule with its gauge |
| panel | the expanded music panel |
| dot | Focus mode: the pill with a dot |
