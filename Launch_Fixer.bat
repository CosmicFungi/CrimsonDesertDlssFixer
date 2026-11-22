@echo off
if exist "%~dp0publish\CrimsonDesertDlssFixer.exe" (
    start "" "%~dp0publish\CrimsonDesertDlssFixer.exe"
) else (
    start "" "%~dp0bin\Release\net10.0-windows\CrimsonDesertDlssFixer.exe"
)
