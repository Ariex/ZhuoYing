@ECHO OFF
REM NativeAOT 发布（Windows x64）
REM 前置：VS「使用 C++ 的桌面开发」工作负载。
REM ILCompiler 自带的 vcvars 探测脚本在 VS 2026 上不可靠，
REM 故先进 vcvars64 环境并用 IlcUseEnvironmentalTools 直接走环境工具链。
REM 产物：publish\aot\（Zhuoying.exe + 3 个原生库；.pdb 分发时不带）

SETLOCAL
CD /D "%~dp0"

CALL "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >NUL 2>&1
IF ERRORLEVEL 1 (
    ECHO [错误] 未找到 vcvars64.bat，请确认已安装 VS 的 C++ 桌面开发工作负载
    EXIT /B 1
)

dotnet publish src\Zhuoying\Zhuoying.csproj -c Release -r win-x64 ^
    -p:PublishAot=true ^
    -p:StripSymbols=true ^
    -p:IlcUseEnvironmentalTools=true ^
    -o publish\aot
IF ERRORLEVEL 1 EXIT /B 1

ECHO.
ECHO AOT 发布完成：%~dp0publish\aot