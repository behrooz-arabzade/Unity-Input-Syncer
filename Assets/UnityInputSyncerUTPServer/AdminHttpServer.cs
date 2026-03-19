using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityInputSyncerUTPServer
{
    public class AdminHttpServer : IDisposable
    {
        private const int MaxBodySize = 1_048_576; // 1 MB

        private readonly AdminController controller;
        private readonly HttpListener listener;
        private readonly CancellationTokenSource cts;
        private bool disposed;

        public AdminHttpServer(AdminController controller, AdminHttpServerOptions options = null)
        {
            options = options ?? new AdminHttpServerOptions();
            this.controller = controller;
            cts = new CancellationTokenSource();

            listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{options.Port}/");
        }

        public void Start()
        {
            listener.Start();
            Task.Run(() => ListenLoop(cts.Token));
            Debug.Log($"[AdminHttpServer] Listening on {string.Join(", ", listener.Prefixes)}");
        }

        public void Stop()
        {
            if (disposed) return;
            cts.Cancel();
            listener.Stop();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            listener.Close();
            cts.Dispose();
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                try
                {
                    await HandleContext(context);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AdminHttpServer] Error handling request: {ex}");
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch { }
                }
            }
        }

        private async Task HandleContext(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // Auth check
            var authHeader = request.Headers["Authorization"];
            if (!controller.ValidateAuth(authHeader))
            {
                await WriteResponse(response, 401, "{\"error\":\"Unauthorized\"}");
                return;
            }

            // Read body (capped at 1 MB)
            string body = null;
            if (request.HasEntityBody)
            {
                if (request.ContentLength64 > MaxBodySize)
                {
                    await WriteResponse(response, 413, "{\"error\":\"Payload too large\"}");
                    return;
                }

                var buffer = new char[MaxBodySize + 1];
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    int totalRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                    if (totalRead > MaxBodySize)
                    {
                        await WriteResponse(response, 413, "{\"error\":\"Payload too large\"}");
                        return;
                    }
                    body = new string(buffer, 0, totalRead);
                }
            }

            var result = await controller.HandleRequestAsync(request.HttpMethod, request.Url.AbsolutePath, body);
            await WriteResponse(response, result.StatusCode, result.Body);
        }

        private static async Task WriteResponse(HttpListenerResponse response, int statusCode, string body)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";

            if (!string.IsNullOrEmpty(body))
            {
                var buffer = System.Text.Encoding.UTF8.GetBytes(body);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }

            response.Close();
        }
    }
}
