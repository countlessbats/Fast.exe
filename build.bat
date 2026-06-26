@echo off
setlocal

echo.
echo  ========================================
echo   Building Fast
echo  ========================================
echo.

:: ---- Find MSVC ----
set "VCVARS="
for /f "delims=" %%i in ('where /r "C:\Program Files (x86)\Microsoft Visual Studio" vcvarsall.bat 2^>nul') do (
    set "VCVARS=%%i"
    goto :found_vc
)
for /f "delims=" %%i in ('where /r "C:\Program Files\Microsoft Visual Studio" vcvarsall.bat 2^>nul') do (
    set "VCVARS=%%i"
    goto :found_vc
)
echo [ERROR] Could not find Visual Studio Build Tools (vcvarsall.bat)
echo Install "Desktop development with C++" workload or VS Build Tools.
exit /b 1

:found_vc
echo  [1/4] Compiling FastHook.dll (x64 native C)...
call "%VCVARS%" x64 >nul 2>&1

pushd "%~dp0src\Hook"
cl /nologo /O2 /LD /Fe:"%~dp0bin\FastHook.dll" ^
    /I minhook\include /I minhook\src ^
    dllmain.c speedhook.c ^
    minhook\src\hook.c minhook\src\buffer.c minhook\src\trampoline.c ^
    minhook\src\hde\hde32.c minhook\src\hde\hde64.c ^
    /link /DLL /DEF:NUL winmm.lib kernel32.lib user32.lib
if errorlevel 1 (
    echo [ERROR] FastHook.dll compilation failed.
    popd
    exit /b 1
)
:: Clean up intermediate files
del /q *.obj *.exp *.lib 2>nul
popd
del /q "%~dp0bin\FastHook.exp" "%~dp0bin\FastHook.lib" 2>nul
echo  [OK] FastHook.dll ^-^> bin\FastHook.dll

echo.
echo  [2/4] Compiling FastHook32.dll (x86 native C)...
call "%VCVARS%" x86 >nul 2>&1

pushd "%~dp0src\Hook"
cl /nologo /O2 /LD /Fe:"%~dp0bin\FastHook32.dll" ^
    /I minhook\include /I minhook\src ^
    dllmain.c speedhook.c ^
    minhook\src\hook.c minhook\src\buffer.c minhook\src\trampoline.c ^
    minhook\src\hde\hde32.c minhook\src\hde\hde64.c ^
    /link /DLL /DEF:NUL winmm.lib kernel32.lib user32.lib
if errorlevel 1 (
    echo [ERROR] FastHook32.dll compilation failed.
    popd
    exit /b 1
)
del /q *.obj *.exp *.lib 2>nul
popd
del /q "%~dp0bin\FastHook32.exp" "%~dp0bin\FastHook32.lib" 2>nul
echo  [OK] FastHook32.dll ^-^> bin\FastHook32.dll

:: ---- Build C# host ----
echo.
echo  [3/4] Building Fast host app x64 (.NET 6)...
call "%VCVARS%" x64 >nul 2>&1

:: Try 64-bit dotnet first, then PATH
set "DOTNET="
if exist "C:\Program Files\dotnet\dotnet.exe" (
    set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
) else (
    where dotnet >nul 2>&1
    if not errorlevel 1 set "DOTNET=dotnet"
)

if "%DOTNET%"=="" (
    echo [ERROR] .NET SDK not found. Install from https://dotnet.microsoft.com/download
    exit /b 1
)

"%DOTNET%" publish "%~dp0src\Host\Host.csproj" -c Release -o "%~dp0bin" -r win-x64 --self-contained false -p:PlatformTarget=AnyCPU -p:Prefer32Bit=false --nologo -v q
if errorlevel 1 (
    echo [ERROR] Host app build failed.
    exit /b 1
)
echo  [OK] Fast.exe ^-^> bin\Fast.exe

echo.
echo  [4/4] Building x86 injector helper (.NET 6)...
set "HELPER_OUT=%TEMP%\FastInjector32_publish"
if exist "%HELPER_OUT%" rmdir /s /q "%HELPER_OUT%"
"%DOTNET%" publish "%~dp0src\Host\Host.csproj" -c Release -o "%HELPER_OUT%" -r win-x86 --self-contained false -p:PlatformTarget=AnyCPU -p:Prefer32Bit=false --nologo -v q
if errorlevel 1 (
    echo [ERROR] x86 injector helper build failed.
    exit /b 1
)
copy /y "%HELPER_OUT%\Fast.exe" "%~dp0bin\FastInjector32.exe" >nul
rmdir /s /q "%HELPER_OUT%" 2>nul
echo  [OK] FastInjector32.exe ^-^> bin\FastInjector32.exe

echo.
echo  ========================================
echo   Build complete! Run bin\Fast.exe
echo  ========================================
echo.
