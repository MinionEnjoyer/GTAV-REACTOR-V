using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core.Protocol
{
    public sealed class BridgeError
    {
        public const int MaximumMessageLength = 512;

        private static readonly Regex CodePattern = new Regex(
            "^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public BridgeError(
            string code,
            string message,
            bool retryable = false,
            JObject? details = null)
        {
            if (string.IsNullOrWhiteSpace(code) ||
                code.Length > 96 ||
                !CodePattern.IsMatch(code))
            {
                throw new ArgumentException(
                    "Bridge error codes must be stable lowercase identifiers.",
                    nameof(code));
            }
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException(
                    "Bridge error messages must not be empty.",
                    nameof(message));
            }

            Code = code;
            // Runtime errors can originate in a third-party game API. Keep a
            // long diagnostic from turning error handling into a second
            // exception while still enforcing the outbound bridge bound.
            Message = message.Length <= MaximumMessageLength
                ? message
                : message.Substring(0, MaximumMessageLength);
            Retryable = retryable;
            Details = details == null ? null : (JObject)details.DeepClone();
        }

        public string Code { get; }

        public string Message { get; }

        public bool Retryable { get; }

        public JObject? Details { get; }
    }
}
