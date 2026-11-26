using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Beacon.Services;

public sealed record HttpRequestData
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string RemoteAddress { get; init; } = "";
}

public sealed record HttpResponse(int StatusCode, string Body, string ContentType = "application/json; charset=utf-8")
{
    public static HttpResponse Json(int status, object payload) => new(
        status,
        JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        "application/json; charset=utf-8");
}

/// <summary>
/// A tiny dependency-free HTTP/1.1 server. A raw TcpListener is used instead of
/// HttpListener so the app can listen on 0.0.0.0 without admin rights or URL ACLs.
/// </summary>
public sealed class HttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public event Func<HttpRequestData, Task<HttpResponse>>? RequestReceived;

    public bool IsRunning => _listener is not null;
    public IPAddress Address { get; private set; } = IPAddress.Any;
    public int Port { get; private set; }

    public void Start(IPAddress address, int port)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(address, port);
        _listener.Start();
        Address = address;
        Port = port;
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts!.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _acceptTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _listener = null;
        _acceptTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception)
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.ReceiveTimeout = 20_000;
            client.SendTimeout = 20_000;
            using var stream = client.GetStream();
            var raw = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
            var request = ParseRequest(raw, client);
            var response = RequestReceived is not null
                ? await RequestReceived(request).ConfigureAwait(false)
                : HttpResponse.Json(503, new { ok = false, error = "No handler registered" });
            await WriteResponseAsync(stream, response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            try
            {
                using var stream = client.GetStream();
                await WriteResponseAsync(stream, HttpResponse.Json(400, new { ok = false, error = ex.Message }), ct).ConfigureAwait(false);
            }
            catch { /* connection gone */ }
        }
        finally
        {
            try { client.Close(); } catch { /* ignore */ }
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var status = ReasonPhrase(response.StatusCode);
        var header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(status).Append("\r\n");
        header.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        header.Append("Cache-Control: no-store\r\n");
        header.Append("Access-Control-Allow-Origin: *\r\n");
        header.Append("Access-Control-Allow-Headers: *\r\n");
        header.Append("Access-Control-Allow-Methods: GET, POST, PUT, OPTIONS\r\n");
        header.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            if (n == 0) break;
            ms.Write(buffer, 0, n);
            headerEnd = FindHeaderEnd(ms.GetBuffer(), (int)ms.Length);
            if (ms.Length > 64 * 1024) throw new InvalidDataException("Request headers are too large.");
        }

        if (headerEnd < 0) return ms.ToArray();

        var headerText = Encoding.ASCII.GetString(ms.GetBuffer(), 0, headerEnd);
        var headers = ParseHeaders(headerText);

        // A conforming client waits for this before sending the body.
        if (headers.TryGetValue("Expect", out var expect) &&
            expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            await stream.WriteAsync(interim, 0, interim.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        var chunked = headers.TryGetValue("Transfer-Encoding", out var te) &&
                      te.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var cl) &&
            int.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            contentLength = parsed;

        if (contentLength > 12 * 1024 * 1024) throw new InvalidDataException("Request body is too large.");

        while (true)
        {
            var bodyBytes = (int)ms.Length - (headerEnd + 4);
            if (chunked)
            {
                if (EndsWithZeroChunk(ms.GetBuffer(), headerEnd + 4, (int)ms.Length)) break;
            }
            else if (bodyBytes >= contentLength)
            {
                break;
            }

            var n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            if (n == 0) break;
            ms.Write(buffer, 0, n);
            if (ms.Length > 16 * 1024 * 1024) throw new InvalidDataException("Request body is too large.");
        }

        return ms.ToArray();
    }

    private static HttpRequestData ParseRequest(byte[] raw, TcpClient client)
    {
        var headerEnd = FindHeaderEnd(raw, raw.Length);
        if (headerEnd < 0) throw new InvalidDataException("Malformed request (no header terminator).");

        var text = Encoding.UTF8.GetString(raw);
        var lines = text[..headerEnd].Split("\r\n", StringSplitOptions.None);
        var requestLine = lines.Length > 0 ? lines[0].Split(' ', 3) : Array.Empty<string>();
        var method = requestLine.Length > 0 ? requestLine[0].ToUpperInvariant() : "GET";
        var target = requestLine.Length > 1 ? requestLine[1] : "/";

        var headers = ParseHeaders(string.Join("\r\n", lines.Skip(1)));
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = target;
        var question = target.IndexOf('?');
        if (question >= 0)
        {
            path = target[..question];
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]).Replace('+', ' ');
                query[key] = value;
            }
        }

        var bodyOffset = headerEnd + 4;
        var body = bodyOffset <= raw.Length ? Encoding.UTF8.GetString(raw, bodyOffset, raw.Length - bodyOffset) : "";
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            body = DecodeChunked(raw, bodyOffset);

        return new HttpRequestData
        {
            Method = method,
            Path = SafeUrlDecode(path),
            Query = query,
            Headers = headers,
            Body = body,
            ContentType = headers.GetValueOrDefault("Content-Type") ?? "",
            RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? ""
        };
    }

    private static string SafeUrlDecode(string value)
    {
        try { return Uri.UnescapeDataString(value); } catch { return value; }
    }

    private static Dictionary<string, string> ParseHeaders(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            result[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return result;
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 3; i < length; i++)
            if (data[i - 3] == 13 && data[i - 2] == 10 && data[i - 1] == 13 && data[i] == 10)
                return i - 3;
        return -1;
    }

    private static bool EndsWithZeroChunk(byte[] data, int offset, int length)
    {
        // Chunked bodies end with: 0\r\n\r\n (optionally followed by trailers).
        for (var i = length - 5; i >= offset; i--)
        {
            if (data[i] == (byte)'0' && data[i + 1] == 13 && data[i + 2] == 10 &&
                data[i + 3] == 13 && data[i + 4] == 10) return true;
            if (length - i > 64) break;
        }
        return false;
    }

    private static string DecodeChunked(byte[] data, int offset)
    {
        // Decode once at the end: a UTF-8 sequence may be split across chunk
        // boundaries, and per-chunk decoding would corrupt it.
        using var body = new MemoryStream();
        var pos = offset;
        while (pos < data.Length)
        {
            var lineEnd = IndexOfSequence(data, pos, "\r\n");
            if (lineEnd < 0) break;
            var sizeText = Encoding.ASCII.GetString(data, pos, lineEnd - pos).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size <= 0)
                break;
            pos = lineEnd + 2;
            if (pos + size > data.Length) break;
            body.Write(data, pos, size);
            pos += size + 2;
        }
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static int IndexOfSequence(byte[] data, int start, string sequence)
    {
        for (var i = start; i + sequence.Length <= data.Length; i++)
        {
            var match = true;
            for (var j = 0; j < sequence.Length; j++)
                if (data[i + j] != (byte)sequence[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static string ReasonPhrase(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Response"
    };

    public void Dispose() => Stop();
}
