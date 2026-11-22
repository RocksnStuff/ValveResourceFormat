# Calling the C# ValveResourceFormat library from a Rust + GPUI Shell

Question: what are the viable ways for a Rust + GPUI Shell to call `ValveResourceFormat/` (C#, net11.0, `PublishAot`/`IsAotCompatible` in Release) on Windows, Linux and macOS, and what does each cost? The C# `Renderer/` (OpenGL 4.6 via OpenTK) also stays C#.

Researched 2026-11-23 against Microsoft Learn, the dotnet/runtime repo, crate READMEs and docs.rs. A throwaway probe was also built on macOS arm64 with the .NET 11.0.100 SDK and Rust 1.98.1 (see [Probe](#probe-measured-on-macos-arm64)). The probe was not run on Windows or Linux.

## Summary

All three mechanisms work on all three OSes. The two in-process options use **the same C ABI**: `[UnmanagedCallersOnly]` static methods taking blittable arguments. The probe ran one `Exports.cs` unchanged as a NativeAOT `.dylib` and through hostfxr (via the `netcorehost` crate). The choice between them is mostly about packaging and debugging, not API design. Out-of-process replaces the C ABI with an RPC schema. In exchange it gets crash isolation and richer types, but it needs a separate channel for large buffers and for frames.

| Concern | A. NativeAOT shared library (C exports) | B. Host CoreCLR in-process (nethost/hostfxr) | C. Out-of-process C# backend (IPC) |
|---|---|---|---|
| Large buffers (textures, vertices, VPK bytes) | Zero-copy pointer + length. Memory is native (`NativeMemory`) or pinned managed memory. Same address space. | Same as A. | Must be copied through the socket, or sent through shared memory. .NET named maps are Windows-only. Unix needs a file-backed map or fd passing. |
| Callbacks | `delegate* unmanaged` function pointer + `void*` context. Runs on the calling C# thread. | Same as A. | Server-to-client notifications or streams. Asynchronous by nature. |
| Progress / cancellation | Hand-built: callback return codes, or a cancel export that drives a `CancellationTokenSource`. | Same as A. | Built into the frameworks: gRPC deadlines and cancellation, StreamJsonRpc `CancellationToken` and `IProgress<T>`. |
| Threading / GC | Rust threads attach to the runtime automatically on first call. A separate GC lives inside the dylib. Pointers to managed memory are valid only while pinned. | Same as A, plus one runtime per process with no unload. hostfxr handles are not thread-safe. | Separate processes and separate GCs. Nothing is shared. |
| Error propagation | Must catch at every export. An exception that escapes aborts the process (probe: SIGABRT, exit 134). A non-blittable signature is a fail-fast in .NET 11. | Same as A. | Errors are serialized as RPC errors. A crash in the backend leaves the Shell running. |
| Debuggability | Native debuggers only (lldb, gdb, WinDbg, VS native). Line numbers and locals work. No managed debugger, no managed heap analysis. | Managed debugger works under JIT. Mixed-mode has documented limits. | Each process uses its own debugger. This is the simplest option to debug. |
| Binary size (probe, osx-arm64) | 5.2 MB for 2 exports (trimmed). 29.2 MB with the whole library rooted. Plus native deps (SkiaSharp 16.0 MB, spirv-cross 7.6 MB). | About 8.3 MB of IL assemblies, plus the shared runtime (85 MB on disk), or a user-installed .NET 11 runtime. Plus the same native deps. | A NativeAOT exe about the size of A, plus the IPC stack, plus the same native deps. |
| Packaging per platform | Build on each target OS. Cross-OS compilation is not supported. One dylib/so/dll per RID, beside the Shell. Library and renderer must be in **one** NativeAOT image. | Ship nethost, plus either an app-local runtime folder or a dependency on an installed runtime. Self-contained *components* are unsupported. | Two executables per platform. On macOS the helper is signed and notarized inside the bundle. |
| Effort to expose the Shell API | A flat C facade in C#, plus a C header or hand-written `extern "C"` block in Rust. No first-party header generator exists for NativeAOT exports. | Same facade. Rust resolves function pointers by type and method name instead of symbols. | An RPC contract (proto/MessagePack) with codegen on both sides, plus a shared-memory side channel. |
| Renderer in the same process | Yes. The renderer is compiled into the same image. The Shell still has to get GL frames into a GPUI that does not use GL. | Yes, same as A. | The renderer lives in the backend. Frames cross processes through OS shared-texture handles, or are read back to the CPU. |

Cross-cutting facts that apply to every option:

- **The OpenGL renderer cannot run natively on macOS with any mechanism.** `GLEnvironment.RequiredVersion` is 4.6 (`Renderer/Renderer/OpenGL/GLEnvironment.cs:12-18`). macOS OpenGL stops at 4.1 and is deprecated. GPUI draws with Metal on macOS, Vulkan (Blade) on Linux and DirectX 11 on Windows ([Zed Windows progress report](https://zed.dev/blog/windows-progress-report)). So the renderer's frames always have to cross from GL into another API, by CPU readback or by GPU interop.
- **The native dependencies ship with every option**: `libSkiaSharp` and `libspirv-cross` (from `Vortice.SpirvCross`, `ValveResourceFormat/ValveResourceFormat.csproj:31`). The probe found them next to the NativeAOT dylib with no extra configuration.
- **Every option needs a facade.** The library's API is object-graph C# (`Resource`, `Package`, `Texture`, `SKBitmap`). None of the mechanisms can expose it directly. The facade has to be coarse, handle-based and data-oriented. If the renderer stays C#, mesh and vertex data never have to cross the boundary. Only textures, text dumps, entry bytes, tree listings and frames do.

## A. NativeAOT shared library with `[UnmanagedCallersOnly]` exports

How it works: publish a class library with `PublishAot`. "Publishing a class library as Native AOT creates a native library that exposes methods of the class library annotated with [UnmanagedCallersOnlyAttribute] with a non-null EntryPoint field." ([Building native libraries](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)). Only methods in the published assembly are exported. Methods in project references are not ([NativeAOT interop](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)). That means a dedicated export project referencing `ValveResourceFormat` (and `Renderer`), which is what the probe did.

**Signatures.** "Methods annotated with UnmanagedCallersOnly don't perform marshalling and require blittable parameter and return types" ([interop](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)). They must be static, non-generic, and not called from managed code ([UnmanagedCallersOnlyAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute)). New in .NET 11: when an unmanaged caller invokes a method with a non-blittable signature (for example a `bool` return or a `char` parameter), NativeAOT throws `InvalidProgramException`. That exception causes the process to fail fast, and it can't be caught to recover from the invalid signature ([interop](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)). Strings cross as UTF-8 pointer + length. Objects cross as opaque handles (`GCHandle.ToIntPtr`).

**Large buffers.** Both sides share one address space, so a buffer crosses as `(ptr, len)` with no copy at the boundary. There are two patterns. The probe exercised both:
- Ownership transfer: C# allocates with `NativeMemory.Alloc`, fills it, and Rust frees it later through a `vrf_free` export. The probe's `vrf_decode_texture` returned a 256x170 RGBA buffer (174,080 bytes) this way. The decoder writes into a `Span<byte>` (`Texture.DecodeTexture(ReadOnlySpan<byte>, Span<byte>, ...)`, `ValveResourceFormat/Resource/ResourceTypes/Texture.cs:955`), so a facade can also decode straight into a Rust-allocated buffer.
- Borrow: a pointer into pinned managed memory (`fixed`, or an array on the pinned heap) that is valid only during a callback or until an explicit release. The probe's VPK lister passed each entry name this way.

**Callbacks, progress, cancellation.** C# calls a Rust `extern "C" fn` through a `delegate* unmanaged<...>` parameter with a `void*` context ([UnmanagedCallersOnlyAttribute example](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute)). The probe's VPK enumeration stopped when the Rust callback returned non-zero, which is the simplest form of cancellation. For long jobs, a facade can export `vrf_job_cancel(handle)` to signal a `CancellationTokenSource`. The library already takes `CancellationToken` in 7 files and `IProgress<T>` in its extract and search paths (`IO/Extract/*Extract.cs`, `IO/PackageSearch.cs`, `IO/Gltf/GltfModelExporter.cs`). Callbacks run on whichever C# thread invoked them. GPUI state is main-thread bound, so the Rust side has to forward results to GPUI's foreground executor.

**Threading and GC.** In the probe, four Rust `std::thread`s each called an export with no setup. Each got its own managed thread id (1-4, main thread 5), so native threads attach on first call. No Microsoft page read for this research states that behaviour. The image contains its own GC. "Two NativeAOT libraries in one process" is not covered in the official docs. A dotnet/runtime maintainer wrote that multiple NativeAOT libraries in one process work, but "each will be a fully independent process with its own GC space" ([dotnet/runtime#49686](https://github.com/dotnet/runtime/issues/49686)). So `ValveResourceFormat` and `Renderer` must be compiled into a single NativeAOT image if they are to share objects. Unloading is not supported: "Unloading Native AOT libraries (via dlclose or FreeLibrary, for example) is not supported" ([libraries](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)).

**Errors.** Every export needs a `try`/`catch` that maps to a return code, plus something like a thread-local last-error string (the probe's `vrf_last_error` returned `System.IO.FileNotFoundException: Could not find file ...`). A managed exception that escapes an export is not covered in the docs. In the probe it printed "Unhandled exception" and aborted the whole process (SIGABRT, exit 134).

**Debugging.** "The managed debugger won't work on them. However, the Native AOT compiler generates fully native executable files that native debuggers can debug ... WinDbg or Visual Studio on Windows and gdb or lldb on Unix-like systems." The compiler emits line numbers, types, locals and parameters. Set a breakpoint on `RhThrowEx` to catch managed throws. "Managed heap analysis isn't currently supported in Native AOT." ([diagnostics](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics)). Symbols go to a separate `.pdb`, `.dbg` or `.dSYM` ([overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)). Because the ABI is the same as option B, development builds can run the same exports under JIT through hostfxr with a managed debugger (see B).

**Size.** Measured on osx-arm64 with `StripSymbols`:
- 5.2 MB with two exports (texture decode, VPK listing). The trimmer keeps only reachable code.
- 29.2 MB with `TrimmerRootAssembly Include="ValveResourceFormat"`, which keeps everything. Rooting the whole assembly also raised IL2026 trim warnings from SharpGLTF. A facade that roots only what it calls avoids them.
- Native deps beside it: `libSkiaSharp.dylib` 16.0 MB, `libspirv-cross.dylib` 7.6 MB.
Generic instantiations over structs are specialized per instantiation and "can have significant impact to the disk size" ([overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)).

**Packaging.**
- Only shared libraries are supported. "Static libraries are not officially supported and may require compiling Native AOT from source" ([libraries](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)). So the Shell ships a `.dll`, `.so` or `.dylib` per RID, not a single static binary.
- "Cross-OS compilation, such as targeting Linux on a Windows host, is not supported." Cross-architecture builds are documented for Windows and Linux ([dotnet/runtime compiling.md](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/compiling.md)). CI therefore needs a runner per OS. A macOS universal build means building osx-x64 and osx-arm64 and merging them.
- Linux: "A Native AOT binary produced on Linux machine is only going to work on same or newer Linux version" ([overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)), so build on the oldest supported glibc. Linker arguments such as rpath go through `<LinkerArg>` ([interop](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)).
- Supported targets: Windows x64/Arm64/x86, Linux x64/Arm64/Arm, macOS x64/Arm64 ([overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)).
- Rust links the dylib with `cargo:rustc-link-lib=dylib=...` plus an rpath (what the probe did), or loads it at runtime with `libloading`.

**Startup.** Probe, first `vrf_decode_texture` call including runtime start: 579 ms on a cold first run, 17 ms warm. Second call: about 60-75 microseconds. These are single runs, not a benchmark.

## B. Hosting CoreCLR in-process through nethost/hostfxr

How it works: the Shell links `nethost`, calls `get_hostfxr_path`, then `hostfxr_initialize_for_runtime_config`, then gets the `hdt_load_assembly_and_get_function_pointer` delegate. Through that delegate it resolves `[UnmanagedCallersOnly]` methods by type and method name, passing `UNMANAGEDCALLERSONLY_METHOD` (`(const char_t*)-1`) as the delegate type ([hosting tutorial](https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting), [native-hosting.md](https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md), [coreclr_delegates.h](https://github.com/dotnet/runtime/blob/main/src/native/corehost/coreclr_delegates.h)). The library is loaded "in isolation (into its own AssemblyLoadContext)" ([native-hosting.md](https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md)).

The Rust crate **netcorehost** (0.22.0, MIT, [docs.rs](https://docs.rs/netcorehost), [repo](https://github.com/OpenByteDev/netcorehost), last push 2026-07-26) wraps this API. It provides `get_function_with_unmanaged_callers_only::<fn(...)>`. The `nethost-download` feature fetches nethost from NuGet. The probe used it to load the *same* `Exports.cs` built as a JIT class library, with `EnableDynamicLoading` and `GenerateRuntimeConfigurationFiles` set.

**Buffers, callbacks, cancellation, errors.** Identical to A, because the exports are identical. An escaping exception is just as fatal, since the native caller has no way to receive it.

**Threading and runtime lifetime.**
- "Only one runtime can be loaded inside a single process" ([tutorial](https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting)).
- "Currently there's no way to unload the managed component or otherwise free the native function pointer" ([native-hosting.md](https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md)).
- The host context handle "is not thread safe - the consumer should only call functions on it from one thread at a time". An initialized but unclosed first context blocks every later `hostfxr_initialize...` call indefinitely (same doc). This concerns setup only. The returned function pointers can be called from any thread, as in A.

**Deployment model (the main cost).**
- "The nethost and hostfxr hosting APIs only support framework-dependent deployments. Self-contained deployments should be treated as stand-alone executables" ([tutorial](https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting)). `hostfxr_initialize_for_runtime_config` states "Self-contained components are not supported" ([hostfxr.h](https://github.com/dotnet/runtime/blob/main/src/native/corehost/hostfxr.h), [native-hosting.md](https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md)).
- There are two ways to ship: (1) require users to install the .NET 11 runtime, or (2) ship a private copy of the shared runtime and point hostfxr at it. hostfxr search order: app folder, then `DOTNET_ROOT`, then the default shared locations ([host-components.md](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-components.md)). nethost's `get_hostfxr_parameters` also takes `dotnet_root` ([native-hosting.md](https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md)).
- A private runtime copy is a shared-framework layout that is serviced separately, not a supported "self-contained component". It is not verified here as a supported configuration.

**Debugging.** The library runs under the normal JIT runtime, so managed debuggers and the full diagnostics stack apply. The hosting samples note that mixed-mode debugging has "a known limitation where no breakpoints will be hit before the runtime starts". The advice is to "start the process from a native-only debugger" ([dotnet/samples hosting](https://github.com/dotnet/samples/tree/main/core/hosting)). The repo's Debug configuration already turns `PublishAot` off (`ValveResourceFormat/ValveResourceFormat.csproj:8`).

**Size.** Probe (Debug JIT build): about 8.3 MB of managed assemblies (`ValveResourceFormat`, `ValvePak`, `ValveKeyValue`, SharpGLTF, SkiaSharp and others). The NuGet `runtimes/` folder for every RID pushed the raw output to 623 MB, so trim it per RID. On top of that comes the shared runtime: `shared/Microsoft.NETCore.App/11.0.0` is 85 MB on disk on osx-arm64. Release would add ReadyToRun (`PublishReadyToRun` is on in `Directory.Build.props`), which makes the assemblies larger and startup faster.

**Startup.** Probe: runtime start plus function-pointer lookup took 30-41 ms. The first decode took 383 ms cold and 37 ms warm. The second call took about 150-520 microseconds (Debug, JIT).

**Related tool.** **DNNE** ([repo](https://github.com/AaronRobinsonMSFT/DNNE)) generates a native shim library with C99 exports for `[UnmanagedCallersOnly]` methods. The shim hosts the runtime through nethost. With `DnneLanguage=rust` it emits a Cargo crate. It describes itself as a prototype. "Failure to load the runtime or find an export results in the native library calling abort()". The generated Rust crate's `build.rs` contains an absolute path. DNNE is the only tool found that generates a C or Rust header from `[UnmanagedCallersOnly]` signatures.

## C. Out-of-process C# backend over IPC

How it works: the C# side is its own executable, ideally NativeAOT. It serves an RPC API over a local socket or pipe, and the Shell is the client. The repo already has a precedent: the GUI's Debug-only MCP server serves JSON-RPC over HTTP to drive the viewer (`GUI/Automation/McpServer.cs`).

**Transports and frameworks.**
- **gRPC**: Unix domain sockets are "the best choice for building cross-platform apps" (Linux, macOS, Windows 10 / Server 2019+). Named pipes work on Windows only, and built-in support needs .NET 8+ ([gRPC interprocess](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess)). "gRPC supports .NET native ahead-of-time (AOT) in .NET 8", with the warning that not all ASP.NET Core features are AOT-compatible ([gRPC Native AOT](https://learn.microsoft.com/en-us/aspnet/core/grpc/native-aot)). Rust side: [tonic](https://docs.rs/tonic) (0.14.9). Rust local sockets: [interprocess](https://docs.rs/interprocess) (2.4.9; Windows named pipes and Unix domain sockets behind one API, tokio support).
- **StreamJsonRpc**: works on any `Stream`. It supports request cancellation and MessagePack ([repo](https://github.com/microsoft/vs-streamjsonrpc)). It "is partially NativeAOT safe". That requires `EnableStreamJsonRpcInterceptors`, source-generated proxies, and `NerdbankMessagePackFormatter` or `SystemTextJsonFormatter` with a `JsonSerializerContext`. `IProgress<T>` value types must be registered ([nativeAOT.md](https://github.com/microsoft/vs-streamjsonrpc/blob/main/docfx/docs/nativeAOT.md)). The Rust side would need its own JSON-RPC client. No first-party crate exists.

**Large buffers.** Copying through the socket is simple, but it costs memory bandwidth on every texture or VPK entry. Shared memory avoids the copy, but on the .NET side named maps are Windows-only. Named `MemoryMappedFile.CreateNew` is "supported on Windows operating systems only" ([CreateNew](https://learn.microsoft.com/en-us/dotnet/api/system.io.memorymappedfiles.memorymappedfile.createnew)). On Linux and macOS, the backend has two choices:
- Map a file both sides can open (`CreateFromFile` on a tmpfs path on Linux, or a temp file on macOS).
- Pass a descriptor over the Unix socket. `UnixDomainSocketEndPoint` exists ([docs](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.unixdomainsocketendpoint)), but none of the .NET API pages read here document `SCM_RIGHTS` descriptor passing, so treat it as P/Invoke work.
On the Rust side, [memmap2](https://docs.rs/memmap2) maps the file.

**Callbacks, progress, cancellation.** These are first-class in both frameworks: server streaming and call cancellation in gRPC, `CancellationToken` and `IProgress<T>` in StreamJsonRpc. Everything is asynchronous, which fits GPUI's async executors.

**Threading, GC, errors.** The processes share nothing. A parser crash or stack overflow on a malformed asset kills the backend, not the Shell, which can restart it. Errors arrive as RPC status or error objects.

**Debugging.** Each process is debugged with its native tooling: a managed debugger (or the NativeAOT native story) for C#, lldb or VS for Rust. This is the simplest of the three, at the cost of cross-process tracing.

**Size and packaging.** One more executable per platform, about the size of A's dylib plus the RPC stack and the same native deps. On macOS a helper executable inside the `.app` has to be signed and notarized like the main binary (not separately sourced here). The backend can also be built for JIT during development without changing the Shell.

**Effort.** An RPC contract (`.proto`, or MessagePack contracts) with generated code on both sides. The API can be richer than a C ABI (records, enums, streams), but a shared-memory side channel is still needed for bulk data.

## The renderer in the same process

The renderer already abstracts its surface. `IGraphicsSurface` has only `Begin()` and `End()` (`Renderer/Renderer/OpenGL/GraphicsContext.cs:9-16`). The GUI implements it over a GLFW context (`GUI/Types/GLViewers/GLFWSurface.cs`) and loads GL entry points with `GLFWBindingsContext` (`GUI/Types/GLViewers/GLBaseControl.cs:1259-1264`). A non-WinForms host can therefore supply its own context.

- **A and B (in-process):** the renderer goes into the same image (A) or the same runtime (B), so it can share `Resource` objects with the library and never marshal meshes. C# creates its own GL context (offscreen GLFW, EGL or WGL) and renders to an FBO. That frame then has to reach a GPUI that does not use GL:
  - CPU readback (`glReadPixels` into a buffer handed to Rust) works everywhere. A 1920x1080 RGBA frame is 8.3 MB, about 0.5 GB/s at 60 fps.
  - GPU interop, same process: on Windows, GL can access D3D11 resources through [WGL_NV_DX_interop2](https://registry.khronos.org/OpenGL/extensions/NV/WGL_NV_DX_interop2.txt). On Linux, GL can import Vulkan-exported memory through [EXT_external_objects_fd](https://registry.khronos.org/OpenGL/extensions/EXT/EXT_external_objects_fd.txt), or a dma-buf through [EGL_EXT_image_dma_buf_import](https://registry.khronos.org/EGL/extensions/EXT/EGL_EXT_image_dma_buf_import.txt).
  - Whether GPUI accepts an external GPU texture on each backend is a GPUI question, outside this ticket.
  - On macOS, GLFW windows and contexts must be created on the main thread, and GPUI owns the main thread. This matters only if the GL 4.6 requirement is ever lowered or met through a translation layer.
- **C (out-of-process):** the renderer lives in the backend, and frames cross processes:
  - Windows: DXGI NT shared handles (`IDXGIResource1::CreateSharedHandle`, "use the returned handle with multiple Direct3D devices", `DuplicateHandle`-able; [docs](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiresource1-createsharedhandle)).
  - Linux: memory or dma-buf file descriptors, which need `SCM_RIGHTS` passing.
  - Input, camera and resize events become RPC messages, which adds latency to every interaction.
  - Embedding the backend's native window as a child of a GPUI window is not supported by GPUI, as far as was found here (not verified).

## Probe (measured on macOS arm64)

The probe is throwaway code in the session scratchpad, not in the repo.
- `VrfProbe.csproj`: class library, `PublishAot`, `ProjectReference` to `ValveResourceFormat`, plus `ValvePak`.
- Exports: `vrf_decode_texture`, `vrf_vpk_list` (callback with cancel), `vrf_last_error`, `vrf_free`, `vrf_gc_info`, `vrf_throw_uncaught`, `vrf_thread_id`.
- Rust callers:
  - `vrfprobe` links `libVrfProbe.dylib` directly.
  - `vrfhost` uses `netcorehost` 0.22 against the JIT build of the same C# file.
- Inputs: `Tests/Files/subscriptions_gamerpvp_inhouse_png.vtex_c` and `Tests/Files/dota_riverflow_fx.vpk`.

Results:
- Texture decode worked through both hosts: 256x170, 174,080 bytes.
- VPK listing stopped early when the callback returned non-zero.
- A bad path came back as rc -1 with the managed exception text.
- Four Rust threads attached automatically.
- An uncaught exception aborted the process (exit 134).
- Sizes and timings are as quoted above.
- Build notes: the NativeAOT publish logged an `xcodebuild requires Xcode` message with only the Command Line Tools installed, and still produced the dylib and `.dSYM`. The first publish, including restore, took about 70 s.

## Open points not settled by sources

- Thread attach for native threads calling `[UnmanagedCallersOnly]` is observed, not documented.
- Whether an app-local copy of the shared framework, found through `dotnet_root`, is a supported configuration for B.
- Whether .NET exposes `SCM_RIGHTS` without P/Invoke.
- Whether GPUI can composite an external D3D11, Vulkan or Metal texture, which decides the renderer path for every option.
- Windows and Linux numbers were not measured.
