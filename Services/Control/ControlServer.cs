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
    /// </summary>
    public sealed class ControlServer : IDisposable
    {
        private const int MaxMessageBytes = 1024 * 1024;
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
            AppDiagnostics.Write("control server stopped");
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    var security = new PipeSecurity();
                    var user = WindowsIdentity.GetCurrent().User;
                    security.AddAccessRule(new PipeAccessRule(
                        user, PipeAccessRights.FullControl, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        PipeAccessRights.FullControl, AccessControlType.Allow));

                    using (var server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None,
                        0, 0, security))
                    {
                        _current = server;
                        server.WaitForConnection();
                        Handle(server);
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

        private void Handle(NamedPipeServerStream server)
        {
            string line;
            using (var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true))
                line = reader.ReadLine();
            if (line == null || line.Length > MaxMessageBytes) return;

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
