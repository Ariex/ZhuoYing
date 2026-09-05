using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Zhuoying.Agent;

/// <summary>
/// MCP Streamable HTTP 服务器：托盘常驻实例内置，仅绑定 127.0.0.1，
/// 设置开关即时启停——无需为 MCP 另起进程或重启程序。
///
/// 手写极简 HTTP/1.1（TcpListener）而非 HttpListener：绕开 http.sys 的
/// URL ACL 权限差异，零依赖可预期。协议面：POST /mcp 携带单条 JSON-RPC，
/// 请求回 200 application/json、通知回 202；GET /help 返回给人看的使用说明页
/// （与 initialize 的 instructions 同源）；其余 GET（含根路径）一律 405，
/// 刻意不暴露服务身份（不提供 SSE 推送，规范允许）；校验 Origin 防 DNS rebinding。
///
/// Claude Code 配置示例（.mcp.json）：
///   { "mcpServers": { "zhuoying": { "type": "http", "url": "http://127.0.0.1:8990/mcp" } } }
/// </summary>
internal sealed class McpHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private volatile bool _stopped;

    public int Port { get; }

    /// <summary>启动失败（端口占用等）抛出，调用方提示用户。</summary>
    public McpHttpServer(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "mcp-http" }.Start();
    }

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
    }

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return; // Stop() 中断 Accept
            }
            new Thread(() => ServeClient(client)) { IsBackground = true }.Start();
        }
    }

    private void ServeClient(TcpClient client)
    {
        using var _ = client;
        client.ReceiveTimeout = 120_000;
        var stream = client.GetStream();
        try
        {
            while (!_stopped && HandleOneRequest(stream))
            {
            }
        }
        catch (IOException)
        {
            // 连接断开
        }
    }

    /// <summary>处理一次请求；返回 false 表示连接应关闭。</summary>
    private bool HandleOneRequest(NetworkStream stream)
    {
        var head = ReadHead(stream);
        if (head == null)
            return false;
        var lines = head.Split("\r\n");
        var requestParts = lines[0].Split(' ');
        if (requestParts.Length < 3)
            return false;
        var method = requestParts[0];
        var path = requestParts[1];
        var query = path.IndexOf('?');
        if (query >= 0)
            path = path[..query];

        var contentLength = 0;
        string? origin = null;
        var keepAlive = true;
        foreach (var line in lines.AsSpan(1))
        {
            var sep = line.IndexOf(':');
            if (sep < 0)
                continue;
            var name = line[..sep].Trim().ToLowerInvariant();
            var value = line[(sep + 1)..].Trim();
            switch (name)
            {
                case "content-length":
                    int.TryParse(value, out contentLength);
                    break;
                case "origin":
                    origin = value;
                    break;
                case "connection":
                    keepAlive = !value.Contains("close", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        // 防 DNS rebinding：浏览器跨源请求带非本机 Origin 时拒绝
        if (origin != null
            && !origin.Contains("://127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !origin.Contains("://localhost", StringComparison.OrdinalIgnoreCase))
        {
            Respond(stream, "403 Forbidden", null, keepAlive: false);
            return false;
        }

        var body = contentLength > 0 ? ReadBody(stream, contentLength) : null;
        if (method == "GET" && path == "/help")
        {
            Respond(stream, "200 OK", HelpPage(), keepAlive,
                contentType: "text/html; charset=utf-8");
            return keepAlive;
        }
        if (method != "POST")
        {
            Respond(stream, "405 Method Not Allowed", null, keepAlive, "Allow: POST\r\n");
            return keepAlive;
        }
        if (body == null || body.Length == 0)
        {
            Respond(stream, "400 Bad Request", null, keepAlive);
            return keepAlive;
        }

        string? response;
        try
        {
            response = McpProtocol.HandleMessage(Encoding.UTF8.GetString(body));
        }
        catch (Exception ex)
        {
            response = AgentCli.JsonText(w =>
            {
                w.WriteStartObject();
                w.WriteString("jsonrpc", "2.0");
                w.WriteNull("id");
                w.WriteStartObject("error");
                w.WriteNumber("code", -32700);
                w.WriteString("message", ex.Message);
                w.WriteEndObject();
                w.WriteEndObject();
            });
        }

        if (response == null)
            Respond(stream, "202 Accepted", null, keepAlive); // 通知无响应体
        else
            Respond(stream, "200 OK", Encoding.UTF8.GetBytes(response), keepAlive);
        return keepAlive;
    }

    /// <summary>读请求头（到空行为止；上限 32KB 防滥用）。连接关闭返回 null。</summary>
    private static string? ReadHead(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var last4 = 0;
        while (buffer.Length < 32 * 1024)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return null;
            buffer.WriteByte((byte)b);
            last4 = ((last4 << 8) | b) & unchecked((int)0xFFFFFFFF);
            if (last4 == 0x0D0A0D0A) // \r\n\r\n
                return Encoding.UTF8.GetString(buffer.ToArray(), 0, (int)buffer.Length - 4);
        }
        return null;
    }

    private static byte[]? ReadBody(NetworkStream stream, int length)
    {
        if (length > 8 * 1024 * 1024)
            return null;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(body, read, length - read);
            if (n <= 0)
                return null;
            read += n;
        }
        return body;
    }

    /// <summary>给人看的使用说明页（GET /help）：说明文本与 initialize 的
    /// instructions 同源，另附本机端点与 Claude Code 配置示例。</summary>
    private byte[] HelpPage()
    {
        var html = $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>捉影 MCP 服务器</title>
            <style>
              body { font-family: system-ui, "Microsoft YaHei", sans-serif; max-width: 46rem;
                     margin: 3rem auto; padding: 0 1.5rem; line-height: 1.7; color: #24292f; }
              h1 { font-size: 1.4rem; }
              h2 { font-size: 1.1rem; margin-top: 2rem; }
              .muted { color: #6a737d; font-size: .9rem; font-weight: normal; }
              pre { font-family: Consolas, "Courier New", monospace; background: #f6f8fa;
                    border-radius: 6px; padding: 1em; overflow-x: auto; white-space: pre-wrap; }
            </style>
            </head>
            <body>
            <h1>捉影 MCP 服务器 <span class="muted">v{{AppVersion.Display}}</span></h1>
            <p>MCP（Streamable HTTP）端点：<b>POST http://127.0.0.1:{{Port}}/mcp</b>，仅绑定本机回环地址。</p>
            <h2>Claude Code 配置（.mcp.json）</h2>
            <pre>{ "mcpServers": { "zhuoying": { "type": "http", "url": "http://127.0.0.1:{{Port}}/mcp" } } }</pre>
            <h2>使用说明</h2>
            <p class="muted">以下内容与 initialize 响应的 instructions 字段同源，连接后模型会自动读到。</p>
            <pre>{{McpProtocol.Instructions}}</pre>
            </body>
            </html>
            """;
        return Encoding.UTF8.GetBytes(html);
    }

    private static void Respond(NetworkStream stream, string status, byte[]? body,
        bool keepAlive, string extraHeaders = "", string contentType = "application/json")
    {
        var header = $"HTTP/1.1 {status}\r\n"
            + (body != null ? $"Content-Type: {contentType}\r\n" : "")
            + $"Content-Length: {body?.Length ?? 0}\r\n"
            + $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n"
            + extraHeaders
            + "\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (body != null)
            stream.Write(body, 0, body.Length);
        stream.Flush();
    }
}
