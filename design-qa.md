# Wear OS 主页设计 QA

## 对比目标

- Source visual truth: `C:\Users\YANGTI~1\AppData\Local\Temp\codex-clipboard-204b8171-406f-4bf5-8319-dcf06c6d1e2e.png`
- Status-page implementation: `C:\Users\YangTianming\.codex\visualizations\2026\08\11\019ff0bd-6728-7e23-8145-c7530b88aeb2\wear-home-pager-status-final.png`
- After-school implementation: `C:\Users\YangTianming\.codex\visualizations\2026\08\11\019ff0bd-6728-7e23-8145-c7530b88aeb2\wear-home-pager-after-school-v2.png`
- Menu-page implementation: `C:\Users\YangTianming\.codex\visualizations\2026\08\11\019ff0bd-6728-7e23-8145-c7530b88aeb2\wear-home-pager-menu-final.png`
- Combined comparison: `C:\Users\YangTianming\.codex\visualizations\2026\08\11\019ff0bd-6728-7e23-8145-c7530b88aeb2\wear-home-pager-comparison-final.png`
- Viewport: Pixel Watch 2 API 35 emulator, app content `450 x 450 px`, round display, rotation 0.
- Normalization: source image is `979 x 1778 px`; both `848 x 848 px` round-screen regions were cropped separately and normalized to `450 x 450 px`. Implementations were captured natively at `450 x 450 px`, so both page comparisons use equal pixel dimensions.
- State: the first page uses controlled debug data for the same `上课 / 数学 / 起止时间 / 下一节课` state. The second page uses the administrator permission set shown in the source.

## Full-view comparison evidence

- The first page uses the entire pale-pink round display for the current time-period status, course, time and next-class summary.
- The second page uses a black round display with one centered vertical menu. One full upward swipe snaps from the first page to the second page without stopping between them.
- The two-dot indicator remains at the right edge and swaps its active lavender dot with the current page.
- The timed-state ring and label form one centered row. The ring remains hidden for after-school and other states without a bounded time range.
- All four admin actions are visible and retain the requested order: `课表`, `换课`, `控制`, `设置`.

## Focused-region evidence

No extra crop is required because the normalized `450 x 450 px` comparison keeps all text, icons, chip borders and corner radii legible. The UI contains no raster imagery or custom decorative assets; all visible icons use the existing Material Icons library.

## Required fidelity surfaces

- Fonts and typography: Android/Wear system Chinese sans-serif is used with bold status text and smaller regular action labels. The current sizes track the normalized reference without wrapping or truncation.
- Spacing and layout rhythm: the status row begins at `0.26D`; subject and time chips use `0.39D` width. Menu tiles use `0.47D` width, `0.185D` height and `0.015D` gaps.
- Colors and visual tokens: black menu background, pale-pink status page, lavender chips/buttons and dark-purple content match the reference palette and preserve readable contrast.
- Progress indicator: the enlarged status-page ring uses `0.125D` diameter, `0.02D` stroke, `#6750A4` progress and `#EEE5FA` track, matching the selected mock's scale and palette.
- Image quality and asset fidelity: no raster assets are required. Material vector icons stay sharp at device density and replace no custom source artwork.
- Copy and content: action labels match the reference. Course/status/time values remain live data rather than hard-coded mock text.

## Comparison history

1. v1 finding `[P1]`: the subject and time chips extended beyond the status card and were visibly clipped. Fix: reduced state/chip dimensions and vertical spacing.
2. v2 finding `[P2]`: action icons and labels were materially larger than the normalized source, and the tiles were overly capsule-shaped. Fix: reduced icon/type scale and changed tile radius.
3. v3 finding `[P1]`: safe-area fitting made the card and grid too narrow, while the top arc only approximated the requested concentric circle. Fix: intersected a true circular path with the card bounds, restored the wide grid, and made the page vertically scrollable.
4. v4 finding `[P1]`: the selected design changed the inset dome into a full-width upper background partition. Fix: removed the card shape and let the round display create the outer arc.
5. v5 finding `[P2]`: the first flat-layout pass placed the lower grid about 12 px too low and oversized the chips/action typography. Fix: matched normalized panel height, grid origin, chip height, icon size and typography.
6. v6 finding `[P1]`: the selected design changed the single scrolling composition into two full-screen pages. Fix: replaced the long column with an official snapping vertical pager and split status/menu content.
7. v7 finding `[P1]`: the first pager capture truncated the complete time range. Fix: reduced only the time-chip typography while retaining the source chip geometry.
8. v8 evidence: both normalized page comparisons and the physical swipe test have no actionable P0/P1/P2 mismatch.

## Findings

- No actionable P0/P1/P2 findings remain.
- `[P3]` The implementation capture uses a current one-hour test interval and live next subject rather than the mock's fixed values; the component dimensions and state are otherwise equivalent.
- `[P3]` The small source label `主页` sits outside the round-screen artwork and is intentionally not implemented as app content.

## Primary interactions tested

- `课表` opens the seven-day schedule overview.
- `控制` opens the notification editor and shows the automatic username-prefix explanation.
- `换课` opens the date selector.
- `设置` opens connection and message settings.
- A full upward swipe snaps from page 1 to page 2, and the reverse gesture returns to page 1.
- Timed class state shows the progress ring and keeps the complete ring-plus-label group on the horizontal center axis.
- After-school state hides the ring and centers `放学` independently.
- Android activity remained alive with no fatal exception during capture and navigation checks.

## Implementation checklist

- [x] Match home visual hierarchy and palette.
- [x] Use a full-screen status page and a separate full-screen vertical menu page.
- [x] Snap one complete screen per vertical swipe and update the right-side page indicator.
- [x] Show the precisely sized progress ring only for bounded class phases.
- [x] Preserve permission-aware menu visibility and next-class privacy.
- [x] Keep permission-based action visibility.
- [x] Verify all four primary navigation actions.
- [x] Capture and compare the final emulator view.

final result: passed

---

# WebUI 任务工作区设计 QA

## 对比目标

- Source visual truth: `C:\Users\YangTianming\.codex\generated_images\019ff143-7e16-7e63-b584-5a169ea8e638\exec-b1debf97-b98c-4962-a634-9025d3a2de47.png`
- Implementation screenshot: `C:\Users\YangTianming\.codex\visualizations\2026\08\11\019ff143-7e16-7e63-b584-5a169ea8e638\remoteci-webui-implementation-v3.png`
- Combined comparison: `C:\Users\YangTianming\.codex\visualizations\2026\08\11\019ff143-7e16-7e63-b584-5a169ea8e638\remoteci-webui-design-comparison-final.png`
- Viewport: desktop WebUI `1440 x 1024` CSS px；窄屏补充检查为 `700 x 900` CSS px。
- Normalization: source is `1487 x 1058 px`; implementation is `1440 x 1024 px`. The combined comparison fits both to equal `700 x 500 px` frames without comparing browser chrome. Browser device scale factor was `2.25`, while the captured implementation artifact was normalized by the browser backend to `1440 x 1024 px`.
- State: authenticated administrator, plugin offline, no current class or synchronized schedule, one account, zero watches.

## Full-view comparison evidence

- The selected full-height sidebar, brand position, five permission-based navigation entries and bottom-anchored red logout action match the source hierarchy.
- The task-workspace title, top-right connectivity summary, current-course hero, two task links, system table and footer preserve the source order and proportions.
- The implementation intentionally renders the live protocol as `v2` instead of the generated mock's erroneous literal `v@Protocol.Version`.

## Focused-region evidence

- The combined comparison includes a second focused row for the sidebar, title, connectivity summary, course/status hero and common-task cards.
- The focused evidence confirms matching left offsets, divider placement, card heights, icon scale, green active state and compact typography without clipping or overlap.

## Required fidelity surfaces

- Fonts and typography: `Segoe UI` / `Microsoft YaHei UI` fallbacks reproduce the source's neutral Chinese sans-serif hierarchy. Display, section, body and metadata text remain readable without unintended wrapping.
- Spacing and layout rhythm: the `248 px` sidebar, `40 px` desktop workspace gutter, first-section top alignment, card radii and section gaps match the selected mock. The `700 px` layout collapses to a `78 px` icon rail and one-column task grid without horizontal overflow.
- Colors and visual tokens: the off-white canvas, white surfaces, emerald active/accent states, gray dividers and red destructive action are mapped to reusable CSS tokens with accessible focus states.
- Image quality and asset fidelity: no raster imagery is required. All visible UI icons use the locally vendored Bootstrap Icons library; no emoji, custom SVG, CSS drawing or placeholder art is used.
- Copy and content: source labels and offline explanations are preserved. Dynamic account, watch, schedule and protocol values remain server-driven.

## Comparison history

1. v1 finding `[P2]`: the first content section sat too low and the logout action was wider and closer to the viewport edge than the source. Fix: removed the extra first-section margin and matched the logout control's width, height and bottom inset.
2. v2 finding `[P2]`: the hero icon was undersized and the notification/system copy drifted from the selected mock. Fix: increased the hero icon surface and restored the source copy, while retaining the real protocol value.
3. v3 evidence: the final full-view and focused comparison has no actionable P0/P1/P2 difference.

## Findings

- No actionable P0/P1/P2 findings remain.
- `[P3]` The implementation title is optically a little heavier than the generated source on some Windows font-rendering configurations. The hierarchy remains equivalent and no wrapping occurs.

## Primary interactions tested

- The overview task link opens the seven-day schedule page and marks its sidebar entry as current.
- The notification task link opens the notification form and marks its sidebar entry as current.
- The red logout button signs out, returns to the login screen, and a subsequent login returns to the task workspace.
- The browser console reported no warnings or errors during the tested flow.

## Implementation checklist

- [x] Remove the duplicated top bar and use one full-height sidebar.
- [x] Anchor the red logout action at the bottom of the sidebar.
- [x] Match the selected task-workspace hierarchy and light visual tokens.
- [x] Preserve permission-aware navigation and working server-backed content.
- [x] Verify desktop and narrow responsive layouts.
- [x] Test primary navigation, logout and browser console state.

final result: passed
