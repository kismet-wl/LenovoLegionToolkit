@echo off

echo Cleaning build outputs...

set ERROR_OCCURRED=0

if exist .vs (
    rmdir /s /q .vs 2>nul
    if exist .vs set ERROR_OCCURRED=1
)
if exist _ReSharper.Caches (
    rmdir /s /q _ReSharper.Caches 2>nul
    if exist _ReSharper.Caches set ERROR_OCCURRED=1
)

if exist build (
    rmdir /s /q build 2>nul
    if exist build set ERROR_OCCURRED=1
)
if exist build_installer (
    rmdir /s /q build_installer 2>nul
    if exist build_installer set ERROR_OCCURRED=1
)

if exist LenovoLegionToolkit.CLI\bin (
    rmdir /s /q LenovoLegionToolkit.CLI\bin 2>nul
    if exist LenovoLegionToolkit.CLI\bin set ERROR_OCCURRED=1
)
if exist LenovoLegionToolkit.CLI\obj (
    rmdir /s /q LenovoLegionToolkit.CLI\obj 2>nul
    if exist LenovoLegionToolkit.CLI\obj set ERROR_OCCURRED=1
)

if exist LenovoLegionToolkit.Lib\bin (
    rmdir /s /q LenovoLegionToolkit.Lib\bin 2>nul
    if exist LenovoLegionToolkit.Lib\bin set ERROR_OCCURRED=1
)
if exist LenovoLegionToolkit.Lib\obj (
    rmdir /s /q LenovoLegionToolkit.Lib\obj 2>nul
    if exist LenovoLegionToolkit.Lib\obj set ERROR_OCCURRED=1
)

if exist LenovoLegionToolkit.Lib.Automation\bin (
    rmdir /s /q LenovoLegionToolkit.Lib.Automation\bin 2>nul
    if exist LenovoLegionToolkit.Lib.Automation\bin set ERROR_OCCURRED=1
)
if exist LenovoLegionToolkit.Lib.Automation\obj (
    rmdir /s /q LenovoLegionToolkit.Lib.Automation\obj 2>nul
    if exist LenovoLegionToolkit.Lib.Automation\obj set ERROR_OCCURRED=1
)

if exist LenovoLegionToolkit.Lib.CLI\bin (
    rmdir /s /q LenovoLegionToolkit.Lib.CLI\bin 2>nul
    if exist LenovoLegionToolkit.Lib.CLI\bin set ERROR_OCCURRED=1
)
if exist LenovoLegionToolkit.Lib.CLI\obj (
    rmdir /s /q LenovoLegionToolkit.Lib.CLI\obj 2>nul
    if exist LenovoLegionToolkit.Lib.CLI\obj set ERROR_OCCURRED=1
)

if exist LenovoLegionToolkit.Lib.Macro\bin (
    rmdir /s /q LenovoLegionToolkit.Lib.Macro\bin 2>nul
    if exist LenovoLegionToolkit.Lib.Macro\bin set ERROR_OCCURRED=1
)
if exist LenovoLegionToolkit.Lib.Macro\obj (
    rmdir /s /q LenovoLegionToolkit.Lib.Macro\obj 2>nul
    if exist LenovoLegionToolkit.Lib.Macro\obj set ERROR_OCCURRED=1
)

if exist LenovoLegionToolkit.WPF\bin (
    rmdir /s /q LenovoLegionToolkit.WPF\bin 2>nul
    if exist LenovoLegionToolkit.WPF\bin set ERROR_OCCURRED=1
)
if exist LenovoLegionToolkit.WPF\obj (
    rmdir /s /q LenovoLegionToolkit.WPF\obj 2>nul
    if exist LenovoLegionToolkit.WPF\obj set ERROR_OCCURRED=1
)

if exist LenovoLegionToolkit.SpectrumTester\bin (
    rmdir /s /q LenovoLegionToolkit.SpectrumTester\bin 2>nul
    if exist LenovoLegionToolkit.SpectrumTester\bin set ERROR_OCCURRED=1
)
if exist LenovoLegionToolkit.SpectrumTester\obj (
    rmdir /s /q LenovoLegionToolkit.SpectrumTester\obj 2>nul
    if exist LenovoLegionToolkit.SpectrumTester\obj set ERROR_OCCURRED=1
)

if %ERROR_OCCURRED% EQU 0 (
    echo Clean completed successfully!
) else (
    echo Clean completed with some errors. Some files may be in use.
)
