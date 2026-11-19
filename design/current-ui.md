# Sharp Agent — UI map

Where each surface of the UI is built. Section numbers are the ones `DESIGN.md` refers to. The look and the tokens are in `DESIGN.md`; strings, tooltips and automation names live in the XAML and code listed here.

## 1. Tokens and shared styles

- Palette (dark values) and every shared style: `App.xaml` (`DESIGN.md` §2.1 and §7).
- Light values, the accent derivation and theme switching: `Ui/Theme.cs` (`LightColors`, `Theme.Apply`). Code-built UI follows a theme switch through `.Paint(property, "Key")`.
- Window frame, caption buttons, DWM border: `MainWindow.Chrome.cs`, `Ui/WindowEffects.cs`. `GlassFrameThickness="0"` on the WindowChrome in `MainWindow.xaml` must stay: without it text loses ClearType.
- Glyph constants: `Glyphs` in `Ui/Theme.cs`. Fonts `IconFont` and `MonoFont`: `App.xaml`.

## 2. Surfaces

XAML names are in `MainWindow.xaml` unless a file is given.

| § | Surface | XAML | Code |
|---|---|---|---|
| 2.1 | Window shell and layering | `RootGrid` (title row over the body row; sidebar column and work column), `WorkArea` | `MainWindow.xaml.cs`, `MainWindow.Chrome.cs` |
| 2.2 | Title row | the Grid with `SidebarToggle`, `ChatTitleText`, `ChatMenuButton`, `SubagentsOpenButton` and the caption buttons | `MainWindow.Chrome.cs`, `MainWindow.Sidebar.cs` (chat menu) |
| 2.3 | Sidebar and rail | `Sidebar` (local styles `SidebarItem`, `SidebarIcon`, `SidebarLabel`) | `MainWindow.Sidebar.cs` |
| 2.4 | Chat area, empty state, scroll-to-bottom button | `EmptyChat`, `ChatScroll`, `ScrollDownButton` | `MainWindow.Chat.cs`, `MainWindow.ChatHistory.cs` |
| 2.4.1 | Sticky progress panel | `ChatStickyHost` | `MainWindow.Chat.cs` |
| 2.5 | Messages; file, changes and error cards; changed-files pill | `ChangesPill` | `MainWindow.Chat.cs`, `MainWindow.Changes.cs`, `MainWindow.Preview.cs` (file card) |
| 2.6 | Markdown, links, file references | — | `Ui/MarkdownRenderer.cs`, `Ui/ChatLinks.cs`, `Ui/LinkTitles.cs`, `Ui/PathRefs.cs` |
| 2.7 | Progress section, tool rows, plan, tool details, streamed draft | `ProgressSection`, `PlanCheck` in `App.xaml` | `MainWindow.Progress.cs`, `MainWindow.Chat.cs`, `Ui/RevealPanel.cs`, `Ui/Shimmer.cs` |
| 2.8 | Composer, strip (mode and folder), attachments | `Composer`, `ModePill`, `FolderButton` | `MainWindow.xaml.cs`, `MainWindow.Attachments.cs`, `Ui/ComposerBox.cs` |
| 2.8.1 | Voice input | `VoicePanel` | `MainWindow.Voice.cs`, `Ui/VoiceWave.cs` |
| 2.9 | Model chip, effort popover, model menus | `ModelButton`; `EffortPickerMenu`, `EffortSlider` in `App.xaml` | `MainWindow.Providers.cs`, `Ui/EffortSlider.cs` |
| 2.10 | Banner | `Banner` | `MainWindow.Settings.cs` (`ShowBanner`) |
| 2.11 | Question card | `QuestionHost` | `MainWindow.Interactions.cs` |
| 2.12 | Toast | `ToastHost` | `MainWindow.Settings.cs` (`ShowToast`) |
| 2.13 | Dialogs and the window dim | `DialogDim` | `Ui/DarkDialog.cs` |
| 2.14 | Preview pane: diff, file, office | `PreviewPane`, `PreviewSplitter` | `MainWindow.Preview.cs`, `MainWindow.Changes.cs` (`ReviewChanges`), `Ui/OfficePreview.cs` |
| 2.15 | Subagents panel | `SubagentsWorkspace` | `MainWindow.Subagents.cs` |
| 2.16 | Drag-and-drop overlay | `DropOverlay` | `MainWindow.Attachments.cs` |
| 2.17 | Picture overlay | `PictureOverlay` | `MainWindow.Preview.cs` |
| 2.18 | Trash and usage flyouts | `FlyoutLayer`, `TrashCard`, `UsageCard` (local styles `TrashRow`, `TrashActions`) | `MainWindow.Trash.cs`, `MainWindow.Usage.cs`, `MainWindow.Sidebar.cs` (`ShowFlyout`) |
| 2.19 | «Ещё» menu, Shortcuts, About | `MoreMenu` | `MainWindow.xaml.cs` |
| 2.20 | Settings page | `SettingsPage` (local styles `SettingsCard`, `SettingsRow`, `RowText`, `RowHint`, `RowStatus`, `RowDropdown`, `RowField`) | `MainWindow.Settings.cs`, `MainWindow.Network.cs`, `MainWindow.Providers.cs` |
| 2.21 | Compact window | `Ui/CompactChatWindow.xaml` | `Ui/CompactChatWindow.xaml.cs`, `MainWindow.Compact.cs` |
| 2.22 | Context menus | implicit ContextMenu and MenuItem styles in `App.xaml` | `Ui/FileActions.cs` (`Item`, `BuildMenu`), `Ui/PopupPlacement.cs` |
| 2.23 | Tooltips | implicit ToolTip style in `App.xaml` | `Ui/Converters.cs` (key badge), `Ui/PopupPlacement.cs` |
| 2.26 | Browser pane | `BrowserPane` (local style `BrowserTabClose`) | `MainWindow.Browser.cs`, `Ui/BrowserView.cs`, `Ui/BrowserInterop.cs` |

The built-in browser page is a native window drawn above WPF content: `BrowserPages` hides itself through style triggers while an overlay, a flyout or the dialog dim is shown. A new overlay must be added to those triggers.

## 3. Motion

All of it is gated by `Theme.AnimationsEnabled` (`DESIGN.md` §4).

| What | Where |
|---|---|
| Effort popover in and out, model list settling | `MainWindow.Providers.cs` (`Settle`) |
| Shimmer of "Думаю…" and running statuses | `Ui/Shimmer.cs` |
| Streamed text fading in | `MainWindow.Progress.cs` (the draft) |
| Tool groups opening and closing | `Ui/RevealPanel.cs` |
| New chat elements fading in, scroll to the last message | `MainWindow.Chat.cs`, `MainWindow.ChatHistory.cs` |
| Voice wave, recording dot, transcribing spinner | `MainWindow.Voice.cs`, `Ui/VoiceWave.cs` |

## 4. Keyboard

Shortcuts and the Esc order: `Window_PreviewKeyDown` in `MainWindow.xaml.cs`. The list shown to the user is the Shortcuts dialog (`Shortcuts_Click`, same file). Chat list keys (arrows, F2, Delete): `MainWindow.Sidebar.cs`.

## 5. Layout breakpoints

Window narrower than 900: the sidebar opens as an overlay with a shade. Work area narrower than 760 with a side pane open: the pane covers the chat. Composer narrower than 558: toolbar labels hide and the folder name shrinks. The code is in `MainWindow.Sidebar.cs`, `MainWindow.Preview.cs` and `MainWindow.xaml.cs`.
