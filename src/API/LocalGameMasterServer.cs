using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LothbrokAI.Core;
using Newtonsoft.Json;
using TaleWorlds.Core;

namespace LothbrokAI.API
{
    /// <summary>
    /// Phase 8 REST API Server.
    /// Exposes a local HttpListener to allow external agents (like Python CLIs or MCP tools)
    /// to query the living game state and optionally push actions back onto the main thread.
    /// </summary>
    public static class LocalGameMasterServer
    {
        private static HttpListener _listener;
        private static CancellationTokenSource _cts;
        private static bool _isRunning = false;

        // Thread-safe queue for incoming actions from external agents.
        // MUST be processed on the Unity/TaleWorlds Main Thread.
        public static ConcurrentQueue<string> PendingActions = new ConcurrentQueue<string>();

        public static void StartServer(int port = 8080)
        {
            if (_isRunning) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/api/");
                _listener.Start();

                _cts = new CancellationTokenSource();
                _isRunning = true;

                // Fire and forget the listening loop onto a background threadpool thread
                Task.Run(() => ListenLoop(_cts.Token));
                
                LothbrokSubModule.Log($"Game Master REST API started on port {port}.", TaleWorlds.Library.Debug.DebugColor.Green);
            }
            catch (Exception ex)
            {
                LothbrokSubModule.LogError("LocalServerFailed", ex);
            }
        }

        public static void StopServer()
        {
            if (!_isRunning) return;

            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
            _isRunning = false;
            
            LothbrokSubModule.Log("Game Master REST API stopped.");
        }

        private static async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    // Wait for a request asynchronously
                    HttpListenerContext context = await _listener.GetContextAsync();
                    await ProcessRequestAsync(context);
                }
                catch (HttpListenerException)
                {
                    // Expected when listener is stopped
                    break;
                }
                catch (Exception ex)
                {
                    LothbrokSubModule.LogError("ListenLoopError", ex);
                }
            }
        }

        private static async Task ProcessRequestAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            // CORS setup for web-based frontends
            response.AppendHeader("Access-Control-Allow-Origin", "*");
            response.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.AppendHeader("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            string responseString = "";
            int statusCode = 200;
            string contentType = "application/json";

            try
            {
                // ===================================
                // ROUTE: GET /api/graph
                // ===================================
                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/api/graph")
                {
                    // Generate graph JSON but don't save to disk
                    responseString = CalradiaGraphExporter.ExportGraph(null);
                }
                // ===================================
                // ROUTE: POST /api/omni-tool
                // ===================================
                else if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/api/omni-tool")
                {
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                        string payload = await reader.ReadToEndAsync();
                        
                        // Queue it for the Main Thread to process (TaleWorlds is not thread-safe)
                        PendingActions.Enqueue(payload);
                        
                        responseString = JsonConvert.SerializeObject(new { status = "queued", payload = payload });
                    }
                }
                else
                {
                    statusCode = 404;
                    responseString = JsonConvert.SerializeObject(new { error = "Route not found. Valid routes: GET /api/graph, POST /api/omni-tool" });
                }
            }
            catch (Exception ex)
            {
                statusCode = 500;
                responseString = JsonConvert.SerializeObject(new { error = ex.Message });
            }
            finally
            {
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.StatusCode = statusCode;
                response.ContentType = contentType;
                response.ContentLength64 = buffer.Length;

                try
                {
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                catch { /* Client disconnected early */ }
                finally
                {
                    response.OutputStream.Close();
                }
            }
        }

        /// <summary>
        /// Called from LothbrokSubModule.OnApplicationTick()
        /// Pops queued instructions and executes them natively.
        /// </summary>
        public static void ProcessQueuedMainThreadActions()
        {
            if (PendingActions.TryDequeue(out string rawPayload))
            {
                LothbrokSubModule.Log($"Executing Async Command: {rawPayload}", TaleWorlds.Library.Debug.DebugColor.Yellow);
                // Future expansion: Pass to ActionEngine.ProcessAction()
                // InformationManager.ShowInquiry(new InquiryData("AI Command Received", rawPayload, true, false, "Acknowledge", "", null, null));
            }
        }
    }
}
