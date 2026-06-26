# Fast.exe

Fast.exe is a small Windows game-speed controller inspired by Cheat Engine's time acceleration feature.

It is built for the specific workflow where you want to configure a game once, then stop thinking about it. Add or pick a process, bind one or more speed hotkeys, and Fast will keep watching for that game in the background.

## What Makes It Different

- Inspired by Cheat Engine's time acceleration feature.
- Remembers every process it has ever seen.
- Can boot automatically with Windows.
- Attach it to a game once, then never have to think about attaching it to that game ever again.
- Bind many different speeds.
- Supports hold and toggle modes.
- Supports keyboard hotkeys.
- Supports Xbox controllers through XInput.
- Supports DualSense/PS5 controllers through Raw Input, with DirectInput fallback.
- Works with both 32-bit and 64-bit apps from one unified `Fast.exe`.

## How It Works

Fast has two pieces:

- A Windows Forms host app, `Fast.exe`, that watches for configured processes and manages hotkeys/controller bindings.
- A native hook DLL that is injected into the target process and scales common game timing APIs.

The host ships with both hook bitnesses:

- `FastHook.dll` for 64-bit targets.
- `FastHook32.dll` for 32-bit targets.

The main app is 64-bit. When it sees a 32-bit target, it quietly launches `FastInjector32.exe`, a tiny helper used only to inject the 32-bit hook into the 32-bit process. You still run just one user-facing program: `Fast.exe`.

## Features

### Persistent Process Watching

Fast stores watched processes in `%APPDATA%\Fast\settings.json`. Once a process is added, Fast keeps scanning for it. If the game closes and later starts again, Fast can attach again automatically.

### Startup Support

Enable "Launch on startup" inside the app to register Fast under the current user's Windows startup entries.

### Multiple Speed Slots

Fast exposes ten speed slots. Each slot can have:

- A keyboard or controller binding.
- A speed multiplier.
- Hold mode or toggle mode.

Examples:

- Hold `LT` for `2.0x`.
- Tap a key to toggle `4.0x`.
- Bind separate buttons for several different speeds.

### Controller Support

Fast supports:

- Xbox-compatible controllers via XInput.
- DualSense/PS5 controllers via Windows Raw Input.
- DirectInput controllers as a fallback path.

The controller support is global, so the game does not need to have focus for Fast to detect the binding.

## Download

Download the latest release from the GitHub Releases page and run:

```text
Fast.exe
```

Keep all files from the release zip together in the same folder. The helper DLLs and dependency DLLs are required.

## Building

Requirements:

- Windows
- .NET 6 SDK or newer SDK capable of targeting `net6.0-windows`
- Visual Studio Build Tools with the "Desktop development with C++" workload

Build:

```bat
build.bat
```

The unified build is written to:

```text
bin\Fast.exe
```

The build script also creates:

```text
bin\FastHook.dll
bin\FastHook32.dll
bin\FastInjector32.exe
```

Close attached games before rebuilding. Windows locks hook DLLs while they are loaded inside a target process.

## Notes

Fast changes perceived time for a target process by hooking timing APIs such as `GetTickCount`, `GetTickCount64`, `QueryPerformanceCounter`, and `timeGetTime`.

Some anti-cheat systems may object to DLL injection or timing hooks. Use Fast only with games and applications where you are allowed to modify local runtime behavior.

## Third-Party Code

Fast includes MinHook in `src/Hook/minhook`. MinHook is distributed under its own BSD-style license; see `src/Hook/minhook/LICENSE.txt`.

Fast also uses Vortice.DirectInput and its dependencies for DirectInput support.
