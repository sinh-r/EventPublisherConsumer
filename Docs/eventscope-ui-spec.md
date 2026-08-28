# EventScope — UI/UX specification

**Brief for Claude Design.** Produce an interactive HTML/CSS/JS mockup.

> **Read this first.** This mockup is a *visual and behavioural reference*, not
> production code. The real application is built in Avalonia 11 / XAML, so none
> of the HTML will be ported. What matters is layout, information density,
> component states, and interaction feel. Use static hardcoded data. Do not
> build data plumbing, do not call any API, do not implement real search.

---

## 1. What the app is

A desktop tool for developers who debug event-driven systems. It connects to
message brokers (Kafka, Azure Service Bus, AWS SQS), streams messages in
real time into a searchable grid, and publishes synthetic test events back.

The user is a backend engineer with a terminal open next to it. They are
looking for one message among four hundred thousand, at 11pm, under pressure.

## 2. Design principles

**Dense, not airy.** This is a professional tool, closer to Wireshark or
DataGrip than to a consumer app. Small type (12–13px in the grid), tight row
heights (26–28px), thin borders. Whitespace is a cost here, not a virtue.

**Dark mode is the primary theme.** Design dark first, light second. Both must
work.

**Monospace for data, sans for chrome.** Every ID, timestamp, payload, and size
figure is monospace. Labels, buttons, and menus are sans.

**Keyboard-first.** Every primary action shows its shortcut. The app should look
like it can be driven without a mouse.

**Status is always visible.** The user must never wonder whether it's still
connected, how much disk is left, or whether messages are being dropped.

**Colour is semantic only.** No decorative colour. Green = healthy stream,
amber = degraded or dropping, red = error or destructive mode, muted grey =
inert or evicted. Everything else is neutral.

Target viewport: 1600×1000. Must degrade gracefully to 1280×800.

---

## 3. Screen inventory

1. Connection manager (launcher)
2. Main workspace — consumer view
3. Main workspace — publisher view
4. Split view (both, resizable)
5. Add/edit connection dialog
6. Deep search overlay
7. Settings — storage and retention
8. Large payload confirmation

---

## 4. Main workspace layout

Vertical stack, top to bottom:

```
┌──────────────────────────────────────────────────────────┐
│ Tab strip                                          36px  │
├──────────────────────────────────────────────────────────┤
│ Connection toolbar                                 48px  │
├──────────────────────────────────────────────────────────┤
│ Warning banner (conditional)                       36px  │
├──────────────────────────────────────────────────────────┤
│ Search bar                                         44px  │
├──────────────────────────────────────────────────────────┤
│                                                          │
│ Message grid                                      flex   │
│                                                          │
├══════════════════════════════════════════════════════════┤ ← drag handle
│ Detail pane (collapsible)                          40%   │
├──────────────────────────────────────────────────────────┤
│ Publisher panel (collapsed by default)         0 or 45%  │
├──────────────────────────────────────────────────────────┤
│ Status bar                                         28px  │
└──────────────────────────────────────────────────────────┘
```

### 4.1 Tab strip

One tab per connection, not per view. Each tab shows a small status dot
(green streaming / grey idle / amber degraded / red error), the connection
name, and a close affordance on hover. A `+` at the right opens the connection
manager.

### 4.2 Connection toolbar

Left to right:

- **Source selector** — dropdown. Shows broker type icon-glyph, then
  `topic-name` or `queue-name`. For Kafka, a partition selector appears next to
  it; for ASB, a subscription selector; for SQS, neither. **These controls
  appear and disappear based on broker capability** — show all three variants in
  the mockup as separate states.
- **Read mode** — segmented control: `Peek` / `Consume`. `Peek` is default and
  selected. `Consume` is styled red-tinted when active. For SQS, `Peek` is
  disabled with a tooltip explaining why.
- **Start / Stop** — primary button, swaps label and colour based on state.
- **Throughput readout** — monospace, right-aligned: `12,431 msg  ·  847/s  ·  1.2 GB`
- **Saturation chip** — appears only when dropping:
  `⚠ 4,102 not displayed` in amber, clickable.

### 4.3 Warning banner

Only shown when read mode is destructive and the broker has no safe alternative
(SQS). Full-width, red-tinted background, one line:
*"Reading from SQS makes messages invisible to other consumers for the
visibility timeout. There is no non-destructive read."* with a dismiss `×` that
collapses it to a small persistent red dot in the toolbar.

### 4.4 Search bar

- Search input, full width minus the controls. Placeholder:
  `Search messages…  (⌘F)`
- **Scope selector** — segmented: `Live` / `Today` / `20 days` / `Deep`.
  Each shows a small latency hint on hover: instant / indexed / indexed /
  full scan.
- **Result count** — `1,204 of 431,882` in monospace.
- A thin progress line under the bar when an indexed or deep search is running.

Design an active-search state where matched substrings in the grid are
highlighted with a subtle amber background.

### 4.5 Message grid

Fixed columns, all resizable, all monospace:

| Column | Width | Notes |
|---|---|---|
| Time | 100px | `14:32:07.418` |
| Subject | 180px | truncate with ellipsis |
| Correlation ID | 260px | truncate middle, not end |
| Size | 70px | right-aligned, `4.2 KB` |
| Part | 48px | Kafka only, hidden otherwise |
| Preview | flex | first ~120 chars of body, single line, muted |

Pinned JSON fields append as extra columns with a small pin glyph in the header
and a `×` to unpin on hover.

Row states to design:
- default
- hover
- selected
- **large** (>64 KB) — size cell in amber, preview cell replaced with muted
  italic `payload not previewed`
- **evicted** — entire row at 55% opacity, preview replaced with muted italic
  `payload evicted`
- **dead-lettered** — thin red left-edge marker, 2px

Header row is sticky. Rows are 26px. Alternating row tint must be very subtle
(2–3% luminance shift), not zebra stripes.

Design the grid at realistic density — show at least 25 rows of plausible fake
event data so the mockup communicates how it actually feels.

### 4.6 Detail pane

Collapsible, drag-resizable, 40% height default. Tabs:

- **Body** — pretty-printed JSON with syntax highlighting and line numbers.
  Collapsible nodes. Each key row has a pin glyph on hover.
- **Properties** — two-column key/value table. Two sections with subheaders:
  *System properties* and *Application properties*.
- **Raw** — unformatted payload, monospace, no highlighting.

Pane header carries: `Copy body`, `Copy as cURL`, `Use as publish template`
(secondary buttons, with shortcuts shown), and a close `×`.

**Large payload state:** for messages over 64 KB, the Body tab shows a centred
placeholder instead of content:
> `4.2 MB payload` / `Load payload` button / muted line: *"Large payloads are
> not rendered automatically."*

**Evicted state:** centred muted message — *"Payload evicted to stay within the
2 GB storage cap. Metadata is still searchable."*

### 4.7 Status bar

Single line, 28px, monospace, muted, left to right:

`431,882 messages  ·  1.84 / 2.00 GB  ·  20-day retention  ·  index lag 340ms  ·  connected`

Include a thin horizontal disk-usage meter inline. When usage exceeds 90%, the
meter and figure turn amber.

---

## 5. Publisher panel

Shares the tab. Toggles into the lower region via `Ctrl+2` or a `Publish` button
in the toolbar. When both are open, a drag handle divides them.

Two columns:

### Left, ~60% — JSON tree editor

A table, one row per JSON node, indented by depth with connector guides:

| Key | Type | Value | Generator |
|---|---|---|---|

- **Key** — editable text, monospace. Indentation guides at 16px per level.
- **Type** — dropdown: `string` `number` `boolean` `null` `object` `array`
  `guid` `datetime` `enum`. The synthetic types (last three) render with a
  distinct subtle tint.
- **Value** — editable; disabled and muted when a generator is set.
- **Generator** — token input with autocomplete chips:
  `{{guid}}` `{{now:iso}}` `{{int:1..100}}` `{{pick:A|B|C}}` `{{ref:$.orderId}}`
  Render tokens as small rounded chips inside the input, not raw text.

Row hover reveals: add sibling, add child, delete, duplicate.

Design a tree at least 3 levels deep with 12–15 rows so nesting reads clearly.

### Right, ~40% — Preview

Tabs: **Payload** / **Envelope**.

- Payload: pretty-printed JSON with generated values resolved, generated fields
  highlighted with a subtle amber left-edge so you can see what's synthetic.
- Envelope: key/value table — content type, partition key, session ID, TTL,
  correlation ID, application properties.

A `Regenerate` button re-rolls generator values.

Footer: `Publish` (primary), `Burst ×` with a numeric stepper (default 100),
and a validation line — green check `Valid JSON · 1.4 KB` or red
`Invalid: unresolved {{ref:$.missing}} at line 8`.

---

## 6. Connection manager

Shown on launch and via the tab `+`. Two panes:

- Left: saved connections list, grouped by broker type, each with name,
  endpoint (truncated), and last-used timestamp.
- Right: detail/edit form for the selected connection, or an empty state
  offering three large buttons — `Kafka`, `Azure Service Bus`, `AWS SQS`.

The add/edit form differs per broker. Design all three:
- **Kafka** — bootstrap servers, security protocol dropdown, SASL mechanism,
  username, password, consumer group prefix, topic
- **ASB** — connection string or fully-qualified namespace + auth mode, entity
  type (queue/topic), entity name, subscription (topic only)
- **SQS** — region dropdown, queue URL, credential source dropdown, plus a
  persistent red-tinted note about destructive reads

Each form has a `Test connection` button with three states: idle, spinner,
result (green check with broker version detected / red with error text).

---

## 7. Deep search overlay

A modal-style panel, not a full dialog. Anchored below the search bar.

Shows: the query, a determinate progress bar, a live counter
`Scanned 412 MB of 1.84 GB · 87 matches · 3.2s elapsed`, results streaming in
beneath it as they're found, and a `Cancel` button.

---

## 8. Settings — storage and retention

Simple form, single column, max 720px wide:

- **Storage cap** — slider plus numeric input, 500 MB to 2 GB. Below it, a
  stacked horizontal bar showing current allocation: payloads / metadata /
  index, each labelled with its size.
- **Retention** — numeric input, days. Default 20.
- **Indexed body prefix** — segmented control: `Off` `512 B` `2 KB` `8 KB`.
  Below it, a live estimate that updates with selection:
  *"≈ 660,000 messages at current settings."*
- **Per-connection overrides** — a small table listing connections with their
  own indexed-prefix setting.
- **Session files** — a list of day files with sizes and a manual delete per
  row.

---

## 9. Component inventory

Build these as reusable pieces:

- Status dot (4 states)
- Segmented control (2–4 segments, with disabled segment support)
- Capability-gated control wrapper — renders disabled with an explanatory
  tooltip rather than hiding
- Token chip (for generators)
- Inline meter (disk usage)
- Toolbar button (primary / secondary / destructive, each with optional
  keyboard shortcut badge)
- Empty state block (icon-glyph, headline, one muted line, optional action)
- Toast (bottom-right, auto-dismiss, for `Copied` confirmations)

---

## 10. States to include in the mockup

Make these reachable, ideally via a small hidden dev-toggle panel:

| State | What it shows |
|---|---|
| Cold start | No connections. Connection manager empty state. |
| Connecting | Toolbar spinner, grid skeleton rows. |
| Streaming | The happy path. Rows arriving. |
| Paused | Stopped mid-stream, grid retains data, Start button restored. |
| Saturated | Amber chip, dropped-message count climbing. |
| Error | Red tab dot, banner with broker error text, retry button. |
| Empty search | Grid replaced by centred empty state. |
| Deep search running | Overlay with progress. |
| Near cap | Status bar meter amber at 96%. |
| Mixed grid | Rows including large, evicted, and dead-lettered variants. |

---

## 11. Explicitly out of scope

Do not design or build: charts, dashboards, analytics, message replay
timelines, topic browsers, schema registry UI, user accounts, onboarding tours,
or any animation beyond simple state transitions under 150ms.
