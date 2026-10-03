@echo off
setlocal

cl /LD /EHsc /std:c++20 airadio_windows.cpp /link winmm.lib /OUT:airadio-pipewire.dll

if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b %errorlevel%
)

echo.
echo Built airadio-pipewire.dll successfully.
