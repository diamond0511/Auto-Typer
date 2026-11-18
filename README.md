# Auto Typer

A background Windows utility that types your clipboard as **real keystrokes**.

It exists for the places where `Ctrl+V` does not work: remote desktop and Citrix sessions,
virtual machine consoles, KVM/IPMI consoles, installer and BIOS-style fields, and web forms
that deliberately block paste. Those fields accept keyboard input but not clipboard input, so
the only way in is to actually type, which is tedious for a 60-character licence key and
error-prone for a config blob.

Auto Typer runs with **no window and no console**. It lives in the notification area and is
driven entirely by global hotkeys.

---

## Download

**[AutoTyper.exe](https://github.com/diamond0511/Auto-Typer/releases/latest/download/AutoTyper.exe)**
from the latest release. One file, no installer, no DLLs, nothing written to the registry. Put it
wherever you like, including a USB stick: it keeps its settings in `autotyper.ini` beside itself.

Windows needs .NET Framework 4.x, which ships with Windows 10 and 11.

SmartScreen will warn on first run, because the exe is not code signed. Choose More info, then Run
anyway, or build it yourself from source with the step below and trust nothing but your own
compiler.

---

## Quick start

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run
```

That produces `dist\AutoTyper.exe` and starts it. On first run it shows a short dialog listing
the hotkeys, then disappears into the tray.

To use it:

1. Copy some text (`Ctrl+C`).
2. Press **`Ctrl+Alt+V`**.
3. Click the field you want filled. You have 1.2 seconds (configurable).
4. The text types itself.

Press `Ctrl+Alt+V` again, or `Ctrl+Alt+X`, to stop early. **Clicking the mouse also stops it**, so
taking the machine back does not mean reaching for a hotkey. Moving the mouse is fine and does
not interrupt anything.

---

## Hotkeys

| Default | Action |
| --- | --- |
| `Ctrl+Alt+V` | Type the clipboard, or stop if it is already typing |
| `Ctrl+Alt+X` | Stop typing |
| `Ctrl+Alt+S` | Open settings |
| `Ctrl+Alt+Q` | Quit |

All four are configurable. Click a hotkey box in Settings and press the combination you want;
`Backspace` clears it. A modifier is required, because binding a bare key would swallow it
system-wide.

**Shift cannot be the only modifier.** A registered hotkey responds to injected keystrokes exactly
as it does to a real keyboard, and Auto Typer types capitals by genuinely holding `Shift`. So
`Shift+T` as the stop hotkey means the run stops itself at the first capital T in your text,
`Shift+Q` means it quits itself, and `Shift+V` means it restarts. Anything containing `Ctrl`,
`Alt` or `Win` is safe, because neither is ever held while typing: `Ctrl+Shift+T` is fine.

This was a real failure, not a hypothetical one. A 446-character message stopped at character 411
with the message "Stopped after 411 of 446 characters", which is the wording for a deliberate
stop. Character 411 was the `T` of "That is all to get started": the first capital T in the whole
text, and the abort hotkey was set to `Shift+T`. The settings dialog now refuses such a
combination, and an existing settings file containing one is repaired on startup with a dialog
explaining what changed.

---

## Settings

Open with `Ctrl+Alt+S`, or double-click the tray icon.

**Speed**

- *Wait before typing starts*: how long you get to click into the target field.
- *Delay between keys*: with the measured rhythm on, the middle of this range is the typical gap
  between two keys and the recorded distribution is scaled to match it. The default, 80 to
  120 ms, is the speed the recordings were made at. With the measured rhythm off, a flat random
  delay is drawn from the range instead, because a fixed interval is both unnatural and, in
  practice, more likely to trip the input-coalescing in remote-session clients. Set both ends to
  `0` for maximum speed. Raise them if characters go missing: slow web fields and high-latency
  RDP sessions drop input that arrives faster than they can process it.

**Behaviour**

- *Use the measured typing rhythm*: paces every keystroke from recordings of real typists rather
  than drawing a flat random delay. See below for what that changes.
- *Make and correct occasional mistakes*: off by default. Types a wrong key about once every 46
  characters, carries on for a character or two, then backspaces over it.
- *Press Enter for line breaks*: sends a real `Enter` rather than a literal newline character,
  which is what multi-line fields expect. Turn it off to skip line breaks entirely when filling
  a single-line field.
- *Press Tab for tab characters*: sends a real `Tab`, which moves between form fields.
- *Ignore a line break at the very end*: on by default. Copied text very often ends in a
  newline, and that trailing `Enter` would submit the form you just filled in.
- *Stop if the target window loses focus*: on by default. If you click away mid-run, the rest
  of the text is not typed into whatever window you landed on.
- *Stop if the mouse is clicked*: on by default. The machine is not usable for anything else
  while a run is in progress, so a click is taken as a request to have it back. Moving the mouse
  does not count, only pressing one of its buttons.
- *Show the tray icon* / *Show notifications*: turn both off and Auto Typer runs with no
  visible trace. The settings dialog will not let you hide the tray icon unless the settings and
  quit hotkeys are set, so you always have a way back in.

Settings are written to `autotyper.ini` next to the executable when that folder is writable
(so the whole thing is portable: drop it on a USB stick), otherwise to
`%APPDATA%\AutoTyper\autotyper.ini`. The file is plain text and can be edited by hand.

---

## How it works

Characters are injected with `SendInput` using `KEYEVENTF_UNICODE`, which delivers the character
itself rather than a scan code. That means the output does not depend on the active keyboard
layout, so text types correctly whether the target machine is set to US, UK, German or anything
else. Emoji and other astral-plane characters are sent as surrogate pairs.

Three details that are easy to get wrong, and are handled:

- **Modifier release.** When the hotkey fires, `Ctrl` and `Alt` are still physically held. Typing
  immediately would produce shortcuts instead of text, so the engine waits for the modifiers to
  come up and synthesises a release for any that are stuck.
- **Target capture.** The target window is recorded *after* the start delay, not when the hotkey
  fires, so clicking into the field does not count as the focus change that stops the run.
- **Pacing.** Windows ticks its timer every 15.6 ms by default, so `Thread.Sleep(1)` can sleep
  for fifteen. That error only ever adds, which silently inflates every gap. The engine raises
  the timer resolution for the length of a run and finishes short waits on a spin, so the
  requested speed is the actual speed. Measured, that pulled the typical gap from 128 ms back to
  within 2.5% of what was asked for.

Typing runs on a background thread and checks for an abort between every keystroke, so stopping
is immediate rather than at the end of a buffer.

### Stopping on a mouse click

A run cannot share the machine: every keystroke goes to whatever window is in front, so you have
to stay out of the way until it finishes. Rather than make that a rule to remember, a click ends
the run.

The buttons are read by polling, not by a hook, because a low-level mouse hook would sit in the
same input path the engine is injecting into and add latency to every keystroke. Inside the pacing
loop all five buttons are checked every 5 ms, which is cheap and bounds the reaction at roughly
one keystroke.

Two details make it behave rather than merely work:

- **The latch is read once after the start delay**, like the target window is captured there.
  Clicking into the field is the expected way to start, so a click made *before* the run,
  including the one that may have started it from the tray menu, must not count against it.
- **Clicks are caught even when they are over before the next poll.** `GetAsyncKeyState` keeps a
  "pressed since you last asked" bit as well as a "down right now" bit, and both are tested.

**Movement is deliberately not watched.** An earlier version stopped on that too, and it was
wrong twice over: an optical mouse drifts on its own and a remote session re-syncs the cursor, so
runs died for no reason, and nudging the pointer is not a request to stop anything. Only a button
press is unambiguous.

Verified by injecting each kind of activity part way through a 205-character run:

| What happened | Characters typed afterwards |
| --- | --- |
| Clicked | 0 |
| Cursor moved 86 px | all 180 remaining, ran to completion |
| Nothing | all 180 remaining, ran to completion |

---

## Building

Requires nothing but a stock Windows install. `build.ps1` compiles with the C# compiler that
ships as part of the .NET Framework (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`),
so there is no SDK, NuGet restore or project file involved. The application icon is generated by
the build script.

```powershell
.\build.ps1           # build to dist\AutoTyper.exe
.\build.ps1 -Run      # build and launch
.\build.ps1 -Debug    # unoptimised build with symbols
```

The result is a single ~140 KB executable with no dependencies beyond the .NET Framework 4.x
runtime, which is a built-in Windows component. Copy `AutoTyper.exe` anywhere and run it.

---

## How the keystrokes are produced

Each character is sent as **the real key that types it** on the target window's own keyboard
layout, with `Shift` genuinely pressed and released around it and the key held down for 30–75 ms
(the dwell time of actual typing). Shift stays held across a run of capitals, the way a person
holds it for `ZIP` rather than tapping it three times.

This matters for web forms. A Unicode packet carries no physical key, so a page sees
`event.code` empty and `event.shiftKey` false on *every* keystroke, which breaks input masks,
per-keystroke validation and autocomplete. Measured on the same text in Chrome:

| | `event.code` empty | `shiftKey` true | Fallbacks |
| --- | --- | --- | --- |
| Real keys (default) | 3 of 79 | 21 | 1 |
| Unicode packets | 68 of 68 | 0 | all |

Characters the layout cannot physically produce (emoji, an em-dash on a US keyboard, foreign
scripts) still go out as packets, which is the only way to send them at all. `AltGr`
combinations also fall back, because synthesising `Ctrl+Alt` risks firing a shortcut in the
target instead of typing.

Turn **Type with real keys** off only if a particular program refuses them; it reverts to the
layout-independent packet method.

**Use the measured typing rhythm** paces the keystrokes from `src/RhythmProfile.cs`, which holds
measurements taken from real typists rather than invented constants. Keys are held about 120 ms
and overlap the next one about half the time, the beat falls on the first letter after a space
rather than on the space itself, capitals and punctuation stretch the gap, and Shift is released
just before the letter it capitalises.

### Measuring a real rhythm

`tools/rhythm-tracker.html` records how actual people type, so the rhythm model can be fitted to
measurement rather than invented. It is a single self-contained file with no install and no
network access, and it works offline. Open it, type the seven sentences, click Download, and it
saves one JSON file.

The prompts are plain-text technical explanation, because that is what this tool is used to type.
Continuous prose is also what gives usable digraph timing; contrived strings of punctuation
measure nothing you would ever send.

It captures, per keystroke: the physical key, modifier state, auto-repeat flag, key-down and
key-up timestamps to 0.01 ms, the caret position and field length at each event, and every
correction, distinguishing `Backspace`, `Delete`, `Ctrl+Backspace` and `Ctrl+Delete`. Mistakes
are the point, so participants are told not to try for a clean run. Pasting is blocked and
recorded, since it would destroy the measurement.

From that the model can be fitted for real: hold time and inter-key gap distributions rather
than guessed constants, how long Shift leads the letter it capitalises, which digraphs run fast,
where pauses actually fall, and how often someone mistypes and how they back out of it.

Seven sentences is roughly 800 keystrokes per person: enough for solid distributions, still thin
for a full per-digraph table. Several participants, or longer prompts, improve that.

`tools/analyze-rhythm.ps1` pools every `rhythm-*.json` beside it and prints the measured
distributions, then writes `rhythm-profile.json` for the engine to be driven from.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\analyze-rhythm.ps1
.\tools\analyze-rhythm.ps1 -Digraphs      # also dump the full per-pair table
.\tools\analyze-rhythm.ps1 -NoFilter      # keep the events it would normally discard
```

### What the first round measured, and what the engine now does

Nine sessions, three people, 6,602 characters, 53 to 74 wpm. The engine's original constants were
invented, and most of them were wrong. `src/RhythmProfile.cs` now carries the measurements
instead, as percentile tables that are sampled and interpolated, so the shape of the distribution
is reproduced rather than approximated by a formula.

| | Was assumed | Measured (median) |
| --- | --- | --- |
| Key held down | 30-75 ms | **120 ms** |
| Gap between keys | 70-110 ms | 112 ms (100 ms mid-word) |
| Gap before a space | 1.30x | **0.98x**, no slower at all |
| Gap on the first letter after a space | not modelled | **1.60x** |
| Gap before a capital | 1.20x | **2.08x** |
| Gap after `. , ; : ! ?` | 1.55x | 2.23x |
| Entering a digit | 1.20x | **5.9x** (only 24 samples) |
| Same hand twice in a row | not modelled | 1.11x, against 0.96x alternating |

Two structural findings mattered more than any constant, and both changed how the engine works:

- **Half of all keystrokes overlap.** The next key goes down before the previous one comes up in
  49.9% of cases. The engine used to press and fully release each key in turn, which forced a
  short dwell to reach a plausible speed. Real typing has a long dwell *and* a short gap, which
  is only possible if the keys overlap. Releases are now scheduled on a clock rather than
  performed in line, and the engine reproduces the 49% rollover.
- **Shift comes up before the letter does**, by about 40 ms. The engine used to release the
  letter first. Shift is tapped per capital rather than held, except across acronyms like `HTTPS`,
  where it is held, which the engine already did.

Mistakes ran at one per 46 characters, 93% of them corrected. Correction is almost entirely
`Backspace` (324 uses), with `Ctrl+Backspace` occasional (21) and `Delete` and `Ctrl+Delete`
never used once. A mistake is noticed about two characters later, after roughly 0.9 s, and
removed in bursts of one to five backspaces about 144 ms apart. Nobody touched Caps Lock.

**Make and correct occasional mistakes** reproduces that, and is off by default: it puts a wrong
character into the target field for a second or two, which is not always acceptable. It deviates
from the measurement in one way on purpose. Real typists left 7% of their mistakes standing; this
corrects every one, and a correction finishes even if you press the stop hotkey part way through,
so a run can never end with a typo in the field. Which wrong key gets hit is a neighbour on the
same keyboard row, which is the conventional model rather than something measured.

### Checking the engine against the recordings

Measured on the engine typing one of the recorded sentences, against the five people who typed
that same sentence:

| | People | Auto Typer |
| --- | --- | --- |
| Key held down | 111.6 ms | 125.4 ms |
| Gap between keys | 111.8 ms | 122.7 ms |
| Keys overlapping | 48.6% | 48.6% |

Replaying the model alone over the seven recorded sentences gives a gap median of 112.8 ms and a
dwell median of 119.7 ms, against 111.8 and 111.6 from the people: the model is right. The live
figures above sit a little higher because the recorder's own keyboard hook is in the injection
path. Timed without it, the engine runs within 2.5% of its model.

One honest gap remains. The profile keeps the middle 90% of the measured distribution, so the
long pauses for thought are trimmed. The engine therefore types at the fast end of what was
recorded, nearer 95 wpm than the 53 to 74 wpm the sessions averaged over their whole length.

### A warning about where you record

The tracker timestamps events as the browser hands them over. If the page is busy, or the
keystrokes arrive over a remote desktop connection, several events are delivered at once and the
recording measures that delivery rather than the typing. In the first round this ruined 9 to 20%
of the events in six of the nine sessions, all of them typed through a remote session, while the
three local ones were clean.

Both halves are now fixed: the tracker records `event.timeStamp`, which is set upstream of the
browser's input queue, and it repaints on an animation frame instead of on every keystroke. The
analyzer drops any interval it can prove is impossible and prints the share it discarded, so a
contaminated recording is visible rather than silently averaged in. **Record on the machine whose
keyboard is being used**, never through Jump Desktop, RDP or Citrix.

### What this does *not* change

Windows stamps `LLKHF_INJECTED` on every event `SendInput` produces, and no user-mode program
can clear it. Raw Input and DirectInput read below the layer this injects into, so games and
anything using them never see these keystrokes at all. The keystrokes are mechanically identical
to real ones; their origin is still visible to anything that checks.

---

## Typing accuracy and slow applications

Characters that fall back to Unicode packets (`VK_PACKET`) are injected directly.
Most applications read each packet
as it arrives. Some consume them on a separate thread instead: anything built on WinUI or the
Text Services Framework, with **Windows 11 Notepad the worst example found**. If the next packet
lands before the previous one is read, they translate the stale value. The symptom is
characters *substituted* rather than dropped: `City:` arrives as `iity:`, `ZIP` as `PPP`.

This was measured against Store Notepad, typing the same 256-character block repeatedly with a
fresh Notepad process per trial:

| Delay between keys | Exact matches |
| --- | --- |
| 15–55 ms | **0 of 9** |
| 60 ms | 1 of 1 |
| 80 ms | 4 of 4 |
| 70–110 ms (the default) | 4 of 5 |
| 120 ms | 1 of 1 |

The defaults sit above that cliff deliberately. Dropping the delay below **60 ms** is what causes
the corruption, and the settings dialog warns when you do.

Two things worth knowing:

- **No delay makes Notepad perfectly reliable.** Even at the default it dropped a single
  character in one run out of five. If you are typing something where a silent error matters
  (a licence key, a password, a config value), read it back before you rely on it.
- **This is target-specific.** Ordinary Win32 and WinForms fields took the same text at 20–55 ms
  with zero errors across every run. If your target behaves, you can safely speed Auto Typer up;
  if it garbles text, raise the delay before assuming anything else is wrong.

Sending the whole string in one `SendInput` call was also tested and is *worse*: Notepad
discards a large batch outright.

Real virtual keys were also tried as a fix and first looked worse, but that test was run at 5 and
15 ms, both well inside the failure band, so it measured the delay and not the method. Retested
at 80 ms they were fine. The corruption is a pacing problem, not a packet problem, which is why
real keys are now the default for their own reasons.

---

## Limitations

- **Elevated windows.** Windows blocks synthetic input sent from a normal process to an elevated
  one (User Interface Privilege Isolation). To type into an app running as administrator, run
  Auto Typer as administrator too. It reports this rather than silently typing nothing.
- **Text only.** Non-text clipboard content is ignored.
- **One instance.** A second launch tells you it is already running and exits.
- **Secure Desktop.** Nothing can type into the UAC prompt or the Ctrl+Alt+Del screen. That is by
  design in Windows, not a gap here.

---

## Layout

```
build.ps1              build + icon generation
src/
  Program.cs           entry point, single-instance guard, crash reporting
  TrayApp.cs           tray icon, menu, hotkey routing, clipboard handling
  TypingEngine.cs      the keystroke injection and pacing
  MessageWindow.cs     invisible window owning the hotkeys
  RhythmProfile.cs     the measured typing rhythm, as percentile tables
  HotkeyBox.cs         control that captures a key combination
  SettingsForm.cs      settings dialog
  AppSettings.cs       load/save
  Hotkey.cs            hotkey model and parsing
  NativeMethods.cs     Win32 interop
  AssemblyInfo.cs      version and product name stamped into the exe
  app.manifest         DPI awareness, asInvoker
tools/
  rhythm-tracker.html  records how real people type, for fitting the rhythm model
  analyze-rhythm.ps1   pools the recordings into measured distributions
  rhythm-profile.json  the fitted profile, written by the analyzer
dist/
  AutoTyper.exe        build output, not tracked in git
```

---

## Licence

MIT. See [LICENSE](LICENSE).
