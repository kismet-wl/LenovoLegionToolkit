@echo off

echo Cleaning build outputs...

if exist .vs rmdir /s /q .vs
if exist _ReSharper.Caches rmdir /s /q _ReSharper.Caches

if exist build rmdir /s /q build
if exist build_installer rmdir /s /q build_installer

if exist LenovoLegionToolkit.CLI\bin rmdir /s /q LenovoLegionToolkit.CLI\bin
if exist LenovoLegionToolkit.CLI\obj rmdir /s /q LenovoLegionToolkit.CLI\obj

if exist LenovoLegionToolkit.Lib\bin rmdir /s /q LenovoLegionToolkit.Lib\bin
if exist LenovoLegionToolkit.Lib\obj rmdir /s /q LenovoLegionToolkit.Lib\obj

if exist LenovoLegionToolkit.Lib.Automation\bin rmdir /s /q LenovoLegionToolkit.Lib.Automation\bin
if exist LenovoLegionToolkit.Lib.Automation\obj rmdir /s /q LenovoLegionToolkit.Lib.Automation\obj

if exist LenovoLegionToolkit.Lib.CLI\bin rmdir /s /q LenovoLegionToolkit.Lib.CLI\bin
if exist LenovoLegionToolkit.Lib.CLI\obj rmdir /s /q LenovoLegionToolkit.Lib.CLI\obj

if exist LenovoLegionToolkit.Lib.Macro\bin rmdir /s /q LenovoLegionToolkit.Lib.Macro\bin
if exist LenovoLegionToolkit.Lib.Macro\obj rmdir /s /q LenovoLegionToolkit.Lib.Macro\obj

if exist LenovoLegionToolkit.WPF\bin rmdir /s /q LenovoLegionToolkit.WPF\bin
if exist LenovoLegionToolkit.WPF\obj rmdir /s /q LenovoLegionToolkit.WPF\obj

if exist LenovoLegionToolkit.SpectrumTester\bin rmdir /s /q LenovoLegionToolkit.SpectrumTester\bin
if exist LenovoLegionToolkit.SpectrumTester\obj rmdir /s /q LenovoLegionToolkit.SpectrumTester\obj

echo Clean completed successfully!
