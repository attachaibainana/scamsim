using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ScamSim.LLM
{
    public class HttpResult
    {
        /// <summary>HTTP status code, or 0 when no response arrived.</summary>
        public int Status;
        public string Body = "";
        /// <summary>Set when the request never got a response (DNS, offline, TLS...).</summary>
        public string NetworkError;
        public bool TimedOut;
    }

    /// <summary>One HTTP POST. Swapped for a fake in tests, so no test ever calls the real API.</summary>
    public interface IHttpTransport
    {
        IEnumerator Post(string url, IDictionary<string, string> headers, string body, int timeoutSeconds,
            Action<HttpResult> onDone);
    }

    /// <summary>
    /// UnityWebRequest transport: the only HTTP client that runs on every Unity target, WebGL included
    /// (PRD 2.2). It yields null every frame instead of yielding the request operation, so whoever
    /// drives it keeps control while the request is in flight; disposing the coroutine early aborts it.
    /// </summary>
    public class UnityHttpTransport : IHttpTransport
    {
        public IEnumerator Post(string url, IDictionary<string, string> headers, string body, int timeoutSeconds,
            Action<HttpResult> onDone)
        {
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                foreach (KeyValuePair<string, string> header in headers) request.SetRequestHeader(header.Key, header.Value);
                request.timeout = timeoutSeconds;

                float started = Time.realtimeSinceStartup;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                try
                {
                    while (!operation.isDone) yield return null;
                }
                finally
                {
                    if (!request.isDone) request.Abort();
                }
                float elapsed = Time.realtimeSinceStartup - started;

                var result = new HttpResult
                {
                    Status = (int)request.responseCode,
                    Body = request.downloadHandler != null ? request.downloadHandler.text ?? "" : ""
                };

                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    string error = request.error ?? "connection error";
                    result.NetworkError = error;
                    result.TimedOut = error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                                      || error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                                      || elapsed >= timeoutSeconds - 0.5f;
                }

                onDone(result);
            }
        }
    }
}
