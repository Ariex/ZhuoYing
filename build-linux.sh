#!/usr/bin/env bash
# 捉影 Linux 构建/运行脚本（对应 Windows 的 aot-win.bat）。
#
# DOTNET_SYSTEM_NET_DISABLEIPV6：开发机 IPv6 出网是黑洞（超时而非 REJECT），
# .NET 的 HttpClient 不像 curl 那样 Happy Eyeballs 回退 IPv4，
# 不设此变量 dotnet restore 会在 "Determining projects to restore..." 无限挂起。
# 见 docs/TROUBLESHOOTING.md §13。
export DOTNET_SYSTEM_NET_DISABLEIPV6=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

set -euo pipefail
cd "$(dirname "$0")"

case "${1:-build}" in
  build)   shift; exec dotnet build src/Zhuoying/Zhuoying.csproj "$@" ;;
  run)     shift; exec dotnet run --project src/Zhuoying/Zhuoying.csproj -- "$@" ;;
  restore) shift; exec dotnet restore src/Zhuoying/Zhuoying.csproj "$@" ;;
  publish) shift
           exec dotnet publish src/Zhuoying/Zhuoying.csproj -c Release -r linux-x64 \
                --self-contained -o publish/linux-x64 "$@" ;;
  aot)     shift
           # NativeAOT（对应 Windows 的 aot-win.bat）。前置：clang + zlib1g-dev。
           exec dotnet publish src/Zhuoying/Zhuoying.csproj -c Release -r linux-x64 \
                -p:PublishAot=true -p:StripSymbols=true \
                -o publish/aot-linux "$@" ;;
  *) echo "用法: $0 {build|run|restore|publish|aot} [额外参数]" >&2; exit 2 ;;
esac
