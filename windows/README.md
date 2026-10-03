# BigFiles for Windows

<img src="icon.png" width="96" align="right" alt="BigFiles icon">

A small Windows app that shows what takes up disk space, **grouped by the app it belongs to**.
"League of Legends" is one line that combines `C:\Riot Games\League of Legends` and its AppData
folders; expand it to see its biggest files.

It's the Windows counterpart of the macOS app in the root of this repository, written natively in C# / WPF.

## Download

Get `BigFiles-win-x64.zip` from the [Releases](https://github.com/MrStickman202/BigFiles/releases) page,
unzip it and run `BigFiles.exe`. It's one self-contained file: no installer, and no .NET needed.
It runs without administrator rights.

> **The exe is not code-signed**, so Windows SmartScreen may say "Windows protected your PC".
> Click **More info → Run anyway**.

## What it does

- **Scopes** (toolbar dropdown):
  - **Apps & User folder** (default): `Program Files`, `Program Files (x86)`, `ProgramData` and your user folder,
    plus the install folders of registered apps that live elsewhere (e.g. `C:\Riot Games\League of Legends`).
  - **Whole PC**: every fixed drive.
  - **Choose folder…**: any folder.
- **Groups first.** Every file belongs to exactly one group:
  - **App**: an installed app (from the registry's Uninstall keys and the Store app list), combining its install
    folder with its data in `AppData\Local`, `Roaming`, `LocalLow`, `Packages` and `ProgramData`.
    Each Steam game (`steamapps\common\<Game>`) and each game in `Epic Games`, `XboxGames`, `GOG Games`,
    `EA Games` is its own group. Vendor folders are looked into: `Google\Chrome` → Google Chrome.
  - **App data**: leftovers from apps that aren't installed, named after their folder.
  - **Folder**: `~\Documents`, `~\Downloads`, `~\OneDrive`, … and top-level folders like `D:\Photos`.
  - **System**: Windows, Windows system files (`pagefile.sys`, `hiberfil.sys`, `swapfile.sys`…),
    Recycle Bin, Temporary files, Other.
- **Sizes add up.** Under each group: its files at or above the minimum size, then **Smaller files**
  (everything else). That row expands to the biggest files under the filter (up to 300), followed by
  "N more files" or "Tiny files (under 1 MB each)".
- **Size on disk**, like Explorer's "Size on disk": compressed files count compressed, OneDrive online-only
  files count as 0, and hard-linked files count once.
- **Fast and safe scanning**: one pass on a background thread, so the window stays responsive. Stop shows what
  was found so far. Junctions and symbolic links are never followed (AppData has junction loops), and files
  are never opened or read, so OneDrive never downloads anything. Folders it can't read are skipped and counted
  in the status bar; click the link there to restart as administrator.
- **Search** by name or path, sort by any column, filter by minimum size (1 MB … 5 GB).
- Follows the Windows **light / dark** setting.

### Actions (right-click, double-click or keyboard)

| Action | |
|---|---|
| Show in Explorer | Double-click or Enter. A group opens its folders. |
| Open | Opens a file, or a group's folders. |
| Copy path | Ctrl+C |
| Uninstall… | For apps that have an uninstaller: runs it (Store apps open Settings › Apps). |
| Move to Recycle Bin… | Del. Always the Recycle Bin, never a permanent delete. |

**Deleting is careful.** The confirmation lists every path that will be removed and the space it frees, and warns
"This also removes data of: …" when a folder contains another app's data. Some folders are protected and are
refused with an explanation. You can still expand them and remove files inside:
drive roots, `C:\Windows` and everything in it, the `Program Files` / `ProgramData` / `Users` roots, your user
folder, `AppData` and its `Local` / `Roaming` / `LocalLow` roots, Desktop / Documents / Downloads / Pictures /
Videos / Music / OneDrive, `$Recycle.Bin` and `System Volume Information`.
For an installed app with an uninstaller, only its data folders go to the Recycle Bin. Its program folder is left
alone, because deleting it would leave a broken registry entry, so use **Uninstall…** for that.
After a delete, the list and totals update immediately without a rescan.

## Build it yourself

Needs Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
cd windows
powershell -ExecutionPolicy Bypass -File .\build.ps1        # → dist\BigFiles.exe and dist\BigFiles-win-x64.zip
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run   # build and start it
```

The script redraws the icon, runs the logic tests and publishes a single-file, self-contained `win-x64` exe.

## Project layout

```
windows/
  build.ps1                     build script
  src/BigFiles/
    Core/                       UI-independent logic (also compiled into the tests)
      Classifier.cs             which group each folder belongs to
      AppIndex.cs               matching folders to installed apps (install location, names, suffixes)
      Scanner.cs                the single-pass walk and size bookkeeping
      TreeBuilder.cs            rows, filter, search, sort, "Smaller files" breakdown
      Deletion.cs               protected folders, delete plans, updating totals after a delete
    Win/                        Windows-specific: native directory listing, registry, shell, theme
    Views/, MainWindow.xaml     the WPF window, tree table and dialogs
    Themes/                     light and dark colors, control styles
  tests/BigFiles.Tests/         logic tests: dotnet run --project tests/BigFiles.Tests
  tools/IconGen/                draws the icon in code and writes the multi-size .ico
```

### How the scan works

Each folder is listed with `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)` into a 64 KB buffer.
That's the same kernel call `FindFirstFileEx(FIND_FIRST_EX_LARGE_FETCH)` makes, but it also returns each file's
allocation size and file ID, so size on disk and hard-link detection need no extra call per file.
`FindFirstFileEx` is used as a fallback on file systems that don't support it.
Compressed, sparse, deduplicated and cloud files get their real size from `GetCompressedFileSizeW`,
which reads metadata only and doesn't download online-only files.

Reparse points (junctions, symlinks, mount points…) are never entered. The only folders with a reparse tag that
*are* entered are OneDrive-style cloud folders, which are real local folders. Without them, everything in OneDrive
would be missing. Cloud folders whose contents aren't on the PC are skipped.

Only files of 1 MB or more are kept individually. Everything smaller is just added to its group's total.
Hard links are counted once by remembering file IDs per drive, which takes about 16 bytes per file.
