# Knights and Merchants TSK – Win10/11 Fix

**By Daniel Airich** · open source (MIT)

Makes the 1998 real-time strategy game **Knights and Merchants: The Shattered Kingdom** (official patch 1.32) run on Windows 10 / 11 **without the CD in the drive** – with working music.

> Unofficial fan tool. It contains **no game code and no game data**. It only modifies *your own* installation, creates backups of everything it changes and needs *your own original CD* once (to copy the content and to read the audio tracks).

[Deutsche Version weiter unten](#deutsch)

## What it does

| Problem on Windows 10/11 | What the fix does |
|---|---|
| The game crashes right after start (DirectDraw) | Installs [DDrawCompat](https://github.com/narzoul/DDrawCompat) v0.7.2 as `ddraw.dll` (SHA-256 checked download) |
| "Please insert the CD" although the CD / an ISO is mounted | Removes the CD drive search and the copy-protection self checks (drive search, audio-track count, code checksum) in your EXE files; the CD content is copied once into the sub-folder `CD\` of the game folder |
| No music without an audio CD (an ISO image has no audio tracks) | Reads the 19 audio tracks of your original CD once and stores them as `Music\Track02.wav … Track20.wav`; a small own replacement DLL (`KAMCD.dll`) plays them instead of the CD |
| Desktop shortcuts | Creates shortcuts for 1024x768 and 800x600 with the correct working directory |

Everything can be undone with the **Undo** button (original EXEs are restored from `*.orig_1.32`, `ddraw.dll` and `KAMCD.dll` are removed; the copied `CD\` and `Music\` folders are kept).

## Requirements

* Knights and Merchants: **The Shattered Kingdom**, installed and updated with the **official patch 1.32**
  (German, English or Polish – the three official 1.32 patches; the patcher finds its patch points by byte signature, so it works on all of them. Tested on the EXEs of all three).
* Your **original CD** (not an ISO) in a drive for the first run, if you want music. Without an audio CD the music step is skipped and the game runs without music.
* Windows 10 / 11, internet access once (for DDrawCompat) – or put `ddraw.dll` v0.7.2 next to the patcher.

*Not for The Peasants Rebellion (TPR).* The community patch packs (Patch Pack, Service Release 2, 1.6 patch) and KaM Remake are made for TPR and do not fit a plain TSK install.

## Use

1. Install the game and the official patch 1.32.
2. Run `KaM_TSK_Win10_Fix_1.33.exe` (it asks for administrator rights if the game is in *Program Files*). It finds the game folder automatically when it is started from there. Otherwise choose the folder.
3. Put the original CD in the drive and press **Install** (≈ 3 minutes for the audio tracks).
4. Start the game with the new desktop shortcut.

Command line: `KaM_TSK_Win10_Fix_1.33.exe /silent /dir="C:\Games\KnightsAndMerchants" [/cd="D:\"] [/nocd] [/noddraw] [/nomusic] [/noshortcut] [/undo]`

## Known limitations

* The in-game music volume slider has no effect (Windows 10 has no CD volume line). Use the Windows volume mixer.
* Music is stored as uncompressed WAV (≈ 450 MB).
* The patched game looks for the CD content in the relative folder `CD\` – always start it through the shortcut / from its own folder.
* Languages other than the three official TSK 1.32 patches (German, English, Polish) are untested. Texts come from `Data\Misc\text.lib`, so to get English texts you need the English patch's `text.lib` – the patcher does not change the language.
* Only the original, unmodified 1.32 EXEs are recognised. EXEs already changed by other cracks are refused ("foreign modifications").

## How it works

See [docs/HOW_IT_WORKS.md](docs/HOW_IT_WORKS.md) (reverse engineering notes).

## Build

```powershell
# 1. KAMCD.dll (needs TinyCC, 32-bit tcc.exe):
.\src\winmm_cd\build.ps1 -Tcc C:\path\to\tcc.exe -Out build\KAMCD.dll
# 2. patcher (C# 5 compiler of Windows .NET Framework 4, csc.exe):
csc /target:winexe /out:KaM_TSK_Win10_Fix_1.33.exe /win32manifest:src\app.manifest ^
    /resource:build\KAMCD.dll,KAMCD.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll src\KamPatcher.cs
```

## License and credits

* The code in this repository: **MIT**, © 2026 Daniel Airich – see [LICENSE](LICENSE).
* DDrawCompat by narzoul (BSD Zero Clause License) is downloaded from its official release – see [THIRD_PARTY.md](THIRD_PARTY.md).
* *Knights and Merchants* is © Joymania Entertainment / Topware Interactive. This project is not affiliated with them. Use it only with a copy of the game that you legally own.

---

## Deutsch

**Von Daniel Airich** · Open Source (MIT)

Lässt das Spiel **Knights and Merchants: The Shattered Kingdom** (offizieller Patch 1.32) unter Windows 10/11 laufen – **ohne CD im Laufwerk** und mit funktionierender Musik.

> Inoffizielles Fan-Werkzeug. Es enthält **keinen Spielcode und keine Spieldaten**. Es ändert nur *deine eigene* Installation, legt Sicherungen an und braucht *deine eigene Original-CD* einmalig (zum Kopieren des Inhalts und zum Auslesen der Audio-Tracks).

**Was es macht**

* **Grafik-Fix:** Das Spiel stürzt unter Windows 10/11 beim Start ab. Der Patcher installiert DDrawCompat v0.7.2 (`ddraw.dll`, per SHA-256 geprüft).
* **CD-Abfrage:** Die Laufwerkssuche und die Selbstprüfungen des Spiels (Laufwerk, Audio-Track-Zahl, Code-Prüfsumme) werden in deinen EXE-Dateien angepasst. Der CD-Inhalt wird einmalig in den Unterordner `CD\` kopiert.
* **Musik:** Ein ISO-Image hat keine Audio-Spuren. Der Patcher liest die 19 Audio-Tracks deiner Original-CD einmalig aus (`Music\Track02.wav` … `Track20.wav`); eine kleine eigene Ersatz-DLL (`KAMCD.dll`) spielt sie anstelle der CD ab.
* **Verknüpfungen** für 1024x768 und 800x600.
* **Rückgängig** stellt die Original-EXEs wieder her.

**Voraussetzungen:** The Shattered Kingdom mit dem offiziellen Patch 1.32 (Deutsch, Englisch, Polnisch), für die Musik die Original-CD im Laufwerk. **Nicht** für The Peasants Rebellion (TPR): Patch Pack, Service Release 2, 1.6 Patch und KaM Remake sind für TPR gemacht.

**Benutzung:** Patcher starten (am besten aus dem Spielordner), Original-CD einlegen, *Installieren*, Spiel über die neue Desktop-Verknüpfung starten.

**Bekannte Einschränkungen:** Der Musik-Lautstärkeregler im Spiel wirkt nicht (Windows-Mixer benutzen). Die Musik liegt unkomprimiert als WAV (ca. 450 MB). Der Patcher ändert nicht die Sprache (Texte stehen in `Data\Misc\text.lib`).

**Rechtliches:** Nur mit einer legal erworbenen Kopie des Spiels verwenden. *Knights and Merchants* ist © Joymania / Topware; dieses Projekt steht in keiner Verbindung zu ihnen.
