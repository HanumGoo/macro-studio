# Jitbit Macro Recorder inspection

## Scope and method

The installed target was inspected with REA against `C:\Program Files (x86)\MacroRecorder\MacroRecorder.exe` and its adjacent files. Analysis was static: REA parsed PE/CLI metadata, method bodies, user strings, and P/Invoke declarations without loading the target assembly in a CLR or executing it. A live UI inspection was attempted against the user-opened window, but accessibility reported that it remained at higher Windows integrity than the computer-use helper, even after the user said they would relaunch without elevation. No menu or settings dialog was successfully inspected. The executable's SHA-256 was `9e2b3c898821eabc315576f6f274dbdd4e055c60ef3d2325a96caf702fd86ece`.

## Identity and runtime

- `MacroRecorder.exe` is a managed PE/CLI executable, x86, .NET Framework 4.8, assembly version 5.9.0.0.
- The CLI flags indicate IL only and 32-bit required. Metadata coverage was complete for the inspected file: 198 types, 1,658 method definitions, 1,373 fields, and 85 P/Invoke imports.
- The install directory also contains `MacroLauncher.exe`, `Microsoft.Win32.TaskScheduler.dll`, Mono.Cecil assemblies, and `extp.etl`.

## Observed application features

The main form type is `MacroRecorder.MainForm`. Its method names and embedded UI strings identify recording, replay, selection playback, step-by-step playback, cursor insertion/append recording, hotkey registration, scheduling, and a command editor/list. Named commands include mouse and keyboard commands, delays, labels and goto, repeat/end-repeat/exit-loop, pause/break, message boxes, application/file/website commands, window wait/switch/close, clipboard actions and conditions, file/process/window conditions, pixel and image matching, and IF/ELSE/ENDIF. Static inspection of command editor forms further exposed repeat count sources (numeric, clipboard, prompt, or random upper limit); mouse absolute/current/relative positioning and randomized offsets; SMART-Rec/window-relative recording and button inversion; image search region/window/pixel-scan options; pixel-found rectangle/window/not-found options; iteration equals/modulo conditions; wait-for-window/process appear/disappear and timeouts; playback flags including zero=infinite repeat, ignore delays, cursor restore, pause on mouse movement, and block input; scheduler task creation; and an embedded C# command requiring `Program.Main()`. These are UI/code-path indicators, not proof each feature was dynamically exercised.

Static P/Invoke declarations include `SetWindowsHookEx`, `CallNextHookEx`, and `UnhookWindowsHookEx` from `user32.dll`; also `SendInput`, `mouse_event`, `keybd_event`, `BlockInput`, `RegisterHotKey`, and `UnregisterHotKey`. The inspection confirms these imports exist. It does not establish the hook IDs, exact callback branches, thread/message-loop setup, recording event schema, or how playback selects among input APIs.

The UI exposes `.mcr` as its native macro-file extension and includes a command to edit a macro in Notepad. A settings string includes abort/pause hotkey values and playback flags. Those observations do not disclose the complete `.mcr` grammar or establish that the format is plain text. No `.mcr` sample was available in the inspected install directory, and the exact action serialization, coordinate scaling, repeat timing, and delay normalization remain unknown.

## Reconstruction boundary

This project implements its own JSON format and engine. It is behaviorally inspired by the visible feature set and native API imports; it does not read/write proprietary `.mcr` files and should not be described as binary-compatible. Current implementation includes global low-level hooks, SendInput playback, conditional blocks, loop flow, sampled exact-color image scans, process-memory comparisons, file/window/process/clipboard/message checks, and common automation actions. OpenCV matching, C# action execution, scheduler integration, system-wide hotkey management, input blocking, cursor restore, original playback/recording setting parity, and several conditional/editor options remain gaps.

## Remaining evidence gaps

No controlled UI replay or runtime tracing was performed, so exact hook lifecycle, event serialization, playback speed behavior, error recovery, image matching tolerance, and edge-case command semantics need runtime observations before claiming fidelity. REA static results are bound to the SHA-256 above; metadata tokens are build-local.
