# State of GPUI for a large non-Zed desktop tool (November 2026)

Question: can GPUI (Zed's UI framework) host a cross-platform Rust shell for Source 2 Viewer? The shell needs a VPK file explorer (100K+ entries), a tab per file, per-viewer sidebars, text, code and hex views, node graphs, and a 3D viewport drawn by the existing C# OpenGL renderer.

Sources were read on 2026-11-23: zed `main` at `d31d1074a0` (2026-11-22), gpui-kit (formerly gpui-component) `main` at `1c7e7f7c6c` (2026-11-23), gpui-fast `main`, crates.io, and GitHub issues and PRs.

## Summary

- **Publishing is the weakest point.** Zed's own `gpui` on crates.io is still 0.2.2 from 2025-10-22. Zed `main` still says `version = "0.2.2"`, and `gpui_macros` is `publish = false`. Zed says automated weekly releases are "soon™" but has not shipped them ([discussion #55271](https://github.com/zed-industries/zed/discussions/55271), [issue #66275](https://github.com/zed-industries/zed/issues/66275)). In practice an app uses one of three things:
  - a pinned git rev of `zed-industries/zed`;
  - `gpui-pre`, weekly crates.io snapshots of Zed's crates republished by gpui-kit's maintainer (huacnlee);
  - the `gpui-fast` fork.
- **API churn is high.** The README says "pre-1.0. There will often be breaking changes between versions." In the last three months, 154 commits touched `crates/gpui`. Recent breaks include the `gpui_platform` crate split, `ScrollWheelEvent` -> `ScrollEvent` and removed `IntoElement` impls. gpui-kit ships breaking changes in most minor releases and pins `gpui-pre` exactly (`=0.3.12`) because a caret range broke apps.
- **Backends.** macOS uses Metal. Windows uses Direct3D 11 with DirectComposition and DirectWrite. Linux uses wgpu (Vulkan, falling back to GL) on Wayland and X11, rewritten from Blade in February 2026. All three ship in Zed's stable builds (Linux since 2024-07, Windows since 2025-10), so they are production-grade for an editor's workload.
- **Embedding a foreign 3D frame is the main technical risk.** Upstream GPUI has no cross-platform way to show external GPU content:
  - `surface()` takes only a macOS `CVPixelBuffer`. The D3D11 and wgpu renderers draw surfaces as no-ops.
  - `NativeView` (merged 2026-11-22) composites an `NSView` on macOS only.
  - Dynamic updatable textures are an open PR ([#64061](https://github.com/zed-industries/zed/pull/64061)).
  - Upstream does have raw window handles (`Window: HasWindowHandle`), so an app can parent its own child HWND, X11 child or NSView. GPUI overlays (menus, popovers) then cannot draw above that child, and Wayland has no child-window equivalent.
  - The `gpui-fast` fork has the missing piece: `WindowComposition::create_external_gpu_surface` places foreign content under GPUI overlays on macOS, Windows (DirectComposition), Wayland (subsurface) and X11. The upstream PR for it ([#62379](https://github.com/zed-industries/zed/pull/62379)) was auto-closed on 2026-11-21 without being merged.
- **Widgets.** gpui-component, now part of `gpui-kit`, is the de facto library: 21K stars, releases every week, mostly by two Longbridge maintainers. It covers almost the whole list: a virtualized tree, list and table, tabs, a dock with serializable layouts, input, a code editor with tree-sitter or syntect and LSP, menus, a native context menu, dialogs, Markdown and charts. It has no hex view and no node-graph editor.
- **Platform services.** Upstream GPUI has file open and save dialogs (an xdg-desktop-portal on Linux), a native menu bar on macOS only (Windows and Linux keep menus for the app to draw itself), a clipboard, and file drops into the window. Dragging out to the OS works only on macOS and Wayland. Accessibility uses AccessKit on all three platforms (since 2026-05) but has open bugs.
- **Verdict.** GPUI can already host this kind of tool; many non-Zed apps ship on it, including database clients, a reverse-engineering tool and the Pulsar game engine editor. The costs:
  - Plan for a pinned, regularly bumped dependency and frequent breaking changes.
  - The 3D viewport needs either a CPU readback into an image, a native child window whose overlay limits you accept, or adopting `gpui-fast` or another fork.
  - Hex and node-graph views have to be written from scratch.

## 1. Publishing and versioning

| Channel | What it is | Latest | Notes |
| --- | --- | --- | --- |
| [`gpui` on crates.io](https://crates.io/crates/gpui) | Zed's official crate | 0.2.2, 2025-10-22 | No release in 13 months. `gpui_platform`, `gpui_windows`, `gpui_linux` and `gpui_macos` are not on crates.io at all. |
| git dep on `zed-industries/zed` | Zed's recommended route until releases resume | `main` | Zed's README says to "use the latest version of stable Rust". The workspace pins a git rev of `wgpu` ([Cargo.toml](https://github.com/zed-industries/zed/blob/main/Cargo.toml)), and a crate with a git dependency cannot be published to crates.io ([#55271 comment](https://github.com/zed-industries/zed/discussions/55271)). |
| [`gpui-pre`](https://crates.io/crates/gpui-pre) | Snapshot of Zed's GPUI crates, renamed and republished by huacnlee (gpui-kit) | 0.3.13, 2026-11-16, snapshot of `zed@125ca86` | 14 weekly versions since 2026-09-03. [`script/bump-gpui.ts`](https://github.com/longbridge/gpui-kit/blob/main/script/bump-gpui.ts) drops optional git-only deps and audits licenses. Not affiliated with Zed. |
| [`gpui-fast`](https://github.com/longbridge/gpui-fast) | Longbridge fork: retained-mode layout plus native window composition | 0.1.17, 2026-11-17 | Created 2026-09-24, "remains experimental". Tracks upstream through an [`UPSTREAM`](https://github.com/longbridge/gpui-fast/blob/main/UPSTREAM) file. |
| [`gpui-ce`](https://github.com/gpui-ce/gpui-ce) | Community fork | 0.3.0 on crates.io | "Mostly API compatible with upstream, but that is changing!" |

Zed's position, from MrSubidubi on 2026-10-30 ([#66275](https://github.com/zed-industries/zed/issues/66275)): "We are currently working on automated releases for GPUI ... For as long as we do not have these set up, you will need to pin a git revision."

**API stability.**

- [`crates/gpui/README.md`](https://github.com/zed-industries/zed/blob/main/crates/gpui/README.md): "still pre-1.0. There will often be breaking changes between versions."
- 154 commits touched `crates/gpui` between 2026-08-23 and 2026-11-22 (GitHub commits API, path filter).
- Examples of breaking changes:
  - [#65805](https://github.com/zed-industries/zed/pull/65805) renamed `ScrollWheelEvent` to `ScrollEvent`.
  - [#64864](https://github.com/zed-industries/zed/pull/64864) removed hand-written `IntoElement` impls.
  - The `gpui_platform` split moved app startup to `gpui_platform::application()`.
- gpui-kit's root [`Cargo.toml`](https://github.com/longbridge/gpui-kit/blob/main/Cargo.toml) comment: "any snapshot may change GPUI's API ... a caret requirement let the weekly release move applications onto a newer snapshot that gpui-component did not compile with (#3156)."

## 2. Rendering backends

All three ship in Zed's stable channel: macOS first, [Linux on 2024-07-10](https://zed.dev/blog/zed-on-linux), [Windows on 2025-10-15](https://zed.dev/blog/zed-for-windows-is-here).

| Platform | Windowing | Renderer | Text | Source |
| --- | --- | --- | --- | --- |
| macOS | AppKit | Metal | Core Text through `font-kit` | `crates/gpui_macos`, README |
| Windows 10 1903+ | Win32 | Direct3D 11 (feature level 10.1 to 11.1) presented through DirectComposition | DirectWrite | [`directx_devices.rs`](https://github.com/zed-industries/zed/blob/main/crates/gpui_windows/src/directx_devices.rs), [`directx_renderer.rs`](https://github.com/zed-industries/zed/blob/main/crates/gpui_windows/src/directx_renderer.rs) |
| Linux / FreeBSD | Wayland and X11 (features `wayland`, `x11`) | wgpu: Vulkan, falling back to GL | cosmic-text / swash | [`gpui_wgpu`](https://github.com/zed-industries/zed/tree/main/crates/gpui_wgpu), [#46758 "Remove blade, reimplement linux renderer with wgpu"](https://github.com/zed-industries/zed/pull/46758) (merged 2026-02-13) |
| Web | wasm32 | wgpu WebGPU or WebGL | | `crates/gpui_web` |

Signs of maturity:

- Open Linux renderer fixes are still landing, such as [#67323 GL fallback without shader storage](https://github.com/zed-industries/zed/pull/67323) and [#65755 only init GL if Vulkan fails](https://github.com/zed-industries/zed/pull/65755).
- Zed's docs list a Vulkan 1.3 driver and desktop portals as Linux requirements ([install docs](https://zed.dev/docs/installation)).

## 3. Custom GPU content in a view (3D viewport)

This is the decisive question for the C# OpenGL renderer.

**Upstream GPUI (zed `main`)**

- [`elements/surface.rs`](https://github.com/zed-industries/zed/blob/main/crates/gpui/src/elements/surface.rs): `SurfaceSource` has one variant, `Surface(CFRetained<CVPixelBuffer>)`, and only on macOS and iOS. The Windows renderer's `draw_surfaces` returns `Ok(())` without drawing. The wgpu renderer says: "Surfaces are macOS-only for video playback and are not implemented by the WGPU renderer." An IOSurface-backed `CVPixelBuffer` is zero-copy on macOS, and OpenGL can render into an IOSurface, so macOS has a path.
- [`window/native_view.rs`](https://github.com/zed-industries/zed/blob/main/crates/gpui/src/window/native_view.rs), merged in [#67311](https://github.com/zed-industries/zed/pull/67311) and [#67316](https://github.com/zed-industries/zed/pull/67316) on 2026-11-22: "Only macOS shows native views so far, where the view is an `NSView`. `NativeView::new` fails on every other platform." It is composited beneath GPUI content, so GPUI elements can draw over it. It is treated as opaque, and input and focus are routed to it.
- `Window` implements `HasWindowHandle` and `HasDisplayHandle` ([`window.rs`](https://github.com/zed-industries/zed/blob/main/crates/gpui/src/window.rs)). An app can therefore create its own child HWND, X11 child window or NSView and position it every frame. gpui-webview does exactly this through wry's `build_as_child`. The documented caveats ([gpui-webview README](https://github.com/longbridge/gpui-kit/blob/main/crates/webview/README.md)):
  - The child "covers GPUI content in its bounds, including popovers, dialogs, menus and tooltips".
  - It "cannot be clipped".
  - Wayland is not supported.
- `canvas()`, `paint_quad`, `paint_path` and `paint_image` cover only GPUI's own primitives. There is no hook for custom shaders or raw device access, and the renderers keep their `ID3D11Device`, Metal device and wgpu device private.
- The CPU route is to read back each frame and paint it as a `RenderImage`. Today that means a fresh texture and atlas repack every frame. PR [#64061 "Add updatable dynamic textures"](https://github.com/zed-industries/zed/pull/64061) (open since 2026-09-11) adds a stable texture whose dirty region can be re-uploaded, aimed at "remote-desktop / screen-share surfaces". A 1920x1080 BGRA frame is about 8 MB, about 500 MB/s at 60 fps. That is workable for a viewer, but it adds latency and a GPU-to-CPU stall.

**gpui-fast (Longbridge fork)**

- [`crates/gpui/src/fast/composition.rs`](https://github.com/longbridge/gpui-fast/blob/main/crates/gpui/src/fast/composition.rs): `Window::enable_window_composition()` returns a `WindowComposition`, with:
  - `create_native_surface()`
  - `create_external_gpu_surface(Rc<dyn PlatformSurfaceAttachment>)`: "The producer owns the platform attachment and its synchronization. GPUI owns its placement in the window composition tree."
  - `create_gpui_surface`, plus `place_above` and `place_below`.
- The platform attachment is an `NSView` on macOS, an `IDCompositionVisual` on Windows and a `RawWindowHandle` on Linux.
- The [README](https://github.com/longbridge/gpui-fast#window-composition) says GPUI menus, popovers and dialogs render above native content on macOS (overlay layer), Windows (WebView2 composition), Wayland (subsurface) and X11 (overlay window; this needs a compositing manager, otherwise the overlay is cut out of the native content).
- `examples/native_webview.rs` drives "a wgpu device of its own" into the native slot on Linux. That is the same shape as an external OpenGL or Vulkan renderer.
- The upstream PR, [zed#62379 "gpui: Add native window composition support"](https://github.com/zed-industries/zed/pull/62379), was closed unmerged by a bot on 2026-11-21 because it was not self-reviewed. Zed then merged only the macOS `NativeView`.

**Prior art: Pulsar-Native** ([repo](https://github.com/Far-Beyond-Pulsar/Pulsar-Native), 432 stars, active). This game-engine editor uses GPUI with a live 3D viewport. To do it, it vendors [WGPUI](https://github.com/Far-Beyond-Pulsar/WGPUI), a fork of gpui-ce that replaces every platform backend with wgpu and winit and exposes a `WgpuSurfaceHandle` for an external renderer. A serious 3D tool on GPUI has so far needed its own fork.

**What this means for the C# renderer:**

1. **CPU readback into an image.** Works on every platform on any upstream GPUI.
2. **A child window from the raw handle.** Fast, but overlays cannot draw over the viewport, and Wayland needs XWayland.
3. **gpui-fast composition.** The proper fix on all platforms, at the cost of depending on an experimental fork.
4. **macOS only: an IOSurface through `surface()` or `NativeView`.**

## 4. Widget ecosystem: gpui-component (now gpui-kit)

Repo [`longbridge/gpui-component`](https://github.com/longbridge/gpui-component) now redirects to [`longbridge/gpui-kit`](https://github.com/longbridge/gpui-kit) (21.2K stars, 110 open issues, Apache-2.0). It is split into `gpui-kit` (umbrella), `gpui-base` (unstyled behavior) and `gpui-component` (styled). Longbridge builds its shipping desktop trading app, Longbridge Pro, on it.

**Maintenance**

- Weekly releases: v0.7.0 on 2026-09-28, then 0.7.1, 0.7.3, 0.8.0, 0.8.1, 0.9.0, 0.9.1, and 0.10.0 on 2026-11-16 ([releases](https://github.com/longbridge/gpui-kit/releases)).
- Commits since 2026-08-23: huacnlee 276, madcodelife 100, then a long tail of about 20 or fewer each. That is a bus factor of about two.
- Breaking changes in most minor releases:
  - 0.6: Input, Textarea and Editor split into separate APIs.
  - 0.7: plot primitives.
  - 0.8: `gpui-wry` renamed to `gpui-webview`; the GPUI backend switch.
  - 0.9: theme types moved.
  - 0.10: two input API changes.

**Coverage of the planned shell** (module names from [`crates/component/src`](https://github.com/longbridge/gpui-kit/tree/main/crates/component/src) and [`crates/base/src`](https://github.com/longbridge/gpui-kit/tree/main/crates/base/src)):

| Need | Status |
| --- | --- |
| File tree, 100K+ entries | `tree.rs`. `TreeState` flattens a `Vec<TreeItem>` and renders it through `uniform_list` ("virtualized tree"). Children are eager (`TreeItem.children: Vec<TreeItem>`), with no lazy loading, so for a VPK either build the item tree up front or drive `uniform_list` or `VirtualList` directly with your own flattening. Zed's project panel does the latter. |
| Virtualized list / table | `list`, `virtual_list.rs` (items of different sizes) and `table` with `TableDelegate { rows_count, columns_count, render_td }`. The README claims "hundreds of thousands of rows" with fixed and resizable columns, sorting and cell selection. |
| Tabs | `tab` (TabBar with an overflow menu) |
| Dock / panels | `dock` (`dock.rs`, `tab_panel.rs`, `panel.rs`): "Resizable panels, draggable tabs, nested splits, and edge docks, all serializable" |
| Text input | `input` (`InputState`, textarea, number and OTP inputs, search) |
| Code editor + highlighting | `input/editor.rs`. Tree-sitter has a feature per language, including `tree-sitter-csharp`, JSON, YAML and Lua; there are no grammars for KV3 or Valve formats. Since 0.8 a lighter `syntect` feature exists. LSP diagnostics, completion and hover are supported. The README claims "Stable performance at 200K lines". |
| Menus | `menu` (`app_menu_bar.rs`, `context_menu.rs`, `dropdown_menu.rs`, `popup_menu.rs`) plus `native_menu` (macOS and Windows: an OS-drawn popup that can extend beyond the window) |
| Dialogs | `dialog`, `sheet.rs`, `notification.rs`, alert dialog |
| Sidebars, settings, forms | `sidebar`, `setting`, `form`, `group_box.rs`, `slider.rs`, `select.rs`, `checkbox.rs`, `color_picker.rs` |
| Hex view | None. Build it on `uniform_list`. |
| Node graph editor | None (`chart` and `plot` only). Build it on `canvas` and `paint_path`. |
| Other | Markdown and HTML rendering, `diff`, `webview` (wry), an inspector, UI integration-test helpers, a WASM target |

## 5. Platform integration (upstream `Platform` trait)

All in [`crates/gpui/src/platform.rs`](https://github.com/zed-industries/zed/blob/main/crates/gpui/src/platform.rs) and the per-OS crates:

- **File dialogs.** `prompt_for_paths` and `prompt_for_new_path` are native on all three platforms. Linux uses only the xdg-desktop-portal FileChooser through `ashpd` ([`gpui_linux/.../platform.rs`](https://github.com/zed-industries/zed/blob/main/crates/gpui_linux/src/linux/platform.rs)), so a desktop without a portal gets no dialog. Windows has its own `dialog.rs`.
- **App menus.** `set_menus` builds a real `NSMenu` on macOS. On Windows and Linux it only stores the menus (`get_menus`) for the app to draw, which is what gpui-kit's `AppMenuBar` and Zed's title bar do. On Windows, `set_dock_menu` populates the Jump List; on Linux it is `todo(linux)`.
- **Clipboard.** Read and write on all platforms, async read, and the Linux primary selection.
- **Drag and drop.** OS files dropped into a window arrive as `FileDropEvent` / `ExternalPaths`. Dragging from the app out to the OS (`start_external_drag`) is implemented only for macOS and Wayland (`gpui_macos/src/window.rs`, `gpui_linux/.../wayland/window.rs`). It is missing on Windows and X11, so "drag a file out of the VPK onto Explorer" is not available upstream.
- **Accessibility.** AccessKit, with adapters `accesskit_macos`, `accesskit_windows` and `accesskit_unix`, merged in [#56065](https://github.com/zed-industries/zed/pull/56065) (2026-05-27), plus a guide at [`_accessibility.rs`](https://github.com/zed-industries/zed/blob/main/crates/gpui/src/_accessibility.rs). Open issues include [#66690](https://github.com/zed-industries/zed/issues/66690) (cached views drop their a11y nodes), [#66730](https://github.com/zed-industries/zed/issues/66730) and [#66691](https://github.com/zed-industries/zed/issues/66691). gpui-kit claims AccessKit roles, names and states throughout, covered by tests.

## 6. Documentation

Upstream documentation is thin:

- [gpui.rs](https://www.gpui.rs) and the crate README.
- Two long-form guides as rustdoc modules (`_ownership_and_data_flow`, `_accessibility`).
- About 49 examples in [`crates/gpui/examples`](https://github.com/zed-industries/zed/tree/main/crates/gpui/examples), including `uniform_list`, `data_table`, `drag_drop`, `set_menus`, `tree` and `native_view`.
- docs.rs for `gpui` shows the stale 0.2.2. Current API docs live on docs.rs under [`gpui-pre`](https://docs.rs/gpui-pre).

gpui-kit is far better documented: [gpui-kit.com](https://gpui-kit.com) has a doc page per component, there are many examples, and a story gallery (`crates/story`) exists. Zed's own codebase remains the largest reference for advanced patterns.

## 7. Notable non-Zed apps

From [awesome-gpui](https://github.com/zed-industries/awesome-gpui), which Zed maintains:

- **Longbridge Pro**: commercial trading desktop app, the origin of gpui-kit.
- **Pulsar-Native**: game engine editor with a 3D viewport, built on a wgpu GPUI fork.
- **Eleven**: reverse-engineering tool on macOS, Windows and Linux (1K stars).
- **Database clients**: DBDelve (2K), Navop (2.2K), DbCat, DBFlux.
- **Remote workspace**: OxideTerm, an SSH, SFTP, RDP and VNC workspace (2K).
- **Terminal**: tty7 (1.5K).
- **Git client**: GitComet (1K).
- **Graph visualization**: Omnigraph Studio.

Most listed apps are developer tools from 2026. Few, apart from Pulsar, embed foreign GPU content.
