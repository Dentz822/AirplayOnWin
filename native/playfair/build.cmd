@echo off
rem Builds bin\playfair.dll (x64). A prebuilt copy is committed, so this is only needed after changing the C code.
rem Uses Zig if ZIG points to zig.exe (portable, no install: https://ziglang.org/download/),
rem otherwise the Visual Studio C++ compiler ("Desktop development with C++" workload).
setlocal
cd /d "%~dp0"
if not exist bin mkdir bin
set SOURCES=fairplay_export.c src\playfair.c src\omg_hax.c src\hand_garble.c src\modified_md5.c src\sap_hash.c

if defined ZIG (
  "%ZIG%" cc -shared -O2 -target x86_64-windows-gnu -w -o bin\playfair.dll %SOURCES% || exit /b 1
  del /q bin\*.lib bin\*.pdb 2>nul
  goto done
)

for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSDIR=%%i
if not defined VSDIR (echo No compiler: set ZIG=path\to\zig.exe or install VS C++ tools & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist obj mkdir obj
cl /nologo /O2 /LD /W3 /wd4244 /wd4267 /wd4018 /D_CRT_SECURE_NO_WARNINGS /Foobj\ %SOURCES% /Fe:bin\playfair.dll /link /NOLOGO || exit /b 1

:done
echo Built %~dp0bin\playfair.dll
