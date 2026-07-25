@ECHO OFF
REM 自包含单文件发布（Windows x64，非 AOT）
REM 免装 .NET 运行时；原生库内嵌（首次启动解压到临时目录）；压缩后约 46MB。
REM 产物：publish\singlefile\Zhuoying.exe（单文件）

SETLOCAL
CD /D "%~dp0"

dotnet publish src\Zhuoying\Zhuoying.csproj -c Release -r win-x64 ^
    -p:PublishSingleFile=true ^
    -p:SelfContained=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o publish\singlefile
IF ERRORLEVEL 1 EXIT /B 1

ECHO.
ECHO 单文件发布完成：%~dp0publish\singlefile\Zhuoying.exe