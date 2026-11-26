# Sharp Agent design

The visual design of Sharp Agent: tokens, typography, components and motion. Every new or changed piece of UI follows this file.
The app follows this design; where the app differs, this file wins. `current-ui.md` maps every surface to the XAML and code that build it.

## 0. Rules

- This file defines appearance only: it never changes features, Russian strings, tooltips, shortcuts or automation names.
- Change the look in this order: values in `App.xaml` / `Ui/Theme.cs` → shared styles (§7) → layouts.
- AGENTS.md applies: built-in WPF only, no NuGet, no downloaded fonts or icons, concise code.
- Sizes are WPF DIPs at 100 % scale.
- Icons: Segoe Fluent Icons (`IconFont`) at the sizes given here.

## 1. Principles

1. Flat neutral grays with almost no borders: surfaces differ by fill (title row and sidebar `#202020`, main `#181818`, raised `#292929`–`#353535`).
2. One blue for actions (`#2A64C3`), a brighter blue for highlights (`#3881F5`), a deep blue for the user's bubble (`#173D77`). Primary buttons are white with dark text, not blue.
3. System UI font at 14 px Regular nearly everywhere: sidebar, messages, tool rows, menus, composer. Hierarchy comes from color (white / `#D2D3D4` / `#8A8A8A`), not size.
4. A large rounded composer (radius 18) at the bottom of a 736 px column; rows and inputs use radius 8, cards and menus 12, dialogs 16.
5. Interactions are instant: hover, press, select, menus, tooltips, dialogs, panels and pages change in one frame. Motion is kept for the composer popover, streamed text and the "working" shimmer (§4).
6. Agent work is quiet: gray 14 px rows with an icon, no boxes or chips, collapsible groups, a shimmering status line instead of spinners.

## 2. Tokens

### 2.1 Colors

By brush key: dark values are in `App.xaml`, light values in `Ui/Theme.cs` (`LightColors`). Both columns were checked on screen.

| Key | Role | Dark | Light |
|---|---|---|---|
| Bg | Main column, settings content, right pane | `#181818` | `#FFFFFF` |
| ChromeBg | Title row, sidebar, rail and settings nav: one constant neutral color | `#202020` | `#F3F3F3` |
| Surface | Cards: settings groups, banner, question card, compact transcript | `#222222` | `#F4F4F4` |
| BlockBg | Command and diff blocks, file and changes cards | `#292929` | `#F4F4F4` |
| Surface2 | Menus, dialogs | `#292929` | `#FFFFFF` + RaisedLine edge |
| Surface3 | Hover/current item in menus; secondary button | `#3B3B3B` | `#E6E6E6` |
| RowHover | Current row in the sidebar and the settings nav: a veil over ChromeBg | white 7.5 % (`#313131`) | black 4.3 % (`#E9E9E9`) |
| ComposerBg | Composer input box; 1 px edge `#393939` | `#353535` | `#FFFFFF`, edge `#E0E0E0` |
| ComposerStrip | Strip above the composer (mode / folder) | `#272727` | `#F2F2F2` |
| Line | Dividers in cards, panel borders, window border | `#2E2E2E` | `#E5E5E5` |
| LineStrong | Input borders, menu edge in light theme | `#363636` | `#D9D9D9` |
| Text | Primary text, headings, menu items | `#F5F5F5` (headings `#FFFFFF`) | `#0D0D0D` |
| TextSoft | Chat titles in the sidebar, secondary labels | `#D2D3D4` | `#3C3C3C` |
| TextMuted | Secondary text on Bg, ChromeBg and Surface: tool rows, captions, section headers, "Выполнено за…" | `#8A8A8A` | `#686868` |
| TextDim | Secondary text on raised surfaces (Surface2, BlockBg, Raised, Fill, ControlBg, composer and its strip): shortcuts and descriptions in menus, block headers, the confirmations label, inactive strip tab, placeholders in the composer and inputs | `#A6A6A6` | `#5E5E5E` |
| TextFaint | Inactive window title, disabled glyphs | `#666666` | `#A0A0A0` |
| Accent | Send/stop circle, toggle on, focus ring | `#2A64C3` | `#2A64C3` |
| AccentText | Effort level name, links, slider fill | `#3881F5` | `#1F5FCC` |
| OnAccent | Glyph on Accent | `#FFFFFF` | `#FFFFFF` |
| UserBubble | User message bubble, text white | `#173D77` | `#DCE8FC`, text `#0D0D0D` |
| PrimaryBg / PrimaryFg | Primary buttons ("Сохранить", dialog OK) | `#FFFFFF` / `#181818` | `#181818` / `#FFFFFF` |
| Danger | Fill of destructive buttons (white text) | `#D63C41` | `#C42B1C` |
| DangerText | Destructive items in menus, error text and glyphs | `#F0787C` | `#BC2A1B` |
| Warning | "Без подтверждений", risky states (text and glyphs) | `#EC8145` | `#A84300` |
| Success | "+N" counts | `#3FC877` | `#0E7439` |
| DiffAdd | Added line bg | `#1F2F23` | `#E6F4EA` |
| DiffRemove | Removed line bg | `#3A1F22` | `#FBE1E3` |
| Backdrop | Dim behind modal dialogs | black 20 % | black 20 % |
| Raised | Elevated surfaces: tooltips, toasts, composer and sidebar popovers, inputs, float buttons, question-card choices | `#2D2D2D` | `#FFFFFF` |
| RaisedLine | 1 px edge of Surface2 and Raised surfaces | transparent | `#D9D9D9` |
| Fill | Active tab, table header | `#2D2D2D` | `#EFEFEF` |
| ControlBg | Dropdown button, changed-files pill | `#343434` | `#E9E9E9` |
| ComposerLine | Composer edge | `#393939` | `#E0E0E0` |
| Track | Slider and meter track, scrollbar thumb, disabled send circle, voice wave back, float-button edge | `#3A3A3A` | `#D4D4D4` |
| TrackStrong | Toggle off, scrollbar thumb hover, shortcut badge, drop-overlay frame | `#4A4A4A` | `#B4B4B4` |
| TextStrong | Headings on Bg | `#FFFFFF` | `#000000` |
| UserBubbleText | Text in the user bubble | `#FFFFFF` | `#0D0D0D` |
| DiffAddText | Added-line text | `#81D07E` | `#17722F` |
| DangerSoft | Error card | `#2B1D1D` | `#FDE7E9` |
| Shade | Shade behind the overlay sidebar | black 55 % | black 35 % |
| Scrim | Picture overlay backdrop | black 80 % | black 80 % |
| HoverLight | Hover veil of icon, ghost and round buttons: 10 % on hover, 18 % pressed (on Bg = `#2F2F2F`) | white | black |

Code and XAML use brush keys only, never literal colors, so the light theme follows; pure white is allowed only for switch knobs, the slider thumb and text on Danger. Literal values in §2.3–§5 map to keys: `#2D2D2D`, `#2C2C2C` → Raised (tabs and table header → Fill) · `#2F2F2F` → the HoverLight veil · `#343434` → ControlBg · `#383838` → LineStrong · `#393939` → ComposerLine · `#3A3A3A` → Track · `#4A4A4A` → TrackStrong · `#7A7A7A`, `#838383` → TextMuted · `#979797`, `#B0B0B0` → TextDim · `#D8D8D8`, `#E0E0E0` → TextSoft · `#E8E8E8`, `#EEEEEE`, `#F3F3F3` → Text · white headings → TextStrong · `#2B1D1D` → DangerSoft · focus ring and slider fill `#3881F5` → AccentText.

Every text color keeps at least 4.5:1 on the surfaces it is used on: that is why TextDim exists and why DangerText and Warning are lighter than the reference reds and oranges (`#E5484D`, `#E0662A`), and Danger darker. Muted text on a raised surface is always TextDim, never TextMuted.

Accent comes from the Windows accent color (`Theme.Apply`): Accent = that color, moved until it has 3:1 on Bg; AccentText = Accent moved toward white until 4.5:1 on Raised (dark theme) or toward black until 4.5:1 on Surface3 (light theme); UserBubble = Accent at 60 % over black (dark) or 15 % over white (light), darkened or lightened until UserBubbleText has 4.5:1. The blues in the table are the reference; the default Windows blue lands close to them.

Effort slider: fill AccentText, track Track, dots at 50 %, white thumb with a 1 px HoverLight ring at 30 % (visible only in the light theme); the top level ("Ультра") fills with a gradient AccentText → `#9E6FFE`. Toggle off: track TrackStrong, white knob.

### 2.2 Typography

WPF uses the Windows system message font with `Segoe UI, Helvetica, Arial, Global User Interface` as fallbacks. Code uses the `MonoFont` resource.

| Use | Size / weight / color | Line height |
|---|---|---|
| Empty-chat heading | 28 Regular, `#FFFFFF` | 36 |
| Dialog title, settings page title | 20 SemiBold, Text | 28 |
| Body: messages, sidebar items, menus, composer, tool rows, buttons | 14 Regular | 23 in messages, 20 elsewhere |
| Section headers (sidebar groups, settings sections), chat title | 14 SemiBold (headers TextMuted, title Text) | 20 |
| Secondary: descriptions in menus and settings, file-card subtitle | 13 Regular, TextMuted (TextDim on raised surfaces) | 18 |
| Captions: shortcut badges, block headers and status, diff gutter | 12 Regular | 16 |
| Code in blocks and diffs | Mono 12.5 | 19 |

### 2.3 Shape, spacing, sizes

- Radii: 8 rows, items, inputs, small buttons, hover fills, tooltips · 12 cards, menus, popovers, file card · 16 dialogs · 18 composer and user bubble · pill for the send circle, toggles and the changed-files pill: the radius is half the height (a larger one turns a wide box into an oval).
- Window: title row 36; caption buttons 46×36 (close hover red as now); 1 px window border Line.
- Sidebar: 240 wide; rows 31 px pitch, 8 px side inset, hover/current RowHover radius 8; icons 16; group headers with 20 px space above.
- Rail (collapsed sidebar): 50 wide. It is the sidebar with its labels hidden: every icon keeps its 16 px size and position.
- Main column: content max width 736 with equal side insets; the scrollbar overlays the edge. 12 between paragraphs, 16 between messages.
- Composer: input box 736 × min 98, radius 18; strip above it 39 tall, inset 13 from the box edges, top radius 12, tucked under the box's top edge.
- Icon buttons: 32×32 (28×28 in the title row), 16 px glyph, radius 8, hover `#2F2F2F`. Send/stop: 32 circle.
- Menus: padding 6; items 28 tall; icon column 24; text 14; shortcut right-aligned 13 TextDim; separators 1 px `#383838` with 4 px vertical margin.
- Settings: nav items 36 tall (inset 8, radius 8, 3 px vertical gap); content cards 724 wide, radius 12, rows split by 1 px Line; toggles 32×20.
- Dialog: 520 wide, padding 24, buttons 30 tall, radius 8, 8 apart.
- Focus: real focus rings everywhere, 2 px `#3881F5`, radius matching the control.

## 3. Components

Section numbers in parentheses point to `current-ui.md`.

1. **Title row** (§2.2). One 36 px ChromeBg strip across sidebar and work area. Left: sidebar toggle (icon button 28), then the chat title 14 SemiBold Text; a ⌄ after the title opens the existing chat menu (Переименовать, Удалить в корзину). Right: "Субагенты · N" as a ghost button (14 TextSoft, hover RowHover), then caption buttons.
2. **Sidebar** (§2.3). Starts with the "Новый чат" row: Edit glyph 16 + label 14 Text (no accent circle). Chat rows 14 TextSoft, current row RowHover, "…" button on hover/current. A running chat shows a 12 px AccentText spinner, and a chat waiting for input shows a Warning dot. Group headers ("Сегодня", "Вчера"…) 14 SemiBold TextMuted. Footer rows (Корзина, Баланс и расход, Настройки, Ещё) use the row style, no divider line.
3. **Rail** (collapsed sidebar, §2.3). ChromeBg, the same as the sidebar. Collapsing hides the labels and the chat list; icons never change size or move. "Все чаты" appears under "Новый чат" with a 1 px Line divider below it.
4. **Empty chat** (§2.4). Sharp mark 36 TextMuted centered; 16 below it "Новый чат" 28 Regular white.
5. **Composer** (§2.8). ComposerBg box; focus does not change the edge (the Accent edge stays only for internal drag-over). Placeholder "Работайте в Sharp Agent" 14 TextDim. Toolbar inside the box, 8 from its bottom: attach "+" icon button · confirmations as a ghost label: Shield glyph + "Подтверждения" 14 `#979797`, off = Warning glyph + "Без подтверждений" in Warning · right: model chip, mic icon button, send circle.
6. **Composer strip**. Two tabs above the box: left = mode ("Весь ПК" with the laptop glyph E7F8, or "Песочница" with Lock; click opens settings as the mode pill does now), right = working folder (Folder glyph + folder name; click = folder dialog, right-click = the existing folder menu). Mode 14 Text, folder 14 TextDim, glyphs 16. Below the tabs, up to three 26 px chips suggest follow-up requests. Clicking a chip fills the composer.
7. **Model chip and effort popover** (§2.9). Chip: model name 14 Text + effort 14 TextDim + chevron 12, ghost. Popover (EffortPickerMenu): about 255×98, Surface2 `#2D2D2D`, radius 14, anchored above the chip; header: lightning toggle left; center effort name 14 SemiBold AccentText and model name 12 `#B0B0B0` with "›"; reset icon right; slider track 24 tall pill. The model list opens in the same container, under the header: label "Выбрать модель" 13 TextDim, items 14, ✓ on the current one.
8. **Send / stop**. Accent circle with a white up-arrow (E74A); disabled = `#3A3A3A` circle with a `#7A7A7A` glyph; running = Accent circle with a white 10 px square; resume = Play glyph.
9. **Menus** (§2.22). Surface2, radius 12, shadow Blur 24 / depth 8 / opacity .35, padding 6. Hover Surface3 radius 8; destructive items (icon and text) DangerText; two-line items: title 14 Text + description 13 TextDim, ✓ on the right; section labels 13 TextDim; submenus open to the right with "›". Applies to every ContextMenu, the «Ещё» menu, the model list and the folder menu.
10. **Tooltips** (§2.23). Surface2 `#2D2D2D`, radius 8, padding 6×10, text 13 `#F3F3F3`. A shortcut shows as a badge 8 px after the label: 12 px `#E0E0E0` on `#4A4A4A`, radius 6, padding 2×6 (e.g. "Новый чат [Ctrl+N]").
11. **Dialog** (§2.13). Backdrop over the window; panel 520 wide, Surface2, radius 16, padding 24; title 20 SemiBold; body 14 TextSoft; inputs 34 tall, `#2D2D2D` fill, LineStrong border, radius 8; buttons right-aligned: secondary Surface3 + Text, primary PrimaryBg/PrimaryFg; danger primary = Danger bg + white.
12. **User message** (§2.5). Right-aligned UserBubble, radius 18, padding 8×16, white 14 px, max ~70 % of the column; no avatar, no time.
13. **Answer** (§2.5–2.6). 14 px Text, line height 23; bold = SemiBold; links AccentText, underlined on hover; lists indented 24. Footer: copy icon button 28 (16 glyph, TextMuted) + time 12 TextMuted. Reasoning and interim text in the progress section use TextSoft from the first update.
14. **Progress section and tool rows** (§2.7). Header: "Думаю" with the shimmer (§4) while running, then "Выполнено за 35 с ›" 14 TextMuted, collapsible; no spinner. Tool group: glyph 16 + summary 14 TextMuted + chevron; children indented 34 with a 1 px `#2E2E2E` guide line under the group glyph. Row: glyph 16 + text 14 TextMuted, 33 px pitch; file names stay file links with a dotted underline; commands as plain text trimmed with "…" (no TextChip boxes). Warning/error glyphs and text stay; no success check.
15. **Tool details** (§2.7). Command block: BlockBg, radius 12, padding 12; header = tool label 12 TextDim; "$ command" Mono 12.5 Text; output Mono 12.5 TextSoft; status bottom-right 12 TextDim ("Выполнено" / "Не выполнено"). Diff block: header "{file} +N −M" (name 13 Text, +N Success, −M DangerText), line numbers 12 TextMuted, added lines DiffAdd with a 2 px Success bar on the left edge.
16. **File card** (§2.5). Fills the chat column. BlockBg, radius 12, padding 12; 32 px file-type tile; title 14 SemiBold (trimmed) + subtitle 13 TextDim ("Документ · DOCX"); right a secondary button "Открыть" (Surface3, radius 8, 30 tall).
17. **Changes card** (§2.5). Same card: header "Изменено N файлов" 14 SemiBold + "+A −D"; "Отменить" and "Просмотреть изменения" as secondary buttons; rows 14 px with counts on the right, RowHover on hover. While a task is running, a pill "Изменено N файлов +A −D" (`#343434`, 14 px, 30 tall) sits at the end of the scrollable chat, updates as tool results arrive, and opens the same review pane.
18. **Right pane** (§2.14). Bg, 1 px Line on the left, splitter as now. Header: title 14 SemiBold + path 12 TextMuted, icon buttons 32 on the right. "Изменения | Файл" as tabs: 28 tall, 13 px, active `#2D2D2D` radius 8, inactive TextMuted. Change picker = ghost dropdown "{i}/{n} · path ⌄". Diff and file views: Mono 12.5, line numbers TextMuted, diff colors from §2.1. Office preview: white page on Bg, toolbar with the zoom toggle as a ghost button and page arrows as icon buttons.
19. **Settings** (§2.20). Nav (ChromeBg): "Настройки" 20 SemiBold with the back icon; items 36 tall, icon 16 + label 14, current RowHover. Content (Bg): page title 20 SemiBold, then sections: header 14 SemiBold Text + optional description 13 TextMuted, then a Surface card (radius 12) of rows. Row: title 14 Text + description 13 TextMuted on the left; on the right a toggle 32×20 (Accent on, `#4A4A4A` off, white 14 knob), a ghost dropdown "Значение ⌄" (14 Text, `#343434` fill, radius 8, 30 tall) or a 34-tall input. Choices are dropdown rows; on/off options are toggle rows. Long inputs take the full card width under their title. "Отмена" (secondary) and "Сохранить" (primary) stay at the bottom right.
20. **Sidebar popovers** (§2.18). Surface2 `#2C2C2C`, radius 12, padding 12; rows: label 14 TextSoft left, value 14 Text right; captions 13 TextDim; links as rows. Used by the Trash and Balance flyouts.

## 4. Motion

| What | Behaviour | WPF |
|---|---|---|
| Hover, press, selection | Instant | Brushes set in triggers, no ColorAnimation |
| Menus, context menus, dropdowns | Instant open and close | ContextMenu without PopupAnimation |
| Tooltips | 500 ms delay, instant show/hide, no delay between adjacent controls | `InitialShowDelay=500`, `BetweenShowDelay=500`, no fade |
| Dialogs, backdrop | Instant | No fade |
| Right pane, settings page, sidebar panel, flyouts | Instant | Remove the 160 ms settings and 120 ms flyout fades |
| Composer popover (effort / model) | Opacity 0→1 with TranslateY +8→0, 180 ms, cubic-bezier(0, 0, 0.2, 1); closes with a 50 ms fade | `KeySpline="0,0 0.2,1"`, `Duration=0:0:0.18` |
| Popover → model list | Same container; the list fades in and settles upward over 180 ms | Opacity 0→1 with TranslateY +8→0 on the list, same easing as the popover |
| Streamed answer text | Each new chunk fades in 0→1 over 160 ms, near-linear | Each appended Run gets its own brush with animated Opacity |
| New messages and rows | Fade in over 160 ms | Keep the existing fade of new chat elements |
| "Думаю" and running statuses | One contrasting band sweeps across the label in 2 s, then pauses for 5 s | Absolute LinearGradientBrush across the label with a TranslateTransform and repeating keyframes |
| Loading content and the current plan step | Spinner (0.9 s) | Reuse the existing spinner |
| Scrolling | Smooth wheel scrolling is optional | Keep the custom wheel and the 340 ms scroll-to-bottom |
| Tool group expand/collapse | 150 ms or instant | Shorten RevealPanel |

## 5. Other surfaces

- **Subagents panel** (§2.15): right-pane chrome. Header 44: puzzle glyph + "Субагенты" 14 SemiBold, expand/close icon buttons. List rows like tool rows: title 14 Text, status 13 TextMuted (running statuses shimmer), chevron. Detail page: the activity stream as tool rows (§3.14), the result as an answer.
- **Trash flyout** (§2.18): popover style, width 380; header "Корзина" 14 SemiBold + "Очистить" small secondary button; rows like sidebar rows (hover Surface3) with Undo/Delete icon buttons shown on hover or focus; captions 13 TextDim.
- **Balance and usage flyout** (§2.18): popover style; big number 20 SemiBold; meters 4 px pills, track `#3A3A3A`, fill Text (Warning when low); links as rows.
- **Toast** (§2.12): Surface2 `#2D2D2D`, radius 12, padding 10×14, 14 px, colored glyph, action as a small secondary button; top center; no fade.
- **Banner** (§2.10): Surface card radius 12 above the composer; glyph colored by kind; title 14 SemiBold, body 13 TextMuted; action as a small primary button.
- **Question card** (§2.11): Surface card radius 12; question 14 SemiBold; choices as rows (`#2D2D2D`, hover Surface3, radius 8) that may wrap to two lines; custom answer input 34 tall.
- **Drag-and-drop overlay** (§2.16): Bg at 92 %, dashed 2 px `#4A4A4A` frame radius 16, glyph 28 Text, title 20 SemiBold, hint 14 TextMuted.
- **Picture overlay** (§2.17): black 80 % backdrop; float buttons as 36 circles `#2D2D2D` with 16 glyphs.
- **Voice input** (§2.8.1): inside the composer box; timer 14 TextSoft; wave bars TextSoft on `#3A3A3A`; cancel = icon button; done = Accent circle with Check.
- **Compact window** (§2.21): transcript card Surface radius 16; user bubbles UserBubble radius 14. The changed-files pill sits at the end of the transcript and opens the review in the full window. Input = small composer (ComposerBg, `#393939` edge, radius 20) with the send circle.
- **Scroll-to-bottom button**: 32 circle `#2D2D2D` with a 1 px `#3A3A3A` edge, arrow-down 16 Text, soft shadow.
- **Error card** (§2.5): card `#2B1D1D`, radius 12, glyph and text DangerText, action as a secondary button.
- **Browser pane** (§2.26): right-pane chrome (§3.18). Tab row: tabs as in §3.18, each with a close × (icon 10, shown on the active tab and on hover), "+" icon button. Toolbar: back, forward, refresh and network as icon buttons 32, then the address field (34 tall, Raised, radius 8, 14 px, placeholder TextDim). Error view = empty state: glyph 28 TextMuted, title 14 SemiBold, hint 13 TextMuted, secondary button.
- **Settings «Сеть»** (§2.20): one settings card; first row = dropdown with the mode; the rows of the chosen mode follow (proxy: type dropdown, host, port, user and password inputs; VPN: subscription input, server dropdown, engine path with a secondary «Выбрать…» button); the route check is a secondary button with its status as 13 px text beside it (TextMuted, Success or DangerText).
- **Sticky panel** (§2.4.1): the progress header and plan pin when they reach the top of the chat column during manual or automatic scrolling (same 736 width, no card fill). The chat scrolls below them.
- **Computer Use**: from the first desktop action until the request ends, each monitor shows a click-through status badge and a rounded cyan, blue and violet frame. The running progress header shows «Использую навык Computer Use». Esc stops the task from any foreground window. Static translucent strokes soften the glow. Three curved waves travel along each edge at a capped frame rate. Only narrow edge surfaces animate, and the frame is static with animations disabled.
- **Grouped tool rows** (§2.7): «Правки файла · …», «Поиск текста · N» and the plan are tool groups (§3.14). The current plan step uses a 16 px spinner on the left. Pending and completed steps use checkboxes (16, radius 4, Accent when done).
- **Dropdown button** (§3.19): opens a menu (§3.9) with ✓ on the current value; the menu is at least as wide as the button.
- **Empty states** (trash, subagents, browser error, previews that cannot open): glyph 28 TextMuted, title 14 SemiBold Text, hint 13 TextMuted, centered (TextDim on raised surfaces). A state with a single message shows it as the title.

## 6. Decisions (2026-11-19)

1. Accent follows the Windows accent color; the other blue roles are derived from it (§2.1).
2. Mode and working folder live in the composer strip (§3.6); the title-row pill and the toolbar chip are gone.
3. Light values were tuned and checked on screen (§2.1).
4. Primary buttons are white (PrimaryBg / PrimaryFg), not accent-colored.
5. Included: the ⌄ chat menu after the title (§3.1), the Backdrop dim behind dialogs (§3.11), the changed-files pill (§3.17) and the popover → model list transition (§4).
6. The title row is 36 px. It and the sidebar are one constant color, ChromeBg: no translucent or wallpaper-tinted backdrop. Both ways were tried and dropped: the system backdrop of Windows 11 needs a transparent window, which costs ClearType in the whole window; a copy painted by the app tinted the UI with whatever the wallpaper was and made the window slow.

## 7. Building blocks in App.xaml

Use these before inventing a style and set only what differs. Brush keys are in §2.1.

| Key | For | Look |
|---|---|---|
| default Button (`SecondaryButton`) | Secondary action | Surface3, radius 8, 30 tall, 14 px, padding 12 |
| `PrimaryButton` | The main action of a surface | PrimaryBg / PrimaryFg |
| `DangerButton` | Destructive confirmation | Danger, white text |
| `GhostButton` | Low-key action with a label (title row, composer) | transparent, TextSoft, Text on hover |
| `DropdownButton` | A value with a chevron; opens a menu with ✓ | ControlBg; Content is the value text |
| `PillButton` | Pill-shaped secondary (changed-files pill) | ControlBg, 30 tall, radius 15 |
| `IconButton`, `IconToggle` | Glyph button 32×32, radius 8, glyph 16 | in code: `Theme.IconButton(glyph, hint, size, fontSize)` |
| `SendButton` | Accent circle: send, stop, voice done | disabled = Track with a TextMuted glyph |
| `FloatButton` | Circle floating over content | Raised, Track edge, shadow |
| `TabToggle` | Tabs (RadioButton or ToggleButton) | 28 tall, 13 px, active Fill |
| `ToggleSwitch` | On/off switch (ToggleButton) | 32×20; no content, named through AutomationProperties.Name |
| `RowButton` | Clickable row on any surface | 14 px TextSoft; `Tag="current"` = RowHover |
| `NavButton`, `ChatItem` | Sidebar and navigation rows | 30 tall |
| `FileChip` | Attachment chip | Surface3, 26 tall, 13 px |
| `FileCard` | Clickable card | BlockBg, radius 12, padding 12 |
| `Card` | Border: Surface, radius 12 | settings groups, banner, question card |
| `Flyout` | Border: popover inside the window | Raised, radius 12, shadow |
| `FieldTextBox`, `FieldPasswordBox` | Inputs, 34 tall | `Tag="invalid"` = DangerText edge |
| `PageTitle`, `SectionTitle`, `FieldLabel`, `FieldHint` | Text roles | 20 SemiBold · 14 SemiBold · 14 · 13 TextMuted |
| `Meter` | 4 px progress pill | Foreground is the fill |
| `ProgressSection`, `PlanCheck` | Collapsible work section, plan checkbox | |
| ContextMenu and MenuItem (implicit) | Menus | `Icon` = 16 px glyph, `InputGestureText`, `IsChecked` = ✓ on the right, `Tag="danger"`; `MenuLabel` TextBlock for section labels |
| ToolTip (implicit) | Tooltips | a trailing "(Ctrl+N)" in the text is shown as the key badge |
| `EffortPickerMenu`, `EffortHeader`, `EffortSlider` | Composer popover | |

- Radius comes from the style (`Border.CornerRadius` setter: 8, 12, 999 for circles). A pill sets half its height: 999 turns a wide box into an oval.
- Hover and press are the HoverLight veil built into the button and row templates; no hand-made hover fills.
- Colors follow the theme only through keys: `{DynamicResource Key}` in XAML, `.Paint(property, "Key")` in code.
- Focus rings: `FocusRect`, `FocusPill`, `FocusInner` (2 px AccentText). `FocusPill` takes its radius from its height (`PillRadius` converter).
