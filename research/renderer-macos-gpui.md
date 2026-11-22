# Research: drawing the C# OpenGL renderer into a GPUI window on Windows, Linux and macOS

Research ticket for the Rust + GPUI desktop shell plan. The C# renderer (`Renderer/`) stays in C#. This note asks how its frames get into a GPUI window on all three platforms, and compares (a) keeping OpenGL 4.6 everywhere, with Mesa Zink on macOS, against (b) putting a new graphics backend under the renderer.

Snapshot date: 2026-11-23. Repo state: `master` at `1661a21b8`. GPUI state: `zed-industries/zed` at `d31d1074a` (2026-11-22).

## Summary

- **The renderer is a GL 4.5/4.6 renderer through and through, not a GL 3.3 one.** It uses DSA everywhere (127 calls in 36 files), 14 compute shaders (55 dispatch, barrier and image-bind calls), std430 SSBOs, image load/store with atomics, multi-draw indirect with `gl_DrawID`/`gl_BaseInstance`, `glClipControl` for reverse-Z, persistent coherent mapped buffers, texture views and `glPolygonOffsetClamp`. macOS's native OpenGL 4.1 has none of these, so **native macOS OpenGL is not an option**, short of a rewrite down to GL 4.1.
- **Some features the renderer does *not* use matter a great deal.** It uses no geometry, tessellation or transform feedback shaders, no bindless textures and no subroutines. Subgroup ops, indirect-count draws and parallel shader compile are optional, with fallbacks already in the code. Those missing stages are exactly what blocks Zink on Apple's Vulkan drivers, and what most backends emulate badly.
- **Route (a), GL 4.6 everywhere, with Zink on macOS:** this works today on Windows and Linux with native drivers. On macOS it is **not production-ready**. Mesa's own docs call Zink on macOS "experimental with very limited capabilities". KosmicKrisp is the Vulkan-on-Metal driver Khronos now recommends on Apple silicon; it needs macOS 26 and Metal 4. KosmicKrisp and MoltenVK both lack geometry shaders and `VK_EXT_transform_feedback`, so Zink reports only GL 2.1 or 3.1 natively. Reaching 4.6 needs `MESA_GL_VERSION_OVERRIDE`. The renderer happens to avoid those stages, so an override may be workable for *this* renderer, but nobody ships it, upstream Mesa has no macOS window-system path for EGL, and the one public 4.6 report needed a patched Mesa and ran about 4x slower. It is worth a 1-2 week spike. It is not a plan of record.
- **Route (b), a new backend under the renderer:** Vulkan (MoltenVK or KosmicKrisp on macOS) is the closest fit. Every GL feature the renderer uses maps onto it. The hard parts are mechanical but large:
  - About 1,668 loose (non-block) `uniform` declarations must move into UBOs or push constants. glslang's "Vulkan relaxed" mode can bridge this, but it has known bugs.
  - All 428 sampler uniforms need explicit set/binding layouts.
  - Mutable GL state must become pipeline objects.
  - `glMemoryBarrier` calls must become explicit barriers.
  - Persistent-mapped streaming must become explicit sync.

  WebGPU through wgpu-native or Dawn is a worse fit. It has no indirect-count on Metal and no persistent mapping. It splits textures from samplers, and its default limit of 16 sampled textures per stage is below the 21 some shaders bind. Veldrid has been effectively unmaintained in public since February 2023. Silk.NET only provides bindings, not an abstraction.
- **Handing frames to GPUI is mostly unsolved in GPUI itself.** On macOS, GPUI can embed a native `NSView` beneath its own content (`attach_native_view`) and can paint a `CVPixelBuffer`. The pixel buffer must be NV12 YUV, so it serves video and not RGBA renders. On Windows (D3D11), `draw_surfaces` is an empty stub and native views return `None`. On Linux (wgpu), surface batches are skipped. Every platform except macOS-with-native-view needs a GPUI patch or fork, either for shared-texture import or for child-surface embedding. The sibling GPUI ticket should own that question.
- **Recommendation for the plan:**
  1. Keep GL 4.6 on Windows and Linux.
  2. Hand off through a **native child surface** first: an `NSView` on macOS via GPUI's existing native-view API, a child HWND on Windows, and a subsurface or child window on Linux. The renderer already runs as a reparented GLFW child window under WinForms, so this is the least change.
  3. Plan for shared textures later: IOSurface, DMA-BUF, or DXGI/`EXT_external_objects_win32`.
  4. Treat macOS rendering as a **separate decision**. Run a Zink + KosmicKrisp spike first. If it fails, the only full answer is a Vulkan backend under the existing `GraphicsDevice`/`GraphicsContext` seam. That is a multi-month port, and it would also replace GL on the other two platforms over time.

## GL feature usage inventory

How it was measured: grep over `Renderer/` (`*.cs`) and `Renderer/Shaders/` (`*.slang`). There are 453 `GL.*` call sites in 71 files under `Renderer/`, plus GUI-side calls in 7 files. All 437 shader files are `#version 460`, including 14 compute shaders and 132 fragment shaders.

### Host API (C#)

| Feature | GL version / extension | Usage | Where (examples) | Why it matters |
|---|---|---|---|---|
| Direct State Access (`glCreate*`, `glNamedBuffer*`, `glTextureStorage*`, `glTextureSubImage*`, `glBindTextureUnit`, `glVertexArray*`, `glBlitNamedFramebuffer`, `glClearNamed*`, `glMapNamedBufferRange`) | 4.5 core | 127 calls, 36 files | `OpenGL/GraphicsDevice.cs`, `Buffers/*`, `RenderTexture.cs`, `Framebuffer.cs`, `Materials/MaterialLoader.cs` | Not on macOS GL 4.1. Maps cleanly to Vulkan/Metal object creation. |
| Compute (`glDispatchCompute`, `glMemoryBarrier`, `glBindImageTexture`) | 4.3 / 4.2 | 19 dispatch + 22 barrier + 14 image binds; 14 `.comp.slang` shaders | `LightBinner.cs`, `DepthPyramid.cs`, `ClutterCuller.cs`, `ScreenSpaceAo.cs`, `SceneEnvironment/VolumetricFog.cs`, `PostProcess/PostProcessRenderer.cs`, `SceneViewState.cs` | Core to lighting (tile/bin culling), culling, SSAO, fog, histogram/exposure, MSAA/depth resolve. Absent on macOS GL. Barrier bits used: SSBO, image, texture fetch, command, buffer update, client-mapped. |
| Multi-draw indirect, indirect count | 4.3; 4.6 / `ARB_indirect_parameters` | `glMultiDrawElementsIndirect`, `glMultiDrawElementsIndirectCount`, `glDrawArraysIndirect`, `glDrawElementsIndirect` (5 sites) | `MeshBatchRenderer.cs:338`, `Scene.cs:1450` | Count variant is already optional (`GLEnvironment.IndirectCountSupported`, off on Intel), so a backend without it is fine. |
| Base instance draws | 4.2 | 15 calls, 10 files | `MeshBatchRenderer.cs`, particles, decals | Needs `firstInstance` in indirect commands; Metal and Vulkan have it, WebGPU only behind `INDIRECT_FIRST_INSTANCE`. |
| Immutable / persistent mapped buffers | 4.4 `ARB_buffer_storage` | `MapPersistentBit` ×3, `MapCoherentBit` ×3, `ClientStorageBit`, `DynamicStorageBit` ×4 | `Buffers/StagingRing.cs`, `Buffers/ReadbackRing.cs`, `Buffers/StreamingVertexBuffer.cs` | Fine in Vulkan/Metal (host-visible memory). WebGPU has no persistent mapping (mapAsync only). |
| Fences | 3.2 | `glFenceSync`, `glClientWaitSync`, `glDeleteSync`, `glFinish`, `glFlush` (10) | staging/readback rings | Become Vulkan fences / timeline semaphores. |
| Texture views | 4.3 | `glTextureView` | `OpenGL/GraphicsDevice.cs:118` | Supported everywhere modern. |
| Copy / clear images | 4.3 / 4.4 | `glCopyImageSubData`, `glClearTexImage`, `glClearTexSubImage` (9) | 7 files | Clear-texture is native-only in wgpu (`CLEAR_TEXTURE`). |
| Clip control (reverse-Z, 0..1 depth) | 4.5 | `glClipControl(LowerLeft, ZeroToOne)` | `OpenGL/GLEnvironment.cs:166` | Vulkan/Metal/WebGPU are natively 0..1; their Y-flip differs from GL. |
| Polygon offset clamp | 4.6 | `glPolygonOffsetClamp` | `RenderState.cs:341` | Zink requires `depthBiasClamp` for 4.6; Vulkan has it, Metal has a depth-bias clamp. |
| Polygon mode line (wireframe) | 1.0 | `glPolygonMode` ×3 | | wgpu `POLYGON_MODE_LINE` is native-only; Metal has `setTriangleFillMode`. |
| Queries | 3.3 timer, 3.0 | `Timestamp` ×2, `PrimitivesGenerated` ×3 | `Timings.cs`, `PerfStats.cs` | `PrimitivesGenerated` has no WebGPU equivalent; Vulkan needs pipeline statistics, which MoltenVK does not support. Debug-only stats. |
| Debug output, labels | 4.3 `KHR_debug` | `glDebugMessageCallback`, `glObjectLabel`, `glPushDebugGroup` | `OpenGL/GLDebugGroup.cs` | Vulkan `VK_EXT_debug_utils`. |
| Program reflection, loose uniforms | 2.0 | `glProgramUniform*`, `glGetUniformLocation`, `glGetActiveUniform(s)`, `glGetActiveUniformBlock` | concentrated in `Shaders/Shader.cs` (875 lines) | Loose uniforms do not exist in Vulkan GLSL (see the shaders table). The good news: every call site goes through `Shader.SetUniform`/`SetTexture`. |
| Optional extensions | `KHR/ARB_parallel_shader_compile`, `NVX_gpu_memory_info`, `ATI_meminfo`, `EXT_texture_filter_anisotropic` | | `OpenGL/GLEnvironment.cs` | All optional. |
| Compressed formats | BC1-7 (S3TC, RGTC, BPTC, core 4.2), ETC2/EAC (core 4.3) | | `GLImageFormatExtensions.cs` | BC is fine on desktop and Apple-silicon Metal. Desktop Vulkan drivers commonly lack ETC2; GL drivers decompress it, so a Vulkan port needs a CPU decode fallback (VRF has texture decoders). |
| Readback | | `glReadPixels` (picking 1x1, thumbnails, screenshots) | `PickingTexture.cs:88`, `ThumbnailRenderer.cs:129` | Trivial to port. |

### Shaders (GLSL in `.slang` files)

| Feature | Count | Notes |
|---|---|---|
| `#version 460` | 437 files | Compiled at runtime per combo by `Shaders/ShaderLoader.cs` (custom preprocessor with `#include` and `#define` combos, then `glShaderSource`) |
| Loose `uniform` scalars, vectors and matrices (outside blocks) | ~1,668 declarations | Illegal in `GL_KHR_vulkan_glsl` ("default uniforms ... except for opaque types" are removed). This is the single biggest shader-side porting cost. |
| `uniform sampler*` | 428 (sampler2D 393, sampler2DArray 18, sampler3D 15, samplerCube 7, sampler2DMS 3, shadow 3, samplerCubeArray 1) | Combined image-samplers. Vulkan accepts them. WGSL/WebGPU need them split. Up to 21 samplers in one file (`steamtours/vr_standard.slang`) plus common includes. |
| Uniform blocks (`std140`) | 6 blocks / 4 files | Globals UBO layout is computed in C# and checked against the driver (`Shader.cs` `VerifyGlobalsLayout`). |
| SSBOs (`std430` / `buffer`) | 37 declarations, 25 files | Light lists, cull bits, instance data, indirect commands |
| Image load/store, atomics | 27 `imageLoad`/`imageStore`, 12 atomic sites | Compute post, fog, light binning |
| `shared` memory, `barrier()` | 20 / 21 | Compute |
| `gl_BaseInstance`, `gl_DrawID`, `gl_InstanceID` | 8 / 1 / 1 | `ARB_shader_draw_parameters` semantics (core 4.6) |
| Subgroups | `GL_KHR_shader_subgroup_*` in 3 files, all `#ifdef`-guarded | Optional fast path |
| Geometry / tessellation / clip distance / layered rendering / subroutines / bindless | 0 | Nothing here hits Zink's or MoltenVK's weak spots |

There is already a backend seam to build on. `Renderer/Renderer/OpenGL/GraphicsDevice.cs` and `GraphicsContext.cs` introduce a device/context split, where `IGraphicsSurface` is "the window side of a graphics context". Its comment says "a device holding a real API object will" read instance state. But only object *creation* goes through it today. Draws, state and binding are still raw `GL.*` calls in about 70 files.

## Route comparison

| | (a1) GL 4.6, Zink on macOS, **native child surface** | (a2) GL 4.6, Zink on macOS, **shared texture** into GPUI | (b1) **Vulkan** backend (MoltenVK / KosmicKrisp on macOS) | (b2) **WebGPU** backend (wgpu-native / Dawn) | (b3) **Metal** backend for macOS only (keep GL elsewhere) | (b4) Veldrid / Silk.NET |
|---|---|---|---|---|---|---|
| Windows | Works today (native GL 4.6, child HWND as now) | Works: `WGL_NV_DX_interop2` (NVIDIA-authored; AMD/Intel expose it in practice, unverified) or `EXT_memory_object_win32` with D3D11 image handles into GPUI's D3D11 device | Works (native Vulkan) | Works (D3D12/Vulkan) | n/a (stay GL) | Veldrid D3D11/Vulkan; Silk.NET is bindings only |
| Linux | Works (native GL); Wayland needs a subsurface, X11 a child window | Works: `EGL_MESA_image_dma_buf_export` (GL→DMA-BUF), import into GPUI's wgpu/Vulkan via `VK_EXT_external_memory_dma_buf` | Works | Works (Vulkan) | n/a | as above |
| macOS | **Experimental.** KosmicKrisp needs macOS 26 + Apple silicon; MoltenVK for Intel. Zink natively reports GL 2.1/3.1 there, 4.6 only by override. No upstream EGL window surface on macOS | Same Zink risk, plus no upstream IOSurface export from Mesa EGL; `glReadPixels` + upload works but costs a CPU copy | Works through a layer; feature gaps (no pipeline statistics, no geometry/xfb, which are unused) | Works (Metal); no indirect-count on Metal | Works natively | Veldrid Metal backend, unmaintained |
| GL features that block or complicate | Zink on Apple drivers: missing geometryShader/xfb (unused, but they gate the version), nullDescriptor and other gaps; compute, SSBO and MDI untested there | Same, plus cross-API sync | Loose uniforms (~1,668); set/binding for 428 samplers; GL state to PSOs; `glMemoryBarrier` to barriers; ETC2 fallback; `PrimitivesGenerated` | Everything in b1, plus split samplers, ≤16 sampled textures/stage default, no persistent mapping, no indirect-count on Metal, `CLEAR_TEXTURE`/`POLYGON_MODE_LINE`/storage-texture formats native-only | Everything in b1 at MSL level via SPIRV-Cross; second backend to maintain | Veldrid: no indirect-count; public maintenance stopped Feb 2023 |
| Performance / latency | Native path on Win/Linux: zero copy, own swapchain, vsync as today. Zink: CPU overhead per call (anecdotal 4x in a GL-heavy game), likely less for this GPU-driven renderer, but unmeasured | One extra GPU composite + cross-API sync; up to +1 frame if GPUI composes on its own display-link cadence | Native-class; MoltenVK/KK translation overhead small vs Zink+layer stack | Some overhead from validation and the mapAsync streaming model | Best on macOS | n/a |
| Input and resize | Native view gets OS input; GPUI on macOS already routes focus, keys and hit-testing for native views. Windows/Linux need GPUI patches. Resize: native surface resize, possible flicker | GPUI owns input and layout; forward to C#. Resize = reallocate shared texture + sync, easy to make tear-free | Same as a1 or a2 (orthogonal to backend) | same | same | same |
| Porting effort | Small on Win/Linux (renderer unchanged); macOS spike 1-2 weeks, productising unknown (Mesa patches, shipping Mesa in the app) | Medium: per-OS interop code (3 paths) + GPUI patches | Large: finish the `GraphicsDevice` seam over ~70 files, shader pipeline rework, PSO cache, barriers. Months | Larger than b1 | Large, and only for one OS; doubles the backend count | Large, for a dead or thin dependency |
| Risk | High on macOS (experimental, depends on fast-moving Mesa forks); low elsewhere | High on macOS; medium elsewhere | Medium: well-trodden; MoltenVK is now community-maintained, KosmicKrisp is new | Medium-high: feature limits fight the renderer's design | Medium; maintenance burden | High |

## Details

### 1. Why macOS needs anything at all

Apple deprecated OpenGL in macOS 10.14 ([macOS Mojave 10.14 release notes](https://developer.apple.com/documentation/macos-release-notes/macos-mojave-10_14-release-notes)), and its core profile stops at GL 4.1. The renderer refuses to start below 4.6 (`GLEnvironment.RequiredVersion`, `GLEnvironment.cs:12-18`). The inventory shows that the 4.3-4.6 features carry real work: compute light binning, GPU culling and MDI, reverse-Z clip control, and persistent streaming rings. They are not incidental. Dropping to 4.1 would mean rewriting the lighting, culling and post pipelines as fragment-shader passes, and that is out of scope.

### 2. Route (a): keep OpenGL 4.6

#### Windows and Linux

These work as today. GUI already hosts the renderer as a hidden, borderless GLFW window reparented into a WinForms control, with its context moved between the UI and a loader thread (`GUI/Types/GLViewers/GLBaseControl.cs:923-1010`). Rendering runs on `RenderLoopThread`, and `PostprocessRender(source, target)` already takes an arbitrary target framebuffer (`ThumbnailRenderer.cs:209`). So rendering to an offscreen FBO for a shared-texture handoff needs no renderer change.

#### macOS through Zink

- **What Zink needs.** [Mesa's Zink page](https://docs.mesa3d.org/drivers/zink.html) lists cumulative Vulkan requirements per GL version:
  - GL 3.0 needs `VK_EXT_transform_feedback`.
  - GL 3.2 needs `geometryShader`.
  - GL 4.0 needs `tessellationShader`.
  - GL 4.3 needs `robustImageAccess2` and 1024 compute invocations.
  - GL 4.6 needs `depthBiasClamp`, `samplerAnisotropy`, `VK_KHR_draw_indirect_count` and subgroup vote.

  On macOS, the same page says: "Zink on macOS is experimental with very limited capabilities. It runs on a macOS Vulkan driver such as KosmicKrisp or MoltenVK" (build with `-Dgallium-drivers=zink`, plus `-Dmoltenvk-dir=` for MoltenVK).
- **Which Vulkan driver.** [Khronos's porting page](https://vulkan.org/porting) states: "Khronos has deprecated MoltenVK ... now maintained by the community on a best-effort basis", and "For Vulkan on Metal, Khronos now recommends KosmicKrisp on Apple Silicon Macs. MoltenVK remains the recommended option for Intel-based Macs and for iOS." [Mesa's KosmicKrisp page](https://docs.mesa3d.org/drivers/kosmickrisp.html) adds: "implemented on top of Metal 4, which requires macOS 26 and up", and M1/M2 on macOS 26 are "known to run into a considerably higher amount of issues". KosmicKrisp ships in the LunarG macOS Vulkan SDK ([LunarG announcement](https://www.lunarg.com/a-vulkan-on-metal-mesa-3d-graphics-driver/)).
- **The version gate.** Neither Apple driver offers geometry shaders or transform feedback upstream:
  - MoltenVK's request for `VK_EXT_transform_feedback` has been open since 2022 ([MoltenVK #1588](https://github.com/KhronosGroup/MoltenVK/issues/1588)).
  - Third-party trackers report that upstream KosmicKrisp advertises neither `geometryShader` nor `VK_EXT_transform_feedback`, and that Zink therefore reports desktop GL 2.1 on KosmicKrisp and 3.1 on MoltenVK ([UTM #22](https://github.com/joshan-kana/UTM/issues/22), [ScarletNX #1106](https://github.com/scarletnx/ScarletNX/issues/1106)). These are secondary sources and the numbers are unverified.
  - LunarG lists tessellation and geometry as a priority ([Vulkanised 2026 slides](https://vulkan.org/user/pages/09.events/vulkanised-2026/1545-Richard-Wright-LunarG.pdf)), and forks implement them ([maclauncher-kosmickrisp](https://github.com/procrastinine/maclauncher-kosmickrisp)).
  - Because VRF uses none of those stages, `MESA_GL_VERSION_OVERRIDE=4.6` + `MESA_GLSL_VERSION_OVERRIDE=460` could expose everything the renderer actually calls. That is an inference and needs a spike to confirm.
- **The only public GL 4.6-on-macOS report** ([gist: Minecraft on Zink + KosmicKrisp](https://gist.github.com/lucamignatti/5312f5e937de2ba44256ecba6de54cc2)) needed a lot of work:
  - It used a personal Mesa fork with patched `platform_surfaceless.c` to add macOS window surfaces, GLFW patched to create contexts via EGL instead of NSGL, and a `dlsym` interposer.
  - It ran with `EGL_PLATFORM=surfaceless MESA_LOADER_DRIVER_OVERRIDE=zink MESA_GL_VERSION_OVERRIDE=4.6`.
  - It needed workarounds for missing `nullDescriptor` (later merged per the comments), stale shader caches, black frames and gray alpha.
  - Commenters report 100 FPS native versus 25 FPS via Mesa.

  Upstream Mesa therefore has no supported way to create a windowed GL context on macOS. A surfaceless EGL context rendering to an FBO is the realistic upstream path, and it implies a shared-texture or readback handoff (a2).
- **Shipping cost.** The app would bundle Mesa (libEGL, libGLESv2/libGL with Zink) plus a Vulkan ICD (KosmicKrisp or MoltenVK), and load it through OpenTK's binding context. `GLBaseControl.cs:1262` already builds a custom `GLFWBindingsContext`, so the loader is swappable. OpenTK's windowing (GLFW → NSGL) cannot be used; context creation would go through EGL directly.

#### The handoff for route (a)

- **(a1) Native child surface.** This is what the app does today on Windows. In GPUI:
  - macOS has `PlatformWindow::attach_native_view`. The view "sits beneath the window's rendered content, so the renderer erases everything painted earlier within these bounds, and everything painted later covers the view" (`crates/gpui/src/scene.rs`, `PaintNativeView`). `crates/gpui_macos/src/window/native_views.rs` routes first-responder focus, mouse hit-testing and key pre-filtering (`on_key_down_before_native_view`). GPUI popovers can therefore draw *over* the viewport on macOS. That is better than a Win32 child HWND, which always covers its parent's DirectComposition content.
  - The default trait implementation returns `None`, "when the platform cannot composite native views" (`crates/gpui/src/platform.rs:1369-1385`). Only `gpui_macos` overrides it, so Windows and Linux need GPUI patches. On Windows, GPUI's D3D11 renderer uses DirectComposition (`gpui_windows/src/directx_renderer.rs:116`), and a child HWND sits above that visual tree. On Wayland it needs a `wl_subsurface`.
  - Zink on macOS would need a `CAMetalLayer`-backed `NSView`. The gist had to swap the layer at runtime "because a plain NSViewBackingLayer is not a CAMetalLayer".
- **(a2) Shared texture.**
  - macOS: GPUI's `window.paint_surface(bounds, CVPixelBuffer)` and the `surface()` element exist, but are `#[cfg(target_os = "macos")]` only. The Metal renderer asserts `kCVPixelFormatType_420YpCbCr8BiPlanarFullRange` (`crates/gpui_apple/src/metal_renderer.rs`, around line 1269), so the path takes NV12 video frames, not BGRA/RGBA renders. Using it for the viewport means a small GPUI patch for a BGRA plane. Nothing in upstream Mesa exports an IOSurface from Zink/EGL either.
  - Windows: `DirectXRenderer::draw_surfaces` returns `Ok(())` without drawing (`gpui_windows/src/directx_renderer.rs:845`). The GL side is available through [`WGL_NV_DX_interop2`](https://registry.khronos.org/OpenGL/extensions/NV/WGL_NV_DX_interop2.txt), which adds D3D10/11 resources and needs lock/unlock around GL use, or through [`EXT_external_objects_win32`](https://registry.khronos.org/OpenGL/extensions/EXT/EXT_external_objects_win32.txt), which defines `HANDLE_TYPE_D3D11_IMAGE_EXT` and D3D12 fence semaphores.
  - Linux: wgpu's `PrimitiveBatch::Surfaces(_)` arm is empty (`gpui_wgpu/src/wgpu_renderer.rs:1545`). The GL side exports through [`EGL_MESA_image_dma_buf_export`](https://registry.khronos.org/EGL/extensions/MESA/EGL_MESA_image_dma_buf_export.txt), or allocates in Vulkan and imports into GL with [`EXT_external_objects_fd`](https://registry.khronos.org/OpenGL/extensions/EXT/EXT_external_objects_fd.txt) (`glImportMemoryFdEXT`, `glImportSemaphoreFdEXT`, opaque fd only).
  - Cost: one sampled-texture composite inside GPUI's frame plus a cross-API wait (semaphore, keyed mutex or fence). Latency is up to one extra frame unless the renderer's frame production is paced off GPUI's display link instead of its own `RenderLoopThread`.
  - Resize: GPUI owns layout, so the C# side reallocates the shared image on bounds change, typically double-buffered to avoid stalls.

### 3. Route (b): a new backend under the C# renderer

#### Where it plugs in

`GraphicsDevice`/`GraphicsContext`/`IGraphicsSurface` (`Renderer/Renderer/OpenGL/`) is the start of a backend seam. Finishing it means routing state (`RenderState.cs`, `RenderStateTracker`), binding (`Shader.SetTexture`, UBO/SSBO binds), draws (`MeshBatchRenderer.cs`, particles, decals, tool render) and compute (11 files) through it. Today that is 453 `GL.*` call sites in 71 files. Uniform setting is already centralised in `Shaders/Shader.cs`, which helps.

#### Shaders

The GLSL 460 sources can stay as the source language:

1. Compile them to SPIR-V at runtime per combo with glslang/shaderc. This replaces `glShaderSource` in `ShaderLoader.cs:426`. shaderc .NET bindings ship in [Vortice.Vulkan](https://github.com/amerkoleci/Vortice.Vulkan) ("bindings for Vulkan, VMA, SPIRV-Cross and shaderc").
2. Cross-compile to MSL or WGSL where needed with SPIRV-Cross. `ValveResourceFormat.csproj` already references `Vortice.SpirvCross` 1.6.0 for decompiling game shaders (`ShaderSpirvReflection.cs`, `ShaderExtract.cs`), and its reflection can replace `glGetActiveUniform`.

naga is not the right front end. Its GLSL input is "secondary support ... GLSL 440+ and Vulkan semantics only" ([naga README](https://github.com/gfx-rs/wgpu/tree/trunk/naga)), and it would still need the shaders converted to Vulkan semantics first.

The blockers, in order of size:

1. **Loose uniforms.** [`GL_KHR_vulkan_glsl`](https://docs.vulkan.org/glslext/latest/glslext/khr/GL_KHR_vulkan_glsl.html) removes "default uniforms (uniform variables not inside a uniform block), except for opaque types". About 1,668 declarations are affected. glslang has a relaxed mode (`-R`, `setEnvInputVulkanRulesRelaxed()`) that packs them into a global uniform block and auto-maps bindings, but it has open correctness issues (for example, [glslang #3549](https://www.github.com/KhronosGroup/glslang/issues/3549) on validation errors, and [#3594](https://www.github.com/KhronosGroup/glslang/issues/3594) on samplers in structs). The sturdier fix is to generate a per-shader material UBO from the declarations, which the C# side already half-does for globals (`GlobalsLayout`, `VerifyGlobalsLayout`), and turn `SetUniform` into writes to a CPU-side block.
2. **Explicit bindings** for 428 combined samplers and 37 SSBOs. Auto-map is possible, but descriptor-set design (per-frame / per-material / per-draw) is the real work.
3. **Fixed-function state to pipelines.** Blend, depth, stencil, cull, polygon offset and polygon mode move into PSOs, keyed by render state × shader combo × vertex layout × render-target formats. A pipeline cache is needed. Vulkan dynamic-state extensions reduce the explosion.
4. **Barriers.** The 22 `glMemoryBarrier` sites become explicit pipeline barriers or a small render graph.
5. **Coordinate conventions.** Reverse-Z with 0..1 depth carries over. Framebuffer Y-origin and texture-coordinate flips need care.

#### (b1) Vulkan

- Feasibility:
  - Windows and Linux are native.
  - macOS goes through MoltenVK ("a layered implementation of Vulkan 1.4 graphics and compute functionality"; it lists `VK_KHR_draw_indirect_count`, `VK_EXT_metal_objects` and `VK_EXT_external_memory_metal` ([MoltenVK user guide](https://github.com/KhronosGroup/MoltenVK/blob/main/Docs/MoltenVK_Runtime_UserGuide.md))), or through KosmicKrisp on macOS 26.
  - MoltenVK's documented limitations include no pipeline statistics queries, which only affects the debug `PrimitivesGenerated` stat. Subgroup vote/ballot "Requires Mac GPU family 2 or Apple GPU family 4". It is optional here anyway.
- Interop with GPUI is cleaner than from GL: Vulkan external memory with opaque fd or DMA-BUF on Linux, `VK_KHR_external_memory_win32` with D3D11 texture handles on Windows, and `VK_EXT_metal_objects` to export an `IOSurface`/`MTLTexture` on macOS.
- Effort: months. It is the backend that best matches the renderer's GPU-driven design.

#### (b2) WebGPU via wgpu-native or Dawn

- Attraction: GPUI on Linux already renders with wgpu (`crates/gpui_wgpu`, pinned git revision `cddb2f5d`). But a C# process cannot share wgpu's Rust device objects across FFI in any supported way, so this buys little for composition.
- Feature friction against this renderer ([wgpu `Features`](https://docs.rs/wgpu/latest/wgpu/struct.Features.html)):
  - `MULTI_DRAW_INDIRECT_COUNT` is native-only, on "DX12" and "Vulkan 1.2+", not Metal. The renderer can fall back, as it does on Intel.
  - `TEXTURE_BINDING_ARRAY`, `POLYGON_MODE_LINE`, `PIPELINE_STATISTICS_QUERY`, `CLEAR_TEXTURE` and `TEXTURE_ADAPTER_SPECIFIC_FORMAT_FEATURES` (without which "read/write storage access is not allowed at all") are native-only.
  - Push constants are "immediates".
  - Beyond the features list, WebGPU has no persistent mapping (StagingRing/ReadbackRing need a rework), splits textures from samplers, and defaults to 16 sampled textures per stage (adapters can raise it natively).
- Bindings: [wgpu-native](https://github.com/gfx-rs/wgpu-native) builds on `webgpu.h` plus `wgpu.h` extensions, with .NET bindings from Silk.NET, wgpu.NET and Alimer.Bindings.WebGPU.

#### (b3) Metal for macOS only

This avoids the translation layer but adds a second production backend and an MSL cross-compile path. It is only worth it if the Vulkan layers prove inadequate. GPUI on macOS is Metal, so IOSurface sharing within one process is straightforward.

#### (b4) Abstraction libraries

- [Veldrid](https://github.com/veldrid/veldrid) offers D3D11, Vulkan, Metal, GL 3 and GLES 3, but "As of February 2023, I'm no longer able to publicly share updates". It has no indirect-count.
- [Silk.NET](https://github.com/dotnet/Silk.NET) is raw bindings (OpenGL, Vulkan, WebGPU, DirectX). "2.X investment is currently limited", and 3.0 is in progress. It is useful as a binding source, not as an abstraction.

Neither removes the work in sections 3.1-3.5.

### 4. Input, resize and threading, common to all routes

- The renderer already renders off the UI thread (`RenderLoopThread`), with context hand-off between threads (`GLBaseControl.cs:923-970`). Under GPUI, the C# renderer can keep its own thread. The handoff point is either a native surface the renderer presents to itself (a1), or a shared image plus a fence that GPUI samples (a2).
- Input: with a native child surface, OS events go to the view first. GPUI macOS already arbitrates focus and keys for native views; other platforms need equivalent patches. With a shared texture, GPUI's element receives the mouse and keyboard and forwards them to C# over FFI. The current raw-mouse capture (`GLFW.RawMouseMotionSupported`, `GLBaseControl.cs:616`) would move into the Rust shell.
- Resize: a child surface resizes its own swapchain; that is the existing behaviour, and it can flicker on Windows during live resize. A shared texture is reallocated on layout change, and GPUI stretches the old frame until the new one is ready.

## Open questions and suggested spikes

1. **Zink on macOS spike, 1-2 weeks.**
   1. Build Mesa with `-Dplatforms=macos -Degl=enabled -Degl-native-platform=surfaceless -Dgallium-drivers=zink` against the LunarG SDK's KosmicKrisp and MoltenVK.
   2. Create an EGL surfaceless 4.6 context with the version override.
   3. Run `Misc/RenderTest` and `Misc/ShaderValidator` offscreen.
   4. Measure compile success across combos, correctness on a CS2 and a Deadlock map, and frame time against a Vulkan-native baseline.

   This decides whether route (a) can cover macOS at all.
2. **GPUI patch scope.** Port native views to Windows (child HWND under DirectComposition) and Linux (Wayland subsurface, X11 child), or add RGBA `paint_surface` on all three. This belongs to the sibling GPUI ticket.
3. **Vulkan port estimate.** Prototype one path (globals UBO + one material shader + one compute pass) through glslang → SPIR-V → Vulkan behind `GraphicsDevice`, to measure the loose-uniform conversion and PSO-cache cost before committing.
4. **ETC2 on Vulkan desktop.** Confirm per-vendor `textureCompressionETC2` support and the CPU-decode fallback cost.

## Sources

- Repo code: `Renderer/Renderer/OpenGL/GLEnvironment.cs`, `GraphicsDevice.cs`, `GraphicsContext.cs`, `Shaders/Shader.cs`, `Shaders/ShaderLoader.cs`, `MeshBatchRenderer.cs`, `Buffers/*`, `GUI/Types/GLViewers/GLBaseControl.cs`, `Renderer/Shaders/**`
- GPUI (zed `d31d1074a`): `crates/gpui/src/scene.rs` (`PaintSurface`, `PaintNativeView`), `crates/gpui/src/window.rs` (`paint_surface`), `crates/gpui/src/elements/surface.rs`, `crates/gpui/src/platform.rs` (`attach_native_view`), `crates/gpui_macos/src/window/native_views.rs`, `crates/gpui_apple/src/metal_renderer.rs`, `crates/gpui_windows/src/directx_renderer.rs`, `crates/gpui_wgpu/src/wgpu_renderer.rs`
- Mesa: [Zink](https://docs.mesa3d.org/drivers/zink.html), [KosmicKrisp](https://docs.mesa3d.org/drivers/kosmickrisp.html)
- Khronos: [Vulkan porting page (MoltenVK deprecation, KosmicKrisp recommendation)](https://vulkan.org/porting), [GL_KHR_vulkan_glsl](https://docs.vulkan.org/glslext/latest/glslext/khr/GL_KHR_vulkan_glsl.html), [EXT_external_objects](https://registry.khronos.org/OpenGL/extensions/EXT/EXT_external_objects.txt), [EXT_external_objects_fd](https://registry.khronos.org/OpenGL/extensions/EXT/EXT_external_objects_fd.txt), [EXT_external_objects_win32](https://registry.khronos.org/OpenGL/extensions/EXT/EXT_external_objects_win32.txt), [WGL_NV_DX_interop2](https://registry.khronos.org/OpenGL/extensions/NV/WGL_NV_DX_interop2.txt), [EGL_EXT_image_dma_buf_import](https://registry.khronos.org/EGL/extensions/EXT/EGL_EXT_image_dma_buf_import.txt), [EGL_MESA_image_dma_buf_export](https://registry.khronos.org/EGL/extensions/MESA/EGL_MESA_image_dma_buf_export.txt)
- MoltenVK: [Runtime user guide](https://github.com/KhronosGroup/MoltenVK/blob/main/Docs/MoltenVK_Runtime_UserGuide.md), [#1588 transform feedback](https://github.com/KhronosGroup/MoltenVK/issues/1588)
- LunarG: [KosmicKrisp announcement](https://www.lunarg.com/a-vulkan-on-metal-mesa-3d-graphics-driver/), [Vulkanised 2026 slides](https://vulkan.org/user/pages/09.events/vulkanised-2026/1545-Richard-Wright-LunarG.pdf)
- Apple: [macOS Mojave 10.14 release notes (OpenGL deprecation)](https://developer.apple.com/documentation/macos-release-notes/macos-mojave-10_14-release-notes)
- wgpu / naga: [wgpu Features](https://docs.rs/wgpu/latest/wgpu/struct.Features.html), [wgpu-native](https://github.com/gfx-rs/wgpu-native), [naga](https://github.com/gfx-rs/wgpu/tree/trunk/naga)
- .NET: [Silk.NET](https://github.com/dotnet/Silk.NET), [Veldrid](https://github.com/veldrid/veldrid), [Vortice.Vulkan](https://github.com/amerkoleci/Vortice.Vulkan)
- glslang: [#3549](https://www.github.com/KhronosGroup/glslang/issues/3549), [#3594](https://www.github.com/KhronosGroup/glslang/issues/3594)
- Secondary or anecdotal, flagged in the text: [Zink + KosmicKrisp Minecraft gist](https://gist.github.com/lucamignatti/5312f5e937de2ba44256ecba6de54cc2), [UTM #22](https://github.com/joshan-kana/UTM/issues/22), [ScarletNX #1106](https://github.com/scarletnx/ScarletNX/issues/1106), [maclauncher-kosmickrisp](https://github.com/procrastinine/maclauncher-kosmickrisp)
