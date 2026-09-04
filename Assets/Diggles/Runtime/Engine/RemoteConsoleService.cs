#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using UnityEngine;

    /// <summary>
    /// Config-gated, loopback-only bridge for driving the live Tcl interpreter from
    /// external development tools.
    /// </summary>
    public sealed class RemoteConsoleService : MonoBehaviour
    {
        private enum MainThreadCommandKind
        {
            Eval,
            Screenshot,
        }

        private sealed class MainThreadWorkItem
        {
            public MainThreadWorkItem(MainThreadCommandKind kind, string argument)
            {
                Kind = kind;
                Argument = argument;
            }

            public MainThreadCommandKind Kind { get; }
            public string Argument { get; }
            public ManualResetEventSlim Completion { get; } = new(false);
            public string? Response { get; set; }
        }

        private const int LogCapacity = 2000;
        private const int AcceptRetryDelayMilliseconds = 50;
        private const int AcceptThreadJoinMilliseconds = 250;
        private const int ClientThreadJoinMilliseconds = 50;
        private const string LivenessToken = "pong";

        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static RemoteConsoleService? s_instance;

        // True while at least one remote client is attached. Read by TclDebugConsole to keep
        // the "@mouse" terrain sampling alive for remote spawn recipes even when the in-game
        // console overlay is closed.
        public static bool AnyClient
        {
            get
            {
                var instance = s_instance;
                if (instance == null) return false;
                lock (instance._clientsGate) return instance._clients.Count > 0;
            }
        }

        private readonly ConcurrentQueue<MainThreadWorkItem> _mainThreadWork = new();
        private readonly object _logGate = new();
        private readonly Queue<string> _logLines = new(LogCapacity);
        private readonly object _clientsGate = new();
        private readonly List<TcpClient> _clients = new();
        private readonly List<Thread> _clientThreads = new();

        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private int _shutdownStarted;
        private bool _logSubscribed;

        /// <summary>
        /// Create the persistent service host when play mode and configuration enable it.
        /// Repeated calls are safe.
        /// </summary>
        public static void Attach()
        {
            if (!Application.isPlaying || s_instance != null) return;

            int port = DigglesConfig.ResolveRemoteConsolePort(0);
            if (port <= 0)
            {
                Debug.Log("Remote console is disabled; set remoteConsolePort to a positive value to enable it.");
                return;
            }

            var go = new GameObject("RemoteConsoleService") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            var instance = go.AddComponent<RemoteConsoleService>();
            s_instance = instance;
            if (instance.StartService(port)) return;

            s_instance = null;
            Destroy(go);
        }

        private bool StartService(int port)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                Application.logMessageReceived += HandleLogMessage;
                _logSubscribed = true;
                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "Diggles remote console accept loop",
                };
                _acceptThread.Start();
                return true;
            }
            catch (Exception exception)
            {
                StopService();
                Debug.LogWarning($"Remote console could not start on 127.0.0.1:{port}: {exception.Message}");
                return false;
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpListener? listener = _listener;
                    if (listener == null) return;

                    TcpClient client = listener.AcceptTcpClient();
                    if (!_running)
                    {
                        CloseClient(client);
                        return;
                    }

                    client.NoDelay = true;
                    var clientThread = new Thread(() => ClientLoop(client))
                    {
                        IsBackground = true,
                        Name = "Diggles remote console client",
                    };
                    lock (_clientsGate)
                    {
                        if (!_running)
                        {
                            CloseClient(client);
                            return;
                        }
                        _clients.Add(client);
                        _clientThreads.Add(clientThread);
                    }

                    try
                    {
                        clientThread.Start();
                    }
                    catch
                    {
                        lock (_clientsGate)
                        {
                            _clients.Remove(client);
                            _clientThreads.Remove(clientThread);
                        }
                        CloseClient(client);
                    }
                }
                catch (SocketException)
                {
                    if (!_running) return;
                    Thread.Sleep(AcceptRetryDelayMilliseconds);
                }
                catch (ObjectDisposedException)
                {
                    if (!_running) return;
                    Thread.Sleep(AcceptRetryDelayMilliseconds);
                }
                catch (InvalidOperationException)
                {
                    if (!_running) return;
                    Thread.Sleep(AcceptRetryDelayMilliseconds);
                }
                catch
                {
                    if (!_running) return;
                    Thread.Sleep(AcceptRetryDelayMilliseconds);
                }
            }
        }

        private void ClientLoop(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, Utf8, false, 4096, true))
                using (var writer = new StreamWriter(stream, Utf8, 4096, true) { NewLine = "\n", AutoFlush = true })
                {
                    while (_running)
                    {
                        string? request = reader.ReadLine();
                        if (request == null) return;

                        string response;
                        try
                        {
                            response = HandleRequest(request);
                        }
                        catch (Exception exception)
                        {
                            response = ErrorResponse(ExceptionMessage(exception));
                        }
                        writer.WriteLine(response);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch
            {
            }
            finally
            {
                CloseClient(client);
                lock (_clientsGate)
                {
                    _clients.Remove(client);
                    _clientThreads.Remove(Thread.CurrentThread);
                }
            }
        }

        private string HandleRequest(string request)
        {
            int separator = request.IndexOf(' ');
            string verb = separator < 0 ? request : request.Substring(0, separator);
            string argument = separator < 0 ? "" : request.Substring(separator + 1);

            switch (verb)
            {
                case "PING":
                    return SuccessResponse(LivenessToken);
                case "LOG":
                    return HandleLogRequest(argument);
                case "EVAL":
                    return RunOnMainThread(MainThreadCommandKind.Eval, argument);
                case "EVALB64":
                    try
                    {
                        string tcl = Utf8.GetString(Convert.FromBase64String(argument));
                        return RunOnMainThread(MainThreadCommandKind.Eval, tcl);
                    }
                    catch (Exception exception)
                    {
                        return ErrorResponse(ExceptionMessage(exception));
                    }
                case "SCREENSHOT":
                    if (string.IsNullOrWhiteSpace(argument) || !Path.IsPathRooted(argument))
                        return ErrorResponse("absolute path required");
                    return RunOnMainThread(MainThreadCommandKind.Screenshot, argument);
                default:
                    return ErrorResponse("unknown command");
            }
        }

        private string HandleLogRequest(string argument)
        {
            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineCount)
                || lineCount < 0)
            {
                return ErrorResponse("invalid log count");
            }
            return SuccessResponse(GetLogTail(lineCount));
        }

        private string RunOnMainThread(MainThreadCommandKind kind, string argument)
        {
            if (!_running) return ErrorResponse("service stopping");

            var workItem = new MainThreadWorkItem(kind, argument);
            _mainThreadWork.Enqueue(workItem);
            while (!workItem.Completion.Wait(100))
            {
                if (!_running) return ErrorResponse("service stopping");
            }
            return workItem.Response ?? ErrorResponse("main thread command failed");
        }

        private void Update()
        {
            while (_mainThreadWork.TryDequeue(out MainThreadWorkItem workItem))
            {
                string response;
                try
                {
                    if (!_running)
                    {
                        response = ErrorResponse("service stopping");
                    }
                    else if (workItem.Kind == MainThreadCommandKind.Eval)
                    {
                        response = SuccessResponse(TclDebugConsole.EvalExternal(workItem.Argument));
                    }
                    else
                    {
                        ScreenCapture.CaptureScreenshot(workItem.Argument);
                        response = SuccessResponse(workItem.Argument);
                    }
                }
                catch (Exception exception)
                {
                    response = ErrorResponse(ExceptionMessage(exception));
                }

                workItem.Response = response;
                workItem.Completion.Set();
            }
        }

        private void HandleLogMessage(string condition, string stackTrace, LogType type)
        {
            AppendLogText(condition);
            if ((type == LogType.Error || type == LogType.Exception)
                && !string.IsNullOrWhiteSpace(stackTrace))
            {
                AppendLogText(stackTrace);
            }
        }

        private void AppendLogText(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            lock (_logGate)
            {
                foreach (string line in lines)
                {
                    while (_logLines.Count >= LogCapacity) _logLines.Dequeue();
                    _logLines.Enqueue(line);
                }
            }
        }

        private string GetLogTail(int requestedLineCount)
        {
            lock (_logGate)
            {
                if (requestedLineCount == 0 || _logLines.Count == 0) return "";

                string[] lines = _logLines.ToArray();
                int lineCount = Math.Min(requestedLineCount, lines.Length);
                return string.Join("\n", lines, lines.Length - lineCount, lineCount);
            }
        }

        private static string SuccessResponse(string payload)
        {
            return "OK " + Convert.ToBase64String(Utf8.GetBytes(payload));
        }

        private static string ErrorResponse(string message)
        {
            return "ERR " + Convert.ToBase64String(Utf8.GetBytes(message));
        }

        private static string ExceptionMessage(Exception exception)
        {
            return string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message;
        }

        private static void CloseClient(TcpClient client)
        {
            try { client.Close(); }
            catch { }
        }

        private void StopService()
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;

            _running = false;
            if (_logSubscribed)
            {
                try { Application.logMessageReceived -= HandleLogMessage; }
                catch { }
                _logSubscribed = false;
            }

            try { _listener?.Stop(); }
            catch { }

            TcpClient[] clients;
            Thread[] clientThreads;
            lock (_clientsGate)
            {
                clients = _clients.ToArray();
                clientThreads = _clientThreads.ToArray();
            }
            foreach (TcpClient client in clients) CloseClient(client);

            while (_mainThreadWork.TryDequeue(out MainThreadWorkItem workItem))
            {
                workItem.Response = ErrorResponse("service stopping");
                workItem.Completion.Set();
            }

            Thread? acceptThread = _acceptThread;
            if (acceptThread != null && acceptThread != Thread.CurrentThread)
            {
                try { acceptThread.Join(AcceptThreadJoinMilliseconds); }
                catch { }
            }
            foreach (Thread clientThread in clientThreads)
            {
                if (clientThread == Thread.CurrentThread) continue;
                try { clientThread.Join(ClientThreadJoinMilliseconds); }
                catch { }
            }

            lock (_clientsGate)
            {
                _clients.Clear();
                _clientThreads.Clear();
            }
            _listener = null;
            _acceptThread = null;
        }

        private void OnApplicationQuit()
        {
            StopService();
        }

        private void OnDestroy()
        {
            StopService();
            if (s_instance == this) s_instance = null;
        }
    }
}
