# Task 2 Report: Seven Theme Palettes and Contrast Contract

## Result

Task 2 is complete. Windows now has the seven canonical macOS-aligned themes:

`classic`, `coffee`, `celadon`, `seaSalt`, `mistPurple`, `inkBlue`, `paper`

Each theme has independent light and dark palette values. `ThemePalette` carries
both `Theme` and `Dark`, and resource application preserves both values.

## Files Changed

- `开发空间/Windows/src/Qingyue.Windows/ReaderTheme.cs`
  - Added the seven-theme light/dark palette model.
  - Added palette-derived syntax colors, accent text, saved/unsaved colors,
    `AccentForeground`, annotation colors, and contrast calculation.
  - Registered `AccentForegroundBrush`.
  - Preserved legacy `Dark`, `Light`, and `WarmPaper` migration through the
    canonical seven-theme model.
  - Removed the old two-argument `Apply` behavior that treated every theme
    other than `Light` as dark.
- `开发空间/Windows/src/Qingyue.Windows/Theme.xaml`
  - Added the default `AccentForegroundBrush` resource.
- `开发空间/Windows/src/Qingyue.Windows/NativeTitleBarTheme.cs`
  - Changed native title-bar application to consume `ThemePalette`.
  - Caption, text, border, and immersive dark-mode selection now derive from
    the actual palette.
  - Added the testable `NativeTitleBarTheme.GetColors(ThemePalette)` contract.
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.Reading.cs`
  - Preserved the current canonical theme when switching light/dark mode.
  - Preserved the requested light/dark mode when applying a canonical theme.
  - Removed the post-apply hard-coded syntax/accent overrides.
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml.cs`
  - Applies the actual startup palette to the native title bar.
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml`
  - Replaced four emphasized-button `Foreground="White"` values with
    `{DynamicResource AccentForegroundBrush}`.
- `开发空间/Windows/src/Qingyue.Windows/Dialogs.cs`
  - Changed primary dialog button foreground to the accent foreground token.
- `开发空间/Windows/src/Qingyue.Windows/ReadingAppearanceDialog.cs`
  - Changed the emphasized Apply button foreground to the accent foreground
    token. Other uncommitted changes in this file were not staged.
- `开发空间/Windows/tests/Qingyue.UITests/Program.cs`
  - Runs the theme contract before the existing WPF flow checks.
- `开发空间/Windows/tests/Qingyue.UITests/ThemeChecks.cs`
  - Added actual palette, resource brush, contrast, title-bar, and
    `ApplyTheme` identity/mode checks.

## RED Evidence

Command:

```powershell
$env:HTTP_PROXY=''
$env:HTTPS_PROXY=''
$env:ALL_PROXY=''
$env:MSBuildEnableWorkloadResolver='false'
dotnet build '.\开发空间\Windows\tests\Qingyue.UITests\Qingyue.UITests.csproj' -c Release --nologo --disable-build-servers -p:UseSharedCompilation=false -p:MSBuildEnableWorkloadResolver=false -p:ImportWorkloads=false -m:1
dotnet '.\开发空间\Windows\tests\Qingyue.UITests\bin\Release\net10.0-windows\Qingyue.UITests.dll'
```

The pre-fix run reached all seven themes and both modes. Palette values,
resource registration, and contrast checks passed, then failed with:

```text
System.Exception: 原生标题栏接口接收 ThemePalette
```

This was the expected RED condition because the title-bar implementation still
accepted a `bool` and used fixed classic colors.

## GREEN Evidence

Focused UI build:

```text
Qingyue.Core -> ...\Qingyue.Core.dll
Qingyue.Windows -> ...\Qingyue.dll
Qingyue.UITests -> ...\Qingyue.UITests.dll
已成功生成。
0 个警告
0 个错误
```

Focused UI run:

```text
PASS 原生标题栏接口接收 ThemePalette
PASS 原生标题栏颜色实际来自传入调色板
PASS ApplyTheme 保留 Coffee 的浅色身份
PASS ApplyTheme 保留 Coffee 的深色身份
WPF 流程检查：333 项通过。
```

Affected reading outcome build/run:

```text
已成功生成。
0 个警告
0 个错误
全部通过：177 项
```

The repository `scripts/test.ps1` currently has no `-Project` parameter, so the
focused project was built and executed directly with the same workload-resolver
settings used by the repository build script.

## Exact Contrast Results

The following are the actual values emitted by `ThemeChecks` from
`ReaderTheme.GetPalette`:

| Theme / mode | Text/Editor | Muted/Panel | Accent/Editor | AccentText/Editor | AccentForeground/Accent | Text/Main | Text/Hover |
|---|---:|---:|---:|---:|---:|---:|---:|
| Classic light | 13.80 | 4.52 | 4.54 | 6.27 | 4.75 | 12.52 | 12.41 |
| Classic dark | 9.71 | 7.70 | 3.63 | 6.89 | 5.34 | 9.69 | 9.14 |
| Coffee light | 12.09 | 4.53 | 4.86 | 4.86 | 5.14 | 10.89 | 10.20 |
| Coffee dark | 13.71 | 5.54 | 6.21 | 8.75 | 6.43 | 12.36 | 11.73 |
| Celadon light | 12.48 | 4.74 | 3.25 | 4.74 | 5.52 | 12.14 | 10.79 |
| Celadon dark | 14.85 | 6.54 | 8.27 | 8.27 | 8.63 | 14.29 | 12.06 |
| SeaSalt light | 14.58 | 4.57 | 4.96 | 4.96 | 4.96 | 14.11 | 12.44 |
| SeaSalt dark | 15.22 | 6.80 | 7.65 | 7.65 | 7.86 | 14.79 | 12.43 |
| MistPurple light | 13.80 | 4.56 | 4.81 | 4.81 | 4.93 | 13.45 | 11.88 |
| MistPurple dark | 14.79 | 6.43 | 7.35 | 7.35 | 7.48 | 14.37 | 12.37 |
| InkBlue light | 10.35 | 4.70 | 4.59 | 4.59 | 4.74 | 9.99 | 8.88 |
| InkBlue dark | 15.64 | 6.29 | 3.98 | 5.07 | 4.74 | 14.41 | 11.74 |
| Paper light | 14.68 | 4.83 | 5.18 | 5.18 | 5.18 | 14.68 | 13.12 |
| Paper dark | 14.89 | 6.70 | 6.44 | 6.44 | 6.61 | 14.59 | 12.57 |

All values meet the required thresholds:

- body/editor and muted/panel: `>= 4.5`
- accent/editor: `>= 3.0`
- accentText/editor: `>= 4.5`
- accentForeground/accent: `>= 4.5`
- text/main and text/hover: `>= 4.5`

## Compatibility Impact

- Existing `Dark`, `Light`, and `WarmPaper` values remain accepted for source
  and settings migration, but are normalized to `classic` or `coffee`; they are
  not runtime theme identities in `ReaderTheme.All`.
- `ApplyTheme(bool)` now changes only the light/dark mode while preserving the
  current canonical theme.
- `ApplyTheme(ReaderThemeKind)` preserves the current mode for canonical
  themes; explicit legacy `Dark`, `Light`, and `WarmPaper` values retain their
  legacy mode intent.
- Emphasized controls now follow palette-specific foreground contrast instead
  of assuming white text.

## Remaining Risks

- Native DWM calls remain platform-dependent; the palette selection is covered,
  but the operating system's title-bar rendering cannot be fully asserted in a
  headless test process.
- `JsonSyntaxLayer` still has `Brushes.White` as a non-emphasis fallback when a
  syntax resource is unavailable. No emphasized button retains a hard-coded
  white foreground.
- The full aggregate test script was not used for this task because it has no
  focused-project parameter and its known FileTests ACL step is independent of
  theme behavior. The directly affected UI and reading outcome projects passed.
