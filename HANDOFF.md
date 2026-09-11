# Where things stand

Snapshot at the end of the first build session, 11 September 2026. Delete this
file once it stops being useful.

## State

| | |
| --- | --- |
| Version | **1.1.1**, installed and running on this machine |
| `main` | `4805aaa` — PR #1 (the initial build) is merged |
| Working branch | `fix/hide-open-action-for-text-clips` — **4 commits ahead of `main`, pushed, fast-forward, not yet merged** |
| Tests | 37 assertions, 4 suites, all passing |
| Installer | `dist\Stash-1.1.1.msi` (not in git — `dist/` is ignored) |

The branch name is far too narrow for what it carries. Retitle the PR to
something like "Usability fixes and a help window" before merging:

```
29e3a17  Fix the crash and flashing when dragging across monitors
4c71c42  Fix three bugs in drag-to-dock
db0e719  Add a help window, stop the search box resizing, simplify the icon
fcea4a0  Hide the Open action on clips that have nothing to open
```

## The one thing that needs a human before merging

**Drag-to-dock is unverified.** Two of the four commits above are drag fixes and
neither has automated coverage. The sequence of reports during the session was:

1. Dragging behaved worse than `Ctrl`+arrow → three bugs found and fixed
   (the monitor clip slicing the panel mid-drag, the snap monitor coming from the
   window rather than the panel, and a drop re-docking on the original monitor).
2. Dragging bottom-dock to the right side then flashed repeatedly and crashed →
   three more causes found and fixed (`DpiChanged` fighting the drag, WPF item
   generator re-entrancy via `ScrollIntoView`, and the placement verifier able to
   ping-pong).

So: **grab the header and drag it to each edge, including across the 200% display,
before merging.** If it still misbehaves, `%LOCALAPPDATA%\Stash\stash.log` is the
place to look — the crash was diagnosed entirely from it, and placement
corrections are logged there by design.

A cursor-driven drag harness was written twice and discarded both times. WPF's
`DragMove` is a modal loop, the harness has to compute its own aiming from window
geometry, and its failures could not be told apart from real bugs. It also drove
the physical mouse, which is intrusive. Not worth resurrecting without a better
idea.

## Environment notes

- **.NET 10 SDK is bootstrapped into `%LOCALAPPDATA%\dotnet`**, not installed
  machine-wide and not on `PATH`. `build.ps1` and `tests\Run-Tests.ps1` find it by
  probing `--list-sdks`, because the system `dotnet` on this box is a runtime with
  no SDK.
- **WiX is pinned to 5.0.2.** Versions 6 and 7 require accepting the Open Source
  Maintenance Fee, a paid commercial licence. Do not bump it without a
  procurement decision.
- This machine has **three monitors at mixed DPI** (2880x1800 @ 200% plus two
  1920x1080 @ 100%). Most of the hard bugs in this project were DPI-related, and
  that layout is why they were caught. Be suspicious of anything that looks fine
  on one display.

## Careful with the tests

`tests\Run-Tests.ps1` **resets the live clipboard history and settings** so card
positions are deterministic. That is fine on a dev profile and destructive now
that the app is in real use — it wiped a real history once during this session.
Either pass `-KeepProfile` (some suites will fail, they depend on known card
positions) or back up `%LOCALAPPDATA%\Stash\{history.json,settings.json}` and the
`images` folder first, and restore afterwards.

Current live profile: 7 clips, docked bottom, height 260, width 340.

## Loose ends

- **The MSI is unsigned.** SmartScreen may warn until it earns reputation. Needs a
  code-signing certificate to fix properly.
- **`dist/` is gitignored**, so the installer is only on this machine. To share it,
  attach the MSI to a GitHub Release rather than committing the binary. Bump
  `<Version>` in `src\Stash\Stash.csproj` first — the MSI upgrade path keys off it.
- **`%LOCALAPPDATA%\Shelf`** is leftover test data from before the app was renamed
  from Shelf to Stash. Safe to delete.
- The installer's install / upgrade / uninstall cycle was verified by hand but is
  not automated.
- No rich-text or HTML preservation; search is substring rather than fuzzy; no
  sync. All deliberate, all listed in the README.

## Worth deciding after living with it

The defaults are educated guesses, not researched: `Ctrl+Alt+V`, 260px panel
height, 340px width, 400 clips, 30-day retention, `Alt`+`1`-`9` to assign quick
slots. The height already moved once (300 → 260) on the strength of a day's use.
Expect one or two more to want adjusting.
