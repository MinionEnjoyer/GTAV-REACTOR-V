using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core.Protocol
{
    public static class BridgeProtocol
    {
        public const int MaximumMessageLength = 65_536;
        public const int MaximumNestingDepth = 32;
        public const int MaximumDeadlineMs = 120_000;
        public const int MinimumSupportedProtocolVersion = 1;
        public const int CurrentProtocolVersion = 2;

        private const int MaximumMethodLength = 96;
        private const int MaximumEventNameLength = 96;
        private const int MaximumIdempotencyKeyLength = 128;

        private static readonly Regex IdPattern = new Regex(
            "^[A-Za-z0-9_-]{1,64}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex MethodPattern = new Regex(
            "^[a-z][A-Za-z0-9]*(\\.[a-z][A-Za-z0-9]*)+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex EventPattern = new Regex(
            "^[a-z][A-Za-z0-9]*(\\.[a-z][A-Za-z0-9]*)+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex IdempotencyKeyPattern = new Regex(
            "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex CancellationReasonPattern = new Regex(
            "^[a-z][a-z0-9._-]{0,63}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> RequestProperties =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "kind",
                "id",
                "method",
                "params",
                "protocolVersion",
                "minimumProtocolVersion",
                "deadlineMs",
                "idempotencyKey",
                "confirmed",
            };

        private static readonly HashSet<string> CancelProperties =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "kind",
                "id",
                "protocolVersion",
                "minimumProtocolVersion",
                "reason",
            };

        public static bool TryParseRequest(
            string json,
            out BridgeRequest? request,
            out BridgeError? error)
        {
            request = null;
            if (!TryLoadMessage(json, out var message, out error) || message == null)
            {
                return false;
            }

            if (!TryReadKind(message, out var kind, out error) ||
                !string.Equals(kind, "request", StringComparison.Ordinal))
            {
                error = new BridgeError(
                    "invalid_request",
                    "Only request messages are accepted by this parser.");
                return false;
            }

            return TryParseRequestObject(message, out request, out error);
        }

        /// <summary>
        /// Parses either a regular request or a protocol-v2 queued
        /// cancellation. Exactly one output is non-null after success.
        /// </summary>
        public static bool TryParseInbound(
            string json,
            out BridgeRequest? request,
            out BridgeCancel? cancellation,
            out BridgeError? error)
        {
            request = null;
            cancellation = null;
            if (!TryLoadMessage(json, out var message, out error) || message == null)
            {
                return false;
            }
            if (!TryReadKind(message, out var kind, out error))
            {
                return false;
            }

            if (string.Equals(kind, "request", StringComparison.Ordinal))
            {
                return TryParseRequestObject(message, out request, out error);
            }
            if (string.Equals(kind, "cancel", StringComparison.Ordinal))
            {
                return TryParseCancelObject(message, out cancellation, out error);
            }

            error = new BridgeError(
                "invalid_request",
                "Only request and cancel messages are accepted from the page.");
            return false;
        }

        public static bool TryNegotiateProtocolVersion(
            int minimumVersion,
            int maximumVersion,
            out int selectedVersion)
        {
            selectedVersion = 0;
            if (minimumVersion < 1 || maximumVersion < minimumVersion)
            {
                return false;
            }

            var candidate = Math.Min(maximumVersion, CurrentProtocolVersion);
            if (candidate < Math.Max(minimumVersion, MinimumSupportedProtocolVersion))
            {
                return false;
            }

            selectedVersion = candidate;
            return true;
        }

        public static bool IsValidEventName(string? eventName) =>
            !string.IsNullOrEmpty(eventName) &&
            eventName!.Length <= MaximumEventNameLength &&
            EventPattern.IsMatch(eventName);

        public static string SerializeResponse(BridgeResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }
            EnsureValidId(response.Id, nameof(response));
            EnsureSupportedOutboundVersion(response.ProtocolVersion);

            var message = CreateResponseMessage(response);
            if (IsWithinMaximumDepth(message) && TrySerializeBounded(message, out var json))
            {
                return json!;
            }

            // A response must always terminate the matching request. Replace
            // an oversized/deep payload with a small typed failure rather than
            // dropping it and forcing the page to wait for its timeout.
            var fallback = BridgeResponse.Failure(
                response.Id,
                new BridgeError(
                    "response_too_large",
                    "The API response exceeded the ReactorV bridge limit."),
                response.ProtocolVersion);
            message = CreateResponseMessage(fallback);
            if (!TrySerializeBounded(message, out json))
            {
                throw new InvalidOperationException(
                    "The bounded ReactorV response fallback could not be serialized.");
            }
            return json!;
        }

        public static string SerializeEvent(
            string eventName,
            JToken? payload,
            int protocolVersion = MinimumSupportedProtocolVersion)
        {
            if (!IsValidEventName(eventName))
            {
                throw new ArgumentException(
                    "Event names must be dotted lower-camel identifiers up to 96 characters.",
                    nameof(eventName));
            }
            EnsureSupportedOutboundVersion(protocolVersion);

            var message = new JObject
            {
                ["kind"] = "event",
                ["event"] = eventName,
                ["payload"] = payload ?? JValue.CreateNull(),
            };
            if (protocolVersion >= 2)
            {
                message["protocolVersion"] = protocolVersion;
            }

            if (!IsWithinMaximumDepth(message) ||
                !TrySerializeBounded(message, out var json))
            {
                throw new InvalidOperationException(
                    "The bridge event exceeded the ReactorV message size or nesting limit.");
            }
            return json!;
        }

        private static bool TryParseRequestObject(
            JObject message,
            out BridgeRequest? request,
            out BridgeError? error)
        {
            request = null;
            if (!HasOnlyProperties(message, RequestProperties, out error))
            {
                return false;
            }
            if (!TryReadRequiredString(message, "id", out var id, out error) ||
                !IdPattern.IsMatch(id!))
            {
                error = InvalidField(
                    "invalid_id",
                    "id",
                    "Request ids must be 1-64 URL-safe characters.");
                return false;
            }
            if (!TryReadRequiredString(message, "method", out var method, out error) ||
                method!.Length > MaximumMethodLength ||
                !MethodPattern.IsMatch(method))
            {
                error = InvalidField(
                    "invalid_method",
                    "method",
                    "The requested API method name is invalid.");
                return false;
            }
            if (!TryReadVersionRange(
                    message,
                    out var selectedVersion,
                    out var requestedVersion,
                    out var minimumVersion,
                    out error))
            {
                return false;
            }

            var parameters = new JObject();
            if (message.TryGetValue("params", StringComparison.Ordinal, out var paramsToken))
            {
                if (paramsToken.Type != JTokenType.Object)
                {
                    error = InvalidField(
                        "invalid_params",
                        "params",
                        "Request params must be a JSON object.");
                    return false;
                }
                parameters = (JObject)paramsToken;
            }

            int? deadlineMs = null;
            if (message.TryGetValue("deadlineMs", StringComparison.Ordinal, out var deadlineToken))
            {
                if (!TryReadInt32(deadlineToken, out var value) ||
                    value < 1 ||
                    value > MaximumDeadlineMs)
                {
                    error = InvalidField(
                        "invalid_deadline",
                        "deadlineMs",
                        $"'deadlineMs' must be an integer from 1 through {MaximumDeadlineMs}.");
                    return false;
                }
                deadlineMs = value;
            }

            string? idempotencyKey = null;
            if (message.TryGetValue("idempotencyKey", StringComparison.Ordinal, out var keyToken))
            {
                if (keyToken.Type != JTokenType.String)
                {
                    error = InvalidField(
                        "invalid_idempotency_key",
                        "idempotencyKey",
                        "'idempotencyKey' must be a bounded URL-safe string.");
                    return false;
                }
                idempotencyKey = keyToken.Value<string>();
                if (string.IsNullOrEmpty(idempotencyKey) ||
                    idempotencyKey!.Length > MaximumIdempotencyKeyLength ||
                    !IdempotencyKeyPattern.IsMatch(idempotencyKey))
                {
                    error = InvalidField(
                        "invalid_idempotency_key",
                        "idempotencyKey",
                        "'idempotencyKey' must be a bounded URL-safe string.");
                    return false;
                }
            }

            var confirmed = false;
            if (message.TryGetValue("confirmed", StringComparison.Ordinal, out var confirmedToken))
            {
                if (confirmedToken.Type != JTokenType.Boolean)
                {
                    error = InvalidField(
                        "invalid_confirmation",
                        "confirmed",
                        "'confirmed' must be a boolean.");
                    return false;
                }
                confirmed = confirmedToken.Value<bool>();
            }

            var hasV2Metadata = deadlineMs.HasValue ||
                idempotencyKey != null ||
                message.ContainsKey("confirmed");
            if (hasV2Metadata && selectedVersion < 2)
            {
                error = new BridgeError(
                    "unsupported_protocol",
                    "Deadlines, idempotency, and confirmation require protocol version 2.",
                    details: ProtocolDetails(minimumVersion, requestedVersion));
                return false;
            }

            request = new BridgeRequest(
                id!,
                method!,
                parameters,
                selectedVersion,
                requestedVersion,
                minimumVersion,
                deadlineMs,
                idempotencyKey,
                confirmed);
            error = null;
            return true;
        }

        private static bool TryParseCancelObject(
            JObject message,
            out BridgeCancel? cancellation,
            out BridgeError? error)
        {
            cancellation = null;
            if (!HasOnlyProperties(message, CancelProperties, out error))
            {
                return false;
            }
            if (!TryReadRequiredString(message, "id", out var id, out error) ||
                !IdPattern.IsMatch(id!))
            {
                error = InvalidField(
                    "invalid_id",
                    "id",
                    "Cancellation ids must match the original request id.");
                return false;
            }
            if (!TryReadVersionRange(
                    message,
                    out var selectedVersion,
                    out var requestedVersion,
                    out var minimumVersion,
                    out error))
            {
                return false;
            }
            if (selectedVersion < 2)
            {
                error = new BridgeError(
                    "unsupported_protocol",
                    "Queued cancellation requires protocol version 2.",
                    details: ProtocolDetails(minimumVersion, requestedVersion));
                return false;
            }

            string? reason = null;
            if (message.TryGetValue("reason", StringComparison.Ordinal, out var reasonToken))
            {
                if (reasonToken.Type != JTokenType.String)
                {
                    error = InvalidField(
                        "invalid_cancel_reason",
                        "reason",
                        "Cancellation reasons must be stable lowercase identifiers.");
                    return false;
                }
                reason = reasonToken.Value<string>();
                if (string.IsNullOrEmpty(reason) ||
                    !CancellationReasonPattern.IsMatch(reason!))
                {
                    error = InvalidField(
                        "invalid_cancel_reason",
                        "reason",
                        "Cancellation reasons must be stable lowercase identifiers.");
                    return false;
                }
            }

            cancellation = new BridgeCancel(
                id!,
                selectedVersion,
                requestedVersion,
                minimumVersion,
                reason);
            error = null;
            return true;
        }

        private static bool TryLoadMessage(
            string json,
            out JObject? message,
            out BridgeError? error)
        {
            message = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumMessageLength)
            {
                error = new BridgeError(
                    "invalid_request",
                    "The request is empty or exceeds 64 KiB.");
                return false;
            }

            try
            {
                if (ContainsJsonComment(json))
                {
                    error = new BridgeError(
                        "invalid_json",
                        "The bridge message must not contain JSON comments.");
                    return false;
                }
                using (var text = new StringReader(json))
                using (var reader = new JsonTextReader(text)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                    MaxDepth = MaximumNestingDepth,
                    SupportMultipleContent = false,
                })
                {
                    var token = JToken.ReadFrom(
                        reader,
                        new JsonLoadSettings
                        {
                            CommentHandling = CommentHandling.Load,
                            DuplicatePropertyNameHandling =
                                DuplicatePropertyNameHandling.Error,
                        });
                    if (token.Type != JTokenType.Object || ContainsComment(token))
                    {
                        error = new BridgeError(
                            "invalid_json",
                            "The bridge message must be one strict JSON object.");
                        return false;
                    }
                    if (reader.Read())
                    {
                        error = new BridgeError(
                            "invalid_json",
                            "The bridge message contains trailing JSON content.");
                        return false;
                    }
                    message = (JObject)token;
                    return true;
                }
            }
            catch (JsonException)
            {
                error = new BridgeError(
                    "invalid_json",
                    "The bridge message is not valid strict JSON.");
                return false;
            }
        }

        private static bool TryReadKind(
            JObject message,
            out string? kind,
            out BridgeError? error)
        {
            if (!TryReadRequiredString(message, "kind", out kind, out error))
            {
                error = InvalidField(
                    "invalid_request",
                    "kind",
                    "The bridge message kind must be a string.");
                return false;
            }
            return true;
        }

        private static bool TryReadVersionRange(
            JObject message,
            out int selectedVersion,
            out int requestedVersion,
            out int minimumVersion,
            out BridgeError? error)
        {
            selectedVersion = MinimumSupportedProtocolVersion;
            requestedVersion = MinimumSupportedProtocolVersion;
            minimumVersion = MinimumSupportedProtocolVersion;
            error = null;

            var hasRequested = message.TryGetValue(
                "protocolVersion",
                StringComparison.Ordinal,
                out var requestedToken);
            var hasMinimum = message.TryGetValue(
                "minimumProtocolVersion",
                StringComparison.Ordinal,
                out var minimumToken);
            if (!hasRequested && !hasMinimum)
            {
                return true;
            }
            if (!hasRequested ||
                !TryReadInt32(requestedToken!, out requestedVersion))
            {
                error = InvalidField(
                    "invalid_protocol",
                    "protocolVersion",
                    "'protocolVersion' must be a positive integer.");
                return false;
            }
            if (hasMinimum)
            {
                if (!TryReadInt32(minimumToken!, out minimumVersion))
                {
                    error = InvalidField(
                        "invalid_protocol",
                        "minimumProtocolVersion",
                        "'minimumProtocolVersion' must be a positive integer.");
                    return false;
                }
            }
            else
            {
                minimumVersion = requestedVersion;
            }

            if (!TryNegotiateProtocolVersion(
                    minimumVersion,
                    requestedVersion,
                    out selectedVersion))
            {
                error = new BridgeError(
                    "unsupported_protocol",
                    "The page and ReactorV do not share a supported protocol version.",
                    details: ProtocolDetails(minimumVersion, requestedVersion));
                return false;
            }
            return true;
        }

        private static bool TryReadRequiredString(
            JObject message,
            string propertyName,
            out string? value,
            out BridgeError? error)
        {
            value = null;
            error = null;
            if (!message.TryGetValue(
                    propertyName,
                    StringComparison.Ordinal,
                    out var token) ||
                token.Type != JTokenType.String)
            {
                return false;
            }
            value = token.Value<string>();
            return value != null;
        }

        private static bool TryReadInt32(JToken token, out int value)
        {
            value = 0;
            if (token.Type != JTokenType.Integer)
            {
                return false;
            }
            try
            {
                value = token.Value<int>();
                return true;
            }
            catch (Exception error) when (
                error is OverflowException ||
                error is FormatException ||
                error is InvalidCastException)
            {
                return false;
            }
        }

        private static bool HasOnlyProperties(
            JObject message,
            HashSet<string> allowed,
            out BridgeError? error)
        {
            foreach (var property in message.Properties())
            {
                if (allowed.Contains(property.Name))
                {
                    continue;
                }
                error = InvalidField(
                    "unknown_property",
                    property.Name,
                    $"Unknown bridge envelope property '{property.Name}'.");
                return false;
            }
            error = null;
            return true;
        }

        private static BridgeError InvalidField(
            string code,
            string field,
            string message) =>
            new BridgeError(
                code,
                message,
                details: new JObject { ["field"] = field });

        private static JObject ProtocolDetails(int minimum, int maximum) =>
            new JObject
            {
                ["clientMinimum"] = minimum,
                ["clientMaximum"] = maximum,
                ["hostMinimum"] = MinimumSupportedProtocolVersion,
                ["hostMaximum"] = CurrentProtocolVersion,
            };

        private static JObject CreateResponseMessage(BridgeResponse response)
        {
            var message = new JObject
            {
                ["kind"] = "response",
                ["id"] = response.Id,
            };
            if (response.ProtocolVersion >= 2)
            {
                message["protocolVersion"] = response.ProtocolVersion;
            }

            if (response.Error == null)
            {
                message["result"] = response.Result ?? JValue.CreateNull();
            }
            else
            {
                var error = new JObject
                {
                    ["code"] = response.Error.Code,
                    ["message"] = response.Error.Message,
                };
                if (response.ProtocolVersion >= 2 || response.Error.Retryable)
                {
                    error["retryable"] = response.Error.Retryable;
                }
                if (response.Error.Details != null)
                {
                    error["details"] = response.Error.Details;
                }
                message["error"] = error;
            }
            return message;
        }

        private static void EnsureValidId(string id, string parameterName)
        {
            if (string.IsNullOrEmpty(id) || !IdPattern.IsMatch(id))
            {
                throw new ArgumentException(
                    "Response ids must be 1-64 URL-safe characters.",
                    parameterName);
            }
        }

        private static void EnsureSupportedOutboundVersion(int protocolVersion)
        {
            if (protocolVersion < MinimumSupportedProtocolVersion ||
                protocolVersion > CurrentProtocolVersion)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(protocolVersion),
                    protocolVersion,
                    "Outbound messages must use a supported protocol version.");
            }
        }

        private static bool TrySerializeBounded(JObject message, out string? json)
        {
            json = message.ToString(Formatting.None);
            if (json.Length <= MaximumMessageLength)
            {
                return true;
            }
            json = null;
            return false;
        }

        private static bool IsWithinMaximumDepth(JToken token)
        {
            var pending = new Stack<KeyValuePair<JToken, int>>();
            pending.Push(new KeyValuePair<JToken, int>(token, 1));
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                if (item.Value > MaximumNestingDepth)
                {
                    return false;
                }

                if (item.Key is JObject obj)
                {
                    foreach (var property in obj.Properties())
                    {
                        pending.Push(new KeyValuePair<JToken, int>(
                            property.Value,
                            item.Value + 1));
                    }
                }
                else if (item.Key is JArray array)
                {
                    foreach (var child in array)
                    {
                        pending.Push(new KeyValuePair<JToken, int>(
                            child,
                            item.Value + 1));
                    }
                }
            }
            return true;
        }

        private static bool ContainsComment(JToken token)
        {
            var pending = new Stack<JToken>();
            pending.Push(token);
            while (pending.Count > 0)
            {
                var child = pending.Pop();
                if (child.Type == JTokenType.Comment)
                {
                    return true;
                }
                if (child is JContainer container)
                {
                    foreach (var nested in container.Children())
                    {
                        pending.Push(nested);
                    }
                }
            }
            return false;
        }

        private static bool ContainsJsonComment(string json)
        {
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text)
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal,
                MaxDepth = MaximumNestingDepth,
                SupportMultipleContent = false,
            })
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.Comment)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
