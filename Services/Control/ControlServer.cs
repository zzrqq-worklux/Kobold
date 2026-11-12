using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Kobold.Core;
using Kobold.Core.Control;
using Newtonsoft.Json;

namespace Kobold.Services.Control
{
    /// <summary>
    /// Local-only JSON-line control pipe. One client connection carries exactly
    /// one request line and gets exactly one response line back. The pipe ACL is
    /// restricted to the current user (plus LocalSystem), and every handler runs
    /// through the supplied dispatcher so config and widgets are only touched on
    /// the UI thread.
    ///
    /// A client that connects and never speaks, or that stalls mid-request, must
    /// not take the server down with it: the accept loop waits a bounded time for
    /// a connection, and each request is served on a worker with a deadline. The
    /// message limit is enforced on bytes while the line is being read, so an
    /// oversized request is rejected before it can grow the buffer.
    /// </summary>
    public sealed class ControlServer : IDisposable
    {
        private const int MaxMessageBytes = 1024 * 1024;
        private const int ConnectionTimeoutMs = 15000;
        private const int RequestTimeoutMs = 30000;
        private const int StopJoinMs = 2000;

        private readonly string _pipeName;
        private readonly Action<Action> _dispatch;
        private readonly Func<ControlRequest, ControlResponse> _handler;
        private Thread _thread;
        private volatile bool _running;
        private volatile NamedPipeServerStream _current;

        public ControlServer(string pipeName, Action<Action> dispatch,
            Func<ControlRequest, ControlResponse> handler)
        {
            _pipeName = pipeName;
            _dispatch = dispatch ?? (action => action());
            _handler = handler;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Kobold.Control" };
            _thread.Start();
            AppDiagnostics.Write("control server started pipe=" + _pipeName);
        }

        public void Stop()
        {
            _running = false;
            try { _current?.Dispose(); } catch { }

            Thread thread = _thread;
            _thread = null;
            if (thread != null && thread != Thread.CurrentThread)
            {
                try { thread.Join(StopJoinMs); } catch (ThreadStateException) { }
            }

            AppDiagnostics.Write("control server stopped");
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    using (var server = CreateServer())
                    {
                        _current = server;
                        if (!WaitForConnection(server)) continue;
                        Serve(server);
                    }
                }
                catch (Exception ex)
                {
                    if (_running) AppDiagnostics.Write("control pipe error: " + ex.Message);
                }
                finally
                {
                    _current = null;
                }
            }
        }

        private NamedPipeServerStream CreateServer()
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(
                user, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            return new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.None,
                0, 0, security);
        }

        /// <summary>
        /// Waits for one client with a deadline. A connect that never completes
        /// (a stuck client, or Stop() disposing the stream) leaves the loop free
        /// to accept the next one instead of blocking the pipe forever.
        /// </summary>
        private bool WaitForConnection(NamedPipeServerStream server)
        {
            try
            {
                return server.WaitForConnectionAsync().Wait(ConnectionTimeoutMs);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Serves one request on a worker thread and gives up after
        /// <see cref="RequestTimeoutMs"/>. Disposing the stream unblocks a read
        /// that is stuck on a silent client; the loop then starts over.
        /// </summary>
        private void Serve(NamedPipeServerStream server)
        {
            var worker = new Thread(() =>
            {
                try
                {
                    Handle(server);
                }
                catch (Exception ex)
                {
                    if (_running) AppDiagnostics.Write("control pipe error: " + ex.Message);
                }
            })
            {
                IsBackground = true,
                Name = "Kobold.Control.Request"
            };

            worker.Start();
            if (!worker.Join(RequestTimeoutMs))
            {
                AppDiagnostics.Write("control pipe: request timed out after "
                    + RequestTimeoutMs + " ms; dropping the connection");
                try { server.Dispose(); } catch { }
            }
        }

        private void Handle(NamedPipeServerStream server)
        {
            string line = ReadRequestLine(server);
            if (line == null)
            {
                // Closed, oversized or empty request. The client may already be
                // gone; Write() only fails for a dead pipe and is caught by the
                // caller, so an oversized request still gets a proper answer.
                TryWrite(server, Error(null, ControlErrorCodes.InvalidRequest,
                    "The request line is empty, too large or the connection closed."));
                return;
            }

            ControlRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<ControlRequest>(
                    line, ControlJson.Settings);
            }
            catch (Exception)
            {
                Write(server, Error(null, ControlErrorCodes.InvalidRequest, "Invalid JSON."));
                return;
            }

            ControlResponse response;
            try
            {
                // The real app dispatches on the UI thread and blocks this
                // connection until the dispatcher finishes.
                ControlResponse captured = null;
                _dispatch(() => captured = _handler(request));
                response = captured;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("control handler error: " + ex.Message);
                response = Error(request, ControlErrorCodes.Internal, ex.Message);
            }

            if (response == null) response = Error(request, ControlErrorCodes.Internal, "No response.");
            Write(server, response);

            // A restart response must reach the client before the app exits, so
            // the shutdown action runs only once the line is on the wire.
            Action afterWrite = response.AfterWrite;
            if (afterWrite == null) return;
            try
            {
                afterWrite();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("control after-write action failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Reads one newline-terminated line, enforcing the size cap on UTF-8
        /// bytes while reading. Returns null for an empty line, an oversized
        /// line or a closed connection.
        /// </summary>
        private static string ReadRequestLine(Stream stream)
        {
            var chunk = new byte[4096];
            using (var message = new MemoryStream())
            {
                while (true)
                {
                    int read = stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0) return null;

                    int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
                    int take = newline >= 0 ? newline : read;
                    message.Write(chunk, 0, take);

                    if (message.Length > MaxMessageBytes) return null;
                    if (newline >= 0) break;
                }

                if (message.Length == 0) return null;

                byte[] bytes = message.ToArray();
                int length = bytes.Length;
                while (length > 0 && bytes[length - 1] == (byte)'\r') length--;
                return Encoding.UTF8.GetString(bytes, 0, length);
            }
        }

        private static ControlResponse Error(ControlRequest request, string code, string message)
        {
            return new ControlResponse
            {
                V = ControlProtocol.Version,
                Id = request == null ? null : request.Id,
                Ok = false,
                Error = new ControlError { Code = code, Message = message }
            };
        }

        /// <summary>Writes a response, ignoring a client that already hung up.</summary>
        private static bool TryWrite(NamedPipeServerStream server, ControlResponse response)
        {
            try
            {
                Write(server, response);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Write(NamedPipeServerStream server, ControlResponse response)
        {
            string json = JsonConvert.SerializeObject(response, ControlJson.Settings);
            byte[] bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);
            server.Write(bytes, 0, bytes.Length);
            server.Flush();
        }

        public void Dispose() { Stop(); }
    }
}
