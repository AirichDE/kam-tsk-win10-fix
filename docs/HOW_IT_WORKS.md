# How it works (reverse engineering notes)

Analysed with Ghidra 12.2 on the official 1.32 EXEs (`KAM_1024.EXE`, `KAM_800.EXE`); addresses below are VAs of the German 1.32 EXE. The patcher does **not** use fixed addresses: it finds every patch point by byte signature (including the English and Polish builds, whose code is at different addresses) and refuses to touch EXEs it does not recognise.

## Why the game refuses to start

At start `FUN_004011b3` builds a status value and compares it with an expected value; if it differs `FUN_004035df` shows the dialog *"Please insert the Knights and Merchants CD…"* (dialog resource 0x67, language chosen by Windows) and the game quits. The value depends on:

1. **Drive search – `FUN_004039fb`.** Looks through drive letters C…Z for a CD-ROM (`GetDriveType == 5`) that contains `AUTORUN.EXE` (name XOR-obfuscated with `0xEF`) and whose volume label is `KAM`. The result is also used as the path prefix for the intro videos (`%sVideo\…avi`).
2. **Copy-protection size check – `FUN_0040374c`.** Sums file sizes on the CD (`KM\_setup.1` + the video folder) and flips flag bits of the status value depending on the total.
3. **Audio-CD check – `FUN_00403958`.** Asks MCI (`cdaudio`) for the total length of the CD's audio tracks and flips `0x1000` only if the CD is long enough. An ISO image has no audio tracks, so this fails.
4. **Self checksum – `FUN_00403693`.** A checksum over the code `0x403484…0x403c48` (and a few dwords at `0x4011b3`) is compared with a constant (`_DAT_004890cc`), so *any* change in that range makes the game quit.

## The patches (all by signature)

| # | Where | Change |
|---|---|---|
| A | `FUN_004039fb` (drive search) | Function replaced by: copy the string `CD\` into the caller's path buffer and return 1. The CD content therefore lives in `<game folder>\CD\`. |
| B | `FUN_00403c48` | `jz` → `jmp` so the "insert CD" dialog is never shown. |
| D | `FUN_00403958` (audio-CD length) | `jbe` → `nop nop`: the flag is always set. |
| C | checksum constant | The patcher re-implements the game's checksum, finds the stored constant (unique 4-byte value) and writes the new sum. |
| E | import table | The import name `WINMM.dll` is changed to `KAMCD.dll` (same length) – see below. Import names are outside the checksummed range. |

## Music without the CD – `KAMCD.dll`

The game plays music only through MCI: `OPEN "cdaudio"`, `SET time format TMSF`, `STATUS number of tracks / track length`, `PLAY from=N to=N+1 NOTIFY` (the window then receives `MM_MCINOTIFY` and starts the next track), `STOP`, `CLOSE`. The EXE imports only 10 functions from `winmm.dll` (`mciSendCommandA`, `mciGetErrorStringA`, `mciSendStringA`, 7× `mixer*`).

`KAMCD.dll` (own C code, `src/winmm_cd/winmm_cd.c`, built with TinyCC) exports exactly these 10 functions. It emulates the `cdaudio` device with `Music\TrackNN.wav` (or `.mp3`) and forwards everything else (the MIDI sequencer part, the mixer) to the real system `winmm.dll`.

* Playback uses the real MCI (`waveaudio`). MCI devices belong to the thread that opened them, so all real MCI calls run on one worker thread; the exported functions only post requests to it.
* End of track → `MM_MCINOTIFY(SUCCESSFUL)`, a new `PLAY` while one is running → `SUPERSEDED`, `STOP`/`CLOSE` → `ABORTED` – the same notifications the real `cdaudio` driver sends.
* The DLL is **not** named `winmm.dll`: other Windows modules (e.g. `mciwave.dll`) import `winmm.dll` by name and would otherwise bind to the 10-function stub and fail to load.
* tcc exports stdcall functions decorated (`_name@N`); `src/winmm_cd/build.ps1` rewrites the names in the export table.

The audio tracks are read with raw CDDA reads (`IOCTL_CDROM_READ_TOC`, `IOCTL_CDROM_RAW_READ`) and written as 44.1 kHz / 16 bit / stereo WAV.

## Tests

* `--selftest <exe>` applies all patches in memory and checks that a second run reports "already patched" and that the checksum matches (run on both EXEs of the German, English and Polish 1.32 patches).
* `src/winmm_cd/McTest.cs` replays the game's MCI call sequence against the DLL and logs the window notifications.
* `src/winmm_cd/PeakMeter.cs` samples the output peak of the default audio device to confirm that sound is really played while the game runs.
