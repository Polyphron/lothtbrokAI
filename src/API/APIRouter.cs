using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LothbrokAI.API
{
    /// <summary>
    /// Result from an LLM API call.
    /// </summary>
    public class LLMResponse
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public string Error { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public long LatencyMs { get; set; }
    }

    /// <summary>
    /// Routes LLM requests to the configured backend.
    /// 
    /// DESIGN: All backends use OpenAI-compatible /v1/chat/completions format.
    /// This means OpenRouter, Ollama, LM Studio, DeepSeek, and KoboldCpp 
    /// all work with the same request/response parsing. Only the base URL 
    /// and auth header differ.
    /// 
    /// All calls are synchronous but designed to be called from a background
    /// thread (Phase 5 adds proper async). For now, we call from the game 
    /// thread to keep things simple — responses will cause a brief pause.
    /// </summary>
    public static class APIRouter
    {
        // ================================================================
        // PUBLIC API
        // ================================================================

        /// <summary>
        /// Send a chat completion request to the configured backend.
        /// </summary>
        /// <param name="systemPrompt">System prompt (character instructions, CoT template)</param>
        /// <param name="userMessage">The player's message with context</param>
        /// <returns>LLM response with text and token usage</returns>
        public static LLMResponse SendChatCompletion(string systemPrompt, string userMessage)
        {
            var config = LothbrokConfig.Current;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                LLMResponse response;

                switch (config.Backend.ToLowerInvariant())
                {
                    case "openrouter":
                        response = CallOpenAICompatible(
                            "https://openrouter.ai/api/v1/chat/completions",
                            config.OpenRouterApiKey,
                            config.OpenRouterModel,
                            systemPrompt, userMessage, config);
                        break;

                    case "deepseek":
                        response = CallOpenAICompatible(
                            "https://api.deepseek.com/v1/chat/completions",
                            config.DeepSeekApiKey,
                            config.DeepSeekModel,
                            systemPrompt, userMessage, config);
                        break;

                    case "ollama":
                    case "lmstudio":
                        response = CallOpenAICompatible(
                            config.LocalApiUrl.TrimEnd('/') + "/v1/chat/completions",
                            null, // No API key for local
                            config.LocalModel,
                            systemPrompt, userMessage, config);
                        break;

                    case "koboldcpp":
                        response = CallOpenAICompatible(
                            config.KoboldCppUrl.TrimEnd('/') + "/v1/chat/completions",
                            null,
                            config.LocalModel,
                            systemPrompt, userMessage, config);
                        break;

                    default:
                        return new LLMResponse
                        {
                            Success = false,
                            Error = "Unknown backend: " + config.Backend
                        };
                }

                sw.Stop();
                response.LatencyMs = sw.ElapsedMilliseconds;

                if (response.Success && config.DebugMode)
                {
                    LothbrokSubModule.Log(string.Format(
                        "API: {0} tokens ({1}+{2}) in {3}ms via {4}",
                        response.TotalTokens, response.PromptTokens,
                        response.CompletionTokens, response.LatencyMs,
                        config.Backend));
                }

                return response;
            }
            catch (Exception ex)
            {
                sw.Stop();
                LothbrokSubModule.Log("API ERROR: " + ex.Message,
                    TaleWorlds.Library.Debug.DebugColor.Red);

                return new LLMResponse
                {
                    Success = false,
                    Error = ex.Message,
                    LatencyMs = sw.ElapsedMilliseconds
                };
            }
        }

        /// <summary>
        /// Send an embedding request to the configured embedding backend.
        /// Returns null on failure.
        /// </summary>
        public static float[] GetEmbedding(string text)
        {
            var config = LothbrokConfig.Current;

            if (string.IsNullOrEmpty(config.EmbeddingBackend) ||
                config.EmbeddingBackend == "none")
            {
                return null; // Fallback to TF-IDF
            }

            try
            {
                string url;
                string apiKey = null;

                switch (config.EmbeddingBackend.ToLowerInvariant())
                {
                    case "openrouter":
                        url = "https://openrouter.ai/api/v1/embeddings";
                        apiKey = config.OpenRouterApiKey;
                        break;
                    case "lmstudio":
                        url = config.EmbeddingUrl.TrimEnd('/') + "/v1/embeddings";
                        break;
                    default:
                        return null;
                }

                var requestBody = new JObject
                {
                    ["model"] = config.EmbeddingModel,
                    ["input"] = text
                };

                string responseJson = HttpPost(url, requestBody.ToString(), apiKey);
                var response = JObject.Parse(responseJson);
                var embedding = response["data"][0]["embedding"];
                return embedding.ToObject<float[]>();
            }
            catch (Exception ex)
            {
                if (config.DebugMode)
                {
                    LothbrokSubModule.Log("Embedding failed (falling back to TF-IDF): " + ex.Message,
                        TaleWorlds.Library.Debug.DebugColor.Yellow);
                }
                return null;
            }
        }

        // ================================================================
        // INTERNAL: OpenAI-compatible API call
        // ================================================================

        private static LLMResponse CallOpenAICompatible(
            string url, string apiKey, string model,
            string systemPrompt, string userMessage,
            LothbrokConfig config)
        {
            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = systemPrompt },
                new JObject { ["role"] = "user", ["content"] = userMessage }
            };

            var tools = Core.ToolRegistry.GetAvailableTools();
            int totalPromptTokens = 0, totalCompletionTokens = 0;
            int maxTurns = 3; // Prevent infinite tool loops
            int currentTurn = 0;

            while (currentTurn < maxTurns)
            {
                currentTurn++;

                var requestBody = new JObject
                {
                    ["model"] = model,
                    ["messages"] = messages,
                    ["temperature"] = config.Temperature,
                    ["max_tokens"] = config.MaxResponseTokens,
                    ["tools"] = tools,
                    ["tool_choice"] = "auto"
                };

                string responseJson = HttpPost(url, requestBody.ToString(), apiKey);
                var response = JObject.Parse(responseJson);

                var usage = response["usage"];
                if (usage != null)
                {
                    totalPromptTokens += usage["prompt_tokens"]?.Value<int>() ?? 0;
                    totalCompletionTokens += usage["completion_tokens"]?.Value<int>() ?? 0;
                }

                var choice = response["choices"][0];
                var message = choice["message"];
                string finishReason = choice["finish_reason"]?.ToString();

                // Append the assistant's message to the history
                messages.Add(message.DeepClone());

                if (finishReason == "tool_calls" || message["tool_calls"] != null)
                {
                    // The LLM wants to use a tool
                    var toolCalls = message["tool_calls"] as JArray;
                    if (toolCalls != null)
                    {
                        foreach (var toolCall in toolCalls)
                        {
                            string toolCallId = toolCall["id"].ToString();
                            string functionName = toolCall["function"]["name"].ToString();
                            string arguments = toolCall["function"]["arguments"].ToString();

                            if (config.DebugMode)
                                LothbrokSubModule.Log($"Agent executing tool: {functionName}({arguments})", TaleWorlds.Library.Debug.DebugColor.White);

                            // Execute physical code
                            string toolResult = Core.ToolRegistry.ExecuteTool(functionName, arguments);

                            // Append result
                            messages.Add(new JObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = toolCallId,
                                ["content"] = toolResult
                            });
                        }
                    }
                    // Loop restarts, sending the new history back to the LLM
                    continue;
                }
                else
                {
                    // Normal finish or stopped. Return text.
                    string text = message["content"]?.ToString() ?? "";
                    return new LLMResponse
                    {
                        Success = true,
                        Text = text,
                        PromptTokens = totalPromptTokens,
                        CompletionTokens = totalCompletionTokens,
                        TotalTokens = totalPromptTokens + totalCompletionTokens
                    };
                }
            }

            return new LLMResponse { Success = false, Error = "Exceeded maximum tool loop turns." };
        }

        // ================================================================
        // INTERNAL: HTTP POST
        // ================================================================

        /// <summary>
        /// Simple synchronous HTTP POST. Will be replaced with async in Phase 5.
        /// 
        /// DESIGN: Using WebRequest instead of HttpClient because .NET 4.7.2
        /// ships with the game and HttpClient requires extra references.
        /// </summary>
        private static string HttpPost(string url, string jsonBody, string apiKey)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = LothbrokConfig.Current.TimeoutSeconds * 1000;

            // Auth header
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("Authorization", "Bearer " + apiKey);
            }

            // DESIGN: OpenRouter wants these headers for usage tracking
            request.Headers.Add("X-Title", "LothbrokAI");
            request.Headers.Add("HTTP-Referer", "https://github.com/lothbrokai");

            // Write body
            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
            request.ContentLength = bodyBytes.Length;

            using (var stream = request.GetRequestStream())
            {
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            // Read response
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (var errorResponse = (HttpWebResponse)wex.Response)
                    using (var reader = new StreamReader(errorResponse.GetResponseStream()))
                    {
                        string errorText = reader.ReadToEnd();
                        throw new Exception($"API HTTP Error {(int)errorResponse.StatusCode}: {errorText}", wex);
                    }
                }
                throw;
            }
        }
    }
}
