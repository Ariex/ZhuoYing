#!/usr/bin/env bash
# 安装 .desktop 到用户应用目录。
#
# 不只是为了出现在应用菜单里——**Wayland 全局热键必须靠它**：
# xdg-desktop-portal 的 GlobalShortcuts 要求调用方有 app id，而 app id 是从进程的
# systemd scope 名（app-<appid>-<pid>.scope）反推的。从终端直接运行的进程没有 scope，
# portal 会直接拒绝（NotAllowed: An app id is required），热键必然不可用。
# 经应用菜单或 XDG autostart 启动才会被放进正确的 scope。
set -euo pipefail
cd "$(dirname "$0")"

BIN="${1:-$PWD/publish/linux-x64/Zhuoying}"
[ -x "$BIN" ] || BIN="$PWD/src/Zhuoying/bin/Debug/net10.0/Zhuoying"
[ -x "$BIN" ] || { echo "找不到可执行文件，先 ./build-linux.sh publish 或传入路径" >&2; exit 1; }

DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$DIR"
cat > "$DIR/zhuoying.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=捉影
Comment=截屏标注工具
Exec="$BIN"
Terminal=false
Categories=Graphics;Utility;
StartupNotify=false
DESKTOP
command -v update-desktop-database >/dev/null && update-desktop-database "$DIR" || true
echo "已安装 $DIR/zhuoying.desktop → $BIN"
echo
echo "全局热键提醒：请从应用菜单启动捉影（或开启开机自启），"
echo "从终端直接运行拿不到 app id，Wayland 下热键会注册失败。"
