@echo off
rem install.sh から呼ぶ。VSPATH に Visual Studio（Build Tools）の場所を入れておく
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist mmdhmd\bin\win64 mkdir mmdhmd\bin\win64
cl /nologo /LD /EHsc /O2 /std:c++17 /utf-8 /Iinclude src\driver.cpp /Fe:mmdhmd\bin\win64\driver_mmdhmd.dll /Fo:mmdhmd\bin\win64\ ws2_32.lib
