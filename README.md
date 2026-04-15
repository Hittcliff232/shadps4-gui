# ShadPS4 Launcher by Hittcliff

GUI launcher for the **shadPS4** emulator (CLI). Lets you manage games, set graphics/audio/input options, and create desktop shortcuts.

## Features

- **Games list** — Scans your game folders for PS4 games (eboot.bin + sce_sys) and shows them in a list.
- **Launch** — Start shadPS4 with the selected game and current options.
- **Create desktop shortcut** — One-click shortcut on the desktop for the selected game.
- **Settings** — Stored in `%LocalAppData%\ShadPS4Launcher\launcher_settings.json`.
- **ShadPS4 config** — Reads and writes `config.toml` (in ShadPS4 user folder, default `%APPDATA%\shadPS4`) for:
  - Graphics: resolution, fullscreen, FSR, RCAS, GPU choice
  - Audio: volume
  - CPU: extra DMEM
  - Input: motion controls, background input
- **Add game folder** — Add a directory that contains PS4 games; the launcher registers it and refreshes the list.
- **Addon folder** — Set DLC/addon path.
- **All CLI options** — Fullscreen, patch file, override root, show FPS, ignore game patch, debugger wait, config mode, log append.

<img width="3440" height="1440" alt="{2D7D0E6D-118B-43A0-B15A-F3B8ED8AAC4F}" src="https://github.com/user-attachments/assets/c612f2d5-dcdf-4a96-9fbf-d45de4bcfb7d" />
<img width="3440" height="1440" alt="{BACB7566-FBE0-48B0-99A9-7EAB78A7E63F}" src="https://github.com/user-attachments/assets/97f85e29-c9d0-4d8f-b452-08fa6148843f" />
<img width="3440" height="1440" alt="{D779DABB-959C-4164-8216-3B294A4DD3BF}" src="https://github.com/user-attachments/assets/1cdaaeba-ccc9-4b8c-b2ba-24ace1f86356" />
<img width="3440" height="1440" alt="{6CFB3D94-1ECE-4347-A9A6-96D9240FA715}" src="https://github.com/user-attachments/assets/5542410f-6586-44f8-acfb-888afe77b355" />
<img width="3440" height="1440" alt="{FB1824DA-0A5D-4638-BADD-22599A0DDDDF}" src="https://github.com/user-attachments/assets/e0851e32-b82e-458e-833f-dd8c1ca0dfcf" />
<img width="3440" height="1440" alt="{8CAB6134-932A-458E-B433-0E803243B13F}" src="https://github.com/user-attachments/assets/11ef0000-4ed7-4261-9ef1-a180b706ae80" />

## Requirements

- Windows 10/11
- .NET 8.0 (Windows Desktop)
- shadPS4 emulator (`shadps4.exe`)

## Build

```bash
cd ShadPS4Launcher
dotnet build -c Release
```

Output: `bin\Release\net8.0-windows\ShadPS4 Launcher by Hittcliff.exe`

## Usage

1. Set **Emulator path** to your `shadps4.exe` (e.g. `c:\Infamous Second Son\ShadPS4\Build\shadps4.exe`).
2. (Optional) Set **User data folder** if you use a portable ShadPS4 layout; otherwise the default is `%APPDATA%\shadPS4`.
3. Click **Add game folder...** and choose a folder that contains PS4 game(s) (with `eboot.bin` and `sce_sys`).
4. Select a game in the list, adjust **General / Graphics / Audio / Input** as needed.
5. Click **Launch** or **Create desktop shortcut**.
