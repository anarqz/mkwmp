// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mkwmp.Core.Net;

/// <summary>
/// Where the browser comes back after the sign-in on nextendo.network: <c>http://127.0.0.1:&lt;a free port&gt;/callback</c>,
/// open only while a link is under way. As <see cref="LocalApi"/>, a raw <see cref="TcpListener"/> with a minimal
/// HTTP/1.1 reader (HttpListener needs a URL reservation on Windows and is unreliable under Wine). It binds the
/// loopback address only, answers nothing but GET /callback, and takes only the answer that carries this sign-in's
/// <c>state</c>: any other request gets a 404 or 400 and the wait goes on.
/// </summary>
internal sealed class NextendoLoopback : IDisposable
{
    private const int MaxHead = 8 * 1024;
    private readonly TcpListener _listener;

    public NextendoLoopback()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(4);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => NextendoApi.RedirectUri(Port);

    /// <summary>
    /// Waits for the browser's answer to this sign-in. Throws <see cref="TimeoutException"/> after
    /// <paramref name="patience"/> and <see cref="OperationCanceledException"/> when <paramref name="cancel"/> fires.
    /// </summary>
    public async Task<Callback> WaitAsync(string state, TimeSpan patience, CancellationToken cancel)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(patience);
        var stop = limit.Token;
        var answered = new TaskCompletionSource<Callback>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Each connection on its own: a browser that opens one and says nothing on it must not hold up the answer.
            while (!answered.Task.IsCompleted)
            {
                var accept = _listener.AcceptTcpClientAsync(stop).AsTask();
                if (await Task.WhenAny(accept, answered.Task).ConfigureAwait(false) == answered.Task)
                {
                    break;
                }
                var client = await accept.ConfigureAwait(false);
                _ = Task.Run(
                    async () =>
                    {
                        using (client)
                        {
                            var answer = await ServeAsync(client, state, stop).ConfigureAwait(false);
                            if (answer.Kind != CallbackKind.Ignored)
                            {
                                answered.TrySetResult(answer);
                            }
                        }
                    },
                    CancellationToken.None);
            }
            return await answered.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            cancel.ThrowIfCancellationRequested();
            throw new TimeoutException(e.Message, e);
        }
        finally
        {
            await limit.CancelAsync().ConfigureAwait(false); // the connections still open, and the accept that waits
            limit.Dispose();
        }
    }

    /// <summary>One request of one connection, then it is closed. A client that sends nothing is dropped after five seconds.</summary>
    private static async Task<Callback> ServeAsync(TcpClient client, string state, CancellationToken cancel)
    {
        try
        {
            client.NoDelay = true;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            idle.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = client.GetStream();
            var buffer = new byte[MaxHead];
            var have = 0;
            int headEnd;
            while ((headEnd = buffer.AsSpan(0, have).IndexOf("\r\n\r\n"u8)) < 0)
            {
                if (have == buffer.Length)
                {
                    await WriteAsync(stream, 431, "", idle.Token).ConfigureAwait(false);
                    return default;
                }
                var n = await stream.ReadAsync(buffer.AsMemory(have), idle.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    return default;
                }
                have += n;
            }
            var request = Encoding.Latin1.GetString(buffer, 0, headEnd).Split("\r\n")[0].Split(' ');
            if (request.Length < 3 || request[0] != "GET")
            {
                await WriteAsync(stream, request.Length < 3 ? 400 : 405, "", idle.Token).ConfigureAwait(false);
                return default;
            }
            var target = request[1];
            var answer = Callback.Parse(target, state);
            var onPath = target == NextendoApi.CallbackPath || target.StartsWith(NextendoApi.CallbackPath + "?", StringComparison.Ordinal);
            var (status, page) = answer.Kind switch
            {
                CallbackKind.Code => (200, Page(Strings.NextendoBrowserDoneTitle, Strings.NextendoBrowserDoneText)),
                CallbackKind.Denied => (200, Page(Strings.NextendoBrowserFailedTitle, Strings.NextendoBrowserFailedText)),
                _ => (onPath ? 400 : 404, onPath ? Page(Strings.NextendoBrowserFailedTitle, Strings.NextendoBrowserFailedText) : ""),
            };
            try
            {
                await WriteAsync(stream, status, page, idle.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // the tab was closed before its page arrived: the answer itself is here
            }
            return answer;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return default; // a client that went away, or said nothing, or the wait is over: not the answer
        }
    }

    /// <summary>The page the player sees in the browser once it came back: two lines, no script, nothing from the request.</summary>
    internal static string Page(string title, string text) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        + $"<title>MKWMP</title></head><body style=\"font-family:system-ui,sans-serif;text-align:center;margin-top:18vh\">"
        + $"<h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(text)}</p></body></html>";

    private static async Task WriteAsync(NetworkStream stream, int status, string html, CancellationToken cancel)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} {reason}\r\ncontent-type: text/html; charset=utf-8\r\ncontent-length: {body.Length}\r\ncache-control: no-store\r\nreferrer-policy: no-referrer\r\nconnection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(head);
        var all = new byte[bytes.Length + body.Length];
        bytes.CopyTo(all, 0);
        body.CopyTo(all, bytes.Length);
        await stream.WriteAsync(all, cancel).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Dispose();
    }
}
