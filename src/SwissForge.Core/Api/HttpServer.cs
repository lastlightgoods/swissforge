using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SwissForge.Core.Api
{
    /// <summary>
    /// A small HTTP/1.1 server built directly on <see cref="TcpListener"/>.
    /// <para>
    /// Two reasons it is not <c>HttpListener</c>: HttpListener needs a URL ACL registration
    /// on Windows, which means an elevated <c>netsh http add urlacl</c> before the add-in
    /// will start — a non-starter on a locked-down shop PC. And it is not ASP.NET Core,
    /// because this code has to load into ESPRIT's .NET Framework process without dragging a
    /// hosting stack in with it.
    /// </para>
    /// <para>
    /// It binds to the loopback interface by default and requires a bearer token on
    /// everything except <c>/health</c>. Binding to a real interface is possible but
    /// deliberately requires the caller to say so explicitly.
    /// </para>
    /// </summary>
    public sealed class HttpServer : IDisposable
    {
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>Port to listen on. 0 asks the OS for a free one.</summary>
        public int Port { get; private set; }

        /// <summary>Address bound to. Loopback unless deliberately changed.</summary>
        public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

        /// <summary>Required bearer token. Null or empty disables authentication.</summary>
        public string AuthToken { get; set; }

        /// <summary>Largest request body accepted, bytes. Keeps a stray upload from eating memory.</summary>
        public int MaxBodyBytes { get; set; } = 8 * 1024 * 1024;

        /// <summary>Seconds to wait for a client to finish sending.</summary>
        public int ReceiveTimeoutSeconds { get; set; } = 15;

        /// <summary>The routing table.</summary>
        public Func<HttpRequest, HttpResponse> Handler { get; set; }

        /// <summary>Raised for each request once handled. Used for logging.</summary>
        public Action<HttpRequest, HttpResponse, double> RequestCompleted { get; set; }

        /// <summary>Raised when a connection fails in a way the caller may want to know about.</summary>
        public Action<Exception> Fault { get; set; }

        public bool IsRunning => _running;

        public string BaseUrl => $"http://{BindAddress}:{Port}";

        public void Start(int port = 0)
        {
            if (_running) throw new InvalidOperationException("Server is already running.");

            _listener = new TcpListener(BindAddress, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;

            _thread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "SwissForge HTTP"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* shutting down */ }
            try { _thread?.Join(2000); } catch { /* shutting down */ }
            _listener = null;
            _thread = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException) { if (!_running) return; continue; }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex) { Fault?.Invoke(ex); continue; }

                var c = client;
                ThreadPool.QueueUserWorkItem(_ => HandleClient(c));
            }
        }

        private void HandleClient(TcpClient client)
        {
            var started = DateTime.UtcNow;
            HttpRequest request = null;
            HttpResponse response;

            try
            {
                client.ReceiveTimeout = ReceiveTimeoutSeconds * 1000;
                client.SendTimeout = ReceiveTimeoutSeconds * 1000;

                using (var stream = client.GetStream())
                {
                    request = ReadRequest(stream, out var readError);

                    if (request == null)
                        response = HttpResponse.Error(400, "bad_request", readError ?? "Could not read the request.");
                    else if (!Authorize(request))
                        response = HttpResponse.Error(401, "unauthorized",
                            "Supply the SwissForge API token as 'Authorization: Bearer <token>'.");
                    else
                    {
                        try
                        {
                            response = Handler?.Invoke(request)
                                       ?? HttpResponse.Error(500, "no_handler", "No request handler is configured.");
                        }
                        catch (Exception ex)
                        {
                            // A handler fault must not take down the server, and must never take
                            // down ESPRIT. Report it and carry on.
                            Fault?.Invoke(ex);
                            response = HttpResponse.Error(500, "handler_error", ex.Message);
                        }
                    }

                    var bytes = response.ToBytes();
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                Fault?.Invoke(ex);
                return;
            }
            finally
            {
                try { client.Close(); } catch { /* already gone */ }
            }

            RequestCompleted?.Invoke(request, response, (DateTime.UtcNow - started).TotalMilliseconds);
        }

        private bool Authorize(HttpRequest request)
        {
            if (string.IsNullOrEmpty(AuthToken)) return true;
            if (request.Path == "/health") return true;

            var header = request.Header("Authorization");
            if (string.IsNullOrEmpty(header)) return false;

            const string prefix = "Bearer ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            return FixedTimeEquals(header.Substring(prefix.Length).Trim(), AuthToken);
        }

        /// <summary>Comparison that does not leak the token one character at a time.</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private HttpRequest ReadRequest(NetworkStream stream, out string error)
        {
            error = null;
            var headerBytes = new List<byte>(2048);
            var one = new byte[1];
            int consecutiveNewlines = 0;

            // Read byte-by-byte to the end of the headers. Requests are small; this keeps the
            // body boundary exact without buffering games.
            while (headerBytes.Count < 64 * 1024)
            {
                int read = stream.Read(one, 0, 1);
                if (read <= 0) break;

                headerBytes.Add(one[0]);

                if (one[0] == (byte)'\n')
                {
                    consecutiveNewlines++;
                    if (consecutiveNewlines == 2) break;
                }
                else if (one[0] != (byte)'\r')
                {
                    consecutiveNewlines = 0;
                }
            }

            if (headerBytes.Count == 0) { error = "Empty request."; return null; }

            var headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
            var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0) { error = "Malformed request."; return null; }

            var parts = lines[0].Split(' ');
            if (parts.Length < 2) { error = "Malformed request line."; return null; }

            var request = new HttpRequest
            {
                Method = parts[0].ToUpperInvariant(),
                RawTarget = parts[1]
            };

            int q = request.RawTarget.IndexOf('?');
            if (q >= 0)
            {
                request.Path = UrlCodec.Decode(request.RawTarget.Substring(0, q));
                UrlCodec.ParseQuery(request.RawTarget.Substring(q + 1), request.Query);
            }
            else
            {
                request.Path = UrlCodec.Decode(request.RawTarget);
            }

            request.Segments = request.Path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            // Honour "Expect: 100-continue". Clients including HttpWebRequest send it by default
            // on POST and will not transmit the body until the interim response arrives, so a
            // server that ignores it simply hangs until the client times out.
            var expect = request.Header("Expect");
            if (!string.IsNullOrEmpty(expect) &&
                expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(interim, 0, interim.Length);
                stream.Flush();
            }

            var lengthHeader = request.Header("Content-Length");
            if (!string.IsNullOrEmpty(lengthHeader) &&
                int.TryParse(lengthHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) &&
                length > 0)
            {
                if (length > MaxBodyBytes)
                {
                    error = $"Body of {length} bytes exceeds the {MaxBodyBytes} byte limit.";
                    return null;
                }

                var body = new byte[length];
                int got = 0;
                while (got < length)
                {
                    int read = stream.Read(body, got, length - got);
                    if (read <= 0) break;
                    got += read;
                }

                if (got < length) { error = "Request body was shorter than Content-Length declared."; return null; }
                request.Body = Encoding.UTF8.GetString(body);
            }

            return request;
        }

        public void Dispose() => Stop();
    }
}
