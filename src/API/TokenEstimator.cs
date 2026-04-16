using System;

namespace LothbrokAI.API
{
    /// <summary>
    /// Estimates token count for prompts.
    /// 
    /// DESIGN: We don't have a real tokenizer in C# 7.3 / .NET 4.7.2,
    /// so we use the standard heuristic: ~4 characters per token for English.
    /// This is conservative (real models tokenize more efficiently) which
    /// means we'll slightly underestimate, never overrun budgets.
    /// 
    /// We track actual usage via API response and log the delta for tuning.
    /// </summary>
    public static class TokenEstimator
    {
        // DESIGN: 4 chars/token is the standard GPT estimate.
        // Real models vary: Claude ~3.5, Llama ~3.8, GPT-4 ~4.0
        // We use 4.0 as a conservative estimate.
        private const float CHARS_PER_TOKEN = 4.0f;

        /// <summary>
        /// Estimate token count for a text string.
        /// </summary>
        public static int Estimate(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(text.Length / CHARS_PER_TOKEN);
        }

        /// <summary>
        /// Truncate text to fit within a token budget.
        /// Adds "... [truncated]" if text was shortened.
        /// </summary>
        public static string TruncateToTokens(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text)) return text;

            int estimated = Estimate(text);
            if (estimated <= maxTokens) return text;

            // Calculate max chars and truncate
            int maxChars = (int)(maxTokens * CHARS_PER_TOKEN) - 20; // Reserve space for suffix
            if (maxChars < 0) maxChars = 0;

            // Try to truncate at a sentence boundary
            string truncated = text.Substring(0, Math.Min(maxChars, text.Length));
            int lastPeriod = truncated.LastIndexOf('.');
            if (lastPeriod > maxChars * 0.7) // Don't truncate too aggressively
            {
                truncated = truncated.Substring(0, lastPeriod + 1);
            }

            return truncated + " ... [truncated]";
        }

        /// <summary>
        /// Log the delta between estimated and actual tokens for tuning.
        /// </summary>
        public static void LogAccuracy(string component, int estimated, int actual)
        {
            if (actual <= 0) return;

            float ratio = (float)estimated / actual;
            if (LothbrokConfig.Current.DebugMode)
            {
                LothbrokSubModule.Log(string.Format(
                    "Token estimate [{0}]: estimated={1}, actual={2}, ratio={3:F2}",
                    component, estimated, actual, ratio));
            }
        }
    }
}
