using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia;
using Zhuoying.Platform;

namespace Zhuoying.Agent;

/// <summary>
/// MCP（Model Context Protocol）JSON-RPC 2.0 消息处理（传输无关；宿主为
/// McpHttpServer——托盘实例内置本机 HTTP 服务，设置开关即时启停）。
/// 手写实现而非官方 SDK——只需 initialize/tools/list/tools/call/ping 四个方法，
/// JsonDocument + Utf8JsonWriter 零反射、NativeAOT 友好、零新依赖。
///
/// 工具（只"看"不"动"）：
///   take_screenshot  区域/显示器/窗口标题三选一定位，返回 PNG 图像内容
///   list_windows     可见顶层窗口（标题 + 物理像素矩形）
///   get_monitors     显示器拓扑（物理像素 + 缩放比）
/// </summary>
internal static class McpProtocol
{
    /// <summary>服务器使用说明——单一来源：initialize 响应的 instructions 字段（并入
    /// 模型上下文，MCP 的官方"技能"通道）与 GET /help 页面（给人看）共用。</summary>
    public const string Instructions =
        """
        捉影（zhuoying）截屏 MCP 服务器：只读观察屏幕，不做任何输入或修改。

        坐标体系：所有坐标均为虚拟屏幕物理像素（非 DIP）。混合 DPI 多屏下先调
        get_monitors 获取各显示器的物理边界与缩放比，再据此换算区域。

        工具用法：
        - get_monitors：显示器拓扑（物理像素边界/工作区、缩放比、主屏标记）。
        - list_windows：可见顶层窗口列表（标题 + 物理像素矩形，自顶向下 Z 序，
          已过滤最小化/隐身/穿透层）。
        - take_screenshot：截屏返回 PNG。region / monitor / window_title 三选一：
          region 为 {x,y,w,h} 物理像素区域；monitor 为 get_monitors 的 index；
          window_title 为标题子串（不区分大小写，取 Z 序最顶的匹配窗口）；
          全省略 = 整个虚拟屏幕——多屏时图像很大，优先指定区域或显示器。
          独占全屏游戏、视频叠加层、HDR 桌面均能正确捕获。

        推荐流程：get_monitors / list_windows 定位目标 → take_screenshot 按区域
        截取 → 需要跟踪画面变化时对同一区域重复截取比对。
        """;

    /// <summary>处理一条 JSON-RPC 消息；通知返回 null（无响应体）。解析异常抛出，
    /// 由传输层转 -32700。</summary>
    public static string? HandleMessage(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("method", out var methodEl))
            return null; // 响应消息，忽略
        var method = methodEl.GetString();
        var hasId = root.TryGetProperty("id", out var idEl);
        var id = hasId ? idEl.GetRawText() : "null";

        switch (method)
        {
            case "initialize":
            {
                // 回显客户端请求的协议版本（本服务器方法面极小，向后兼容）
                var version = "2025-06-18";
                if (root.TryGetProperty("params", out var p)
                    && p.TryGetProperty("protocolVersion", out var v)
                    && v.ValueKind == JsonValueKind.String)
                    version = v.GetString()!;
                return Result(id, w =>
                {
                    w.WriteString("protocolVersion", version);
                    w.WriteStartObject("capabilities");
                    w.WriteStartObject("tools");
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteStartObject("serverInfo");
                    w.WriteString("name", "zhuoying");
                    w.WriteString("version", AppVersion.Display);
                    w.WriteEndObject();
                    w.WriteString("instructions", Instructions);
                });
            }
            case "notifications/initialized":
            case "notifications/cancelled":
                return null;
            case "ping":
                return Result(id, _ => { });
            case "tools/list":
                return Result(id, WriteToolsList);
            case "tools/call":
                return ToolsCall(id, root);
            default:
                return hasId ? Error(id, -32601, $"未知方法 {method}") : null;
        }
    }

    private static void WriteToolsList(Utf8JsonWriter w)
    {
        w.WriteStartArray("tools");

        w.WriteStartObject();
        w.WriteString("name", "take_screenshot");
        w.WriteString("description",
            "截取屏幕并返回 PNG 图像。坐标一律为虚拟屏幕物理像素。" +
            "region / monitor / window_title 三选一（都省略 = 整个虚拟屏幕，" +
            "多屏时图像很大，建议优先指定区域或显示器）。" +
            "独占全屏游戏、视频叠加层、HDR 桌面均能正确捕获。");
        w.WritePropertyName("inputSchema");
        // 注意：raw 值必须单行——stdio 传输按 newline 分帧，内嵌换行会拆散消息
        w.WriteRawValue(
            """{"type":"object","properties":{"region":{"type":"object","description":"物理像素区域","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"w":{"type":"integer"},"h":{"type":"integer"}},"required":["x","y","w","h"]},"monitor":{"type":"integer","description":"显示器序号（get_monitors 的 index）"},"window_title":{"type":"string","description":"窗口标题子串（不区分大小写，取 Z 序最顶的匹配窗口）"}}}""");
        w.WriteEndObject();

        w.WriteStartObject();
        w.WriteString("name", "list_windows");
        w.WriteString("description",
            "列出当前可见的顶层窗口（标题 + 物理像素矩形，自顶向下 Z 序，已过滤最小化/隐身/穿透层）。");
        w.WritePropertyName("inputSchema");
        w.WriteRawValue("""{"type":"object","properties":{}}""");
        w.WriteEndObject();

        w.WriteStartObject();
        w.WriteString("name", "get_monitors");
        w.WriteString("description",
            "列出显示器拓扑：物理像素边界/工作区、DPI 缩放比、是否主屏。混合 DPI 多屏下做坐标换算的依据。");
        w.WritePropertyName("inputSchema");
        w.WriteRawValue("""{"type":"object","properties":{}}""");
        w.WriteEndObject();

        w.WriteEndArray();
    }

    private static string ToolsCall(string id, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p)
            || !p.TryGetProperty("name", out var nameEl))
            return Error(id, -32602, "缺少工具名");
        var name = nameEl.GetString();
        p.TryGetProperty("arguments", out var args);

        try
        {
            switch (name)
            {
                case "get_monitors":
                    return TextResult(id, AgentCli.MonitorsJson());
                case "list_windows":
                    return TextResult(id, AgentCli.WindowsJson());
                case "take_screenshot":
                {
                    var region = ResolveRegion(args);
                    var png = AgentCli.CapturePng(region, out _);
                    return Result(id, w =>
                    {
                        w.WriteStartArray("content");
                        w.WriteStartObject();
                        w.WriteString("type", "image");
                        w.WriteString("data", Convert.ToBase64String(png));
                        w.WriteString("mimeType", "image/png");
                        w.WriteEndObject();
                        w.WriteEndArray();
                    });
                }
                default:
                    return Error(id, -32602, $"未知工具 {name}");
            }
        }
        catch (Exception ex)
        {
            // 工具执行失败：按 MCP 语义返回 isError 结果而非协议错误
            return Result(id, w =>
            {
                w.WriteStartArray("content");
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", ex.Message);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteBoolean("isError", true);
            });
        }
    }

    /// <summary>take_screenshot 的区域解析：region > window_title > monitor > 全虚拟屏。</summary>
    private static PixelRect ResolveRegion(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("region", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                var rect = new PixelRect(
                    r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(),
                    r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32());
                if (rect.Width <= 0 || rect.Height <= 0)
                    throw new ArgumentException($"非法区域 {rect}");
                return rect;
            }
            if (args.TryGetProperty("window_title", out var t)
                && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } title)
            {
                foreach (var (winTitle, rect) in PlatformServices.ScreenCapture.GetVisibleWindowsWithTitles())
                    if (winTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
                        return Clamp(rect, AgentCli.VirtualScreen());
                throw new ArgumentException($"没有标题含 \"{title}\" 的可见窗口");
            }
            if (args.TryGetProperty("monitor", out var m) && m.ValueKind == JsonValueKind.Number)
            {
                var monitors = PlatformServices.ScreenCapture.GetAllMonitors();
                var index = m.GetInt32();
                if (index < 0 || index >= monitors.Count)
                    throw new ArgumentException($"显示器序号 {index} 超界（共 {monitors.Count} 台）");
                return monitors[index].Bounds;
            }
        }
        return AgentCli.VirtualScreen();
    }

    private static PixelRect Clamp(PixelRect rect, PixelRect bounds)
    {
        var x1 = Math.Max(rect.X, bounds.X);
        var y1 = Math.Max(rect.Y, bounds.Y);
        var x2 = Math.Min(rect.Right, bounds.Right);
        var y2 = Math.Min(rect.Bottom, bounds.Bottom);
        if (x2 <= x1 || y2 <= y1)
            throw new ArgumentException("窗口不在屏幕范围内");
        return new PixelRect(x1, y1, x2 - x1, y2 - y1);
    }

    private static string TextResult(string id, string text) => Result(id, w =>
    {
        w.WriteStartArray("content");
        w.WriteStartObject();
        w.WriteString("type", "text");
        w.WriteString("text", text);
        w.WriteEndObject();
        w.WriteEndArray();
    });

    private static string Result(string id, Action<Utf8JsonWriter> writeResult) =>
        AgentCli.JsonText(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            w.WriteRawValue(id);
            w.WriteStartObject("result");
            writeResult(w);
            w.WriteEndObject();
            w.WriteEndObject();
        });

    private static string Error(string id, int code, string message) =>
        AgentCli.JsonText(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            w.WriteRawValue(id);
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        });
}
