# Seamdesk UI

The settings window, layout editor, tray menu and dialogs. They should feel like part of Windows 11: a utility that is
opened for a minute, set up and closed. Calm, familiar, and exact about the one thing nobody else shows well, the desk.

## Foundation

- **WPF Fluent** (`Application.ThemeMode = System`): it follows the system light or dark setting and accent colour.
  Standard controls keep Fluent's own templates; nothing is restyled for flavour.
- **Colour comes only from Fluent's theme brushes**, by `DynamicResource`, so light, dark and the user's accent all
  work. No hex in views, apart from the machine tints on the desk canvas below.
- **Icons:** Segoe Fluent Icons (the Windows 11 icon font), one size per context. No emoji, no text arrows as icons.
- One family, **Segoe UI Variable** (Fluent's default). Numbers on the desk and in codes use tabular figures.

## Type ramp (Fluent's own styles)

| Use | Style | Size / weight |
|---|---|---|
| Page title ("Machines") | `TitleTextBlockStyle` | 28 semibold |
| Section heading | `SubtitleTextBlockStyle` | 20 semibold |
| Row title, button text | `BodyStrongTextBlockStyle` / `BodyTextBlockStyle` | 14 semibold / 14 |
| Explanations, status | `BodyTextBlockStyle` with `TextFillColorSecondaryBrush` | 14 |
| Labels on the desk, badges | `CaptionTextBlockStyle` | 12 |
| Pairing code | 40 semibold, tabular, letter spaced | |

## Spacing (4 px grid)

| Token | px | Use |
|---|---|---|
| `xs` | 4 | icon to label, badge padding |
| `s` | 8 | between rows inside a card, button gaps |
| `m` | 12 | card padding (vertical) |
| `l` | 16 | card padding (horizontal), between cards' groups |
| `xl` | 24 | page margins, above a section heading |
| `xxl` | 36 | page title to first section |

Cards (`CardBackgroundFillColorDefaultBrush`, 1 px `CardStrokeColorDefaultBrush`, radius 8) group one thing each,
like Windows Settings rows: a title, one line of explanation, and the control on the right. Cards never nest.

## Semantic colour

| Meaning | Brush | Where |
|---|---|---|
| Selected, primary action, live crossing | `AccentFillColorDefaultBrush` | Save, the dragged machine's outline, touching edges |
| Connected | `SystemFillColorSuccessBrush` | status dot |
| Connecting / waiting | `SystemFillColorCautionBrush` | status dot |
| Problem (key mismatch, overlap) | `SystemFillColorCriticalBrush` (+ `...BackgroundBrush`) | inline message, overlapping monitors |
| Legacy security | `SystemFillColorCautionBackgroundBrush` + `SystemFillColorCautionBrush` | the badge, with its one-line explanation |

Status is never colour alone: each dot sits next to words ("Connected", "Reconnecting in 8 s").

## The desk canvas

- Monitors are drawn in true proportion at one scale that fits the whole desk with 24 px of margin. Each monitor:
  radius 4, a 1 px stroke, and a tint per machine; labels are the machine name, the display number and its
  resolution ("Display 2 · 1920 × 1080"), in caption size, clipped if the monitor is tiny.
- Machine tints (the only hex): light `#E8F0FB` `#EAF6EC` `#FBEFE6` `#F1EAFB`, dark `#22344A` `#22392A` `#41301F`
  `#33284A`, in machine order; this PC is always the first. They tell machines apart without competing with the
  accent, which is kept for selection and crossings.
- **Touching edges** are drawn as 4 px accent bars along the shared part of the edge, and listed under the canvas in
  words: "DESKTOP · Display 2, bottom → LAPTOP · Display 1 (1,920 px)". The list is computed from `Desk.Cross`, so it
  is exactly what the cursor will do.
- Drag snaps when an edge comes within 10 screen pixels of another machine's edge. Arrow keys nudge the selected
  machine 10 px (Shift: 1 px). Overlapping monitors turn critical red and Save is disabled with the reason.

## Copy

Plain and short, second person, sentence case. Say what happens, not how: "Copy and paste", not "Clipboard sync";
"Show a code" / "Enter a code", not "Pair (J-PAKE)". The one technical term in the main flow is **legacy security**,
always with its sentence: "Uses Mouse Without Borders' shared key, so it's only as safe as Mouse Without Borders."
Errors say what went wrong and what to do next.

## Motion

None beyond Fluent's own control transitions. The desk moves under the pointer, which is the only motion it needs.

## Screenshots

`docs/screenshots/<scene>-<light|dark>-<100|150>.png`, rendered offscreen by `tools/Shots` (see its header; it runs over
ssh on Windows) with the example desk: this PC with a portrait 1440 × 2560 and a 1080p, a laptop running Mouse Without
Borders under the 1080p, and a second Seamdesk PC that is offline. They use Windows 11's default blue accent and a drawn
title bar. Scenes: `machines`, `pairing-show-code`, `pairing-enter-code`, `pairing-wrong-code`, `mwb-peer` (the Mouse
Without Borders card: version, control, copy and paste), `mwb-add`, `layout`, `layout-overlap`, `shortcuts`,
`shortcuts-capture`, `shortcuts-warning`, `behaviour`, `about`, `tray-menu`, `mwb-running`.
