using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReactorV.Integration
{
    internal static class ReactorExtensionLimits
    {
        // Leave room for the protocol-v2 response/event envelope beneath the
        // bridge's 64 KiB message ceiling.
        internal const int MaximumTransportPayload = 60 * 1024;
        internal const int MaximumPayloadDepth = 24;
    }

    /// <summary>Risk attached to an action exposed by a Reactor extension.</summary>
    public enum ReactorActionRisk
    {
        Read,
        Gameplay,
        Persistent,
    }

    public enum ReactorValueType
    {
        Boolean,
        Integer,
        Number,
        String,
        Object,
        Array,
    }

    public enum ReactorLifecycleStage
    {
        Registered,
        StoryReady,
        StoryUnavailable,
        OverlayOpening,
        OverlayOpened,
        OverlayClosing,
        OverlayClosed,
        Suspended,
        Resumed,
        Unloading,
        BrowserReady,
    }

    public sealed class ReactorExtensionDescriptor
    {
        public ReactorExtensionDescriptor(
            string id,
            string name,
            string version,
            string description = "",
            IEnumerable<string>? capabilities = null)
        {
            Id = ReactorValidation.Identifier(id, nameof(id), 64, allowDots: true);
            Name = ReactorValidation.Text(name, nameof(name), 96, required: true);
            Version = ReactorValidation.Text(version, nameof(version), 32, required: true);
            Description = ReactorValidation.Text(description, nameof(description), 512, required: false);
            Capabilities = ReactorValidation.Capabilities(capabilities);
        }

        public string Id { get; }
        public string Name { get; }
        public string Version { get; }
        public string Description { get; }
        public IReadOnlyList<string> Capabilities { get; }

        internal JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["version"] = Version,
            ["description"] = Description,
            ["capabilities"] = new JArray(Capabilities),
        };
    }

    public sealed class ReactorParameterDescriptor
    {
        public ReactorParameterDescriptor(
            string name,
            ReactorValueType type,
            bool required = false,
            double? minimum = null,
            double? maximum = null,
            int maximumLength = 1024,
            IEnumerable<string>? allowedValues = null)
        {
            if (!Enum.IsDefined(typeof(ReactorValueType), type))
                throw new ArgumentOutOfRangeException(nameof(type));
            Name = ReactorValidation.Identifier(name, nameof(name), 64, allowDots: false);
            Type = type;
            Required = required;
            if ((minimum.HasValue && (double.IsNaN(minimum.Value) || double.IsInfinity(minimum.Value))) ||
                (maximum.HasValue && (double.IsNaN(maximum.Value) || double.IsInfinity(maximum.Value))))
                throw new ArgumentException("Parameter bounds must be finite.", nameof(minimum));
            if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
                throw new ArgumentException("Parameter minimum cannot exceed maximum.", nameof(minimum));
            if (maximumLength < 1 || maximumLength > 16_384)
                throw new ArgumentOutOfRangeException(nameof(maximumLength));
            Minimum = minimum;
            Maximum = maximum;
            MaximumLength = maximumLength;
            AllowedValues = (allowedValues ?? Array.Empty<string>())
                .Select(value => ReactorValidation.Text(value, nameof(allowedValues), 128, required: true))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (AllowedValues.Count > 128)
                throw new ArgumentException("A parameter may declare at most 128 allowed values.", nameof(allowedValues));
            if (AllowedValues.Count > 0 && Type != ReactorValueType.String)
                throw new ArgumentException("Allowed values may only be declared for string parameters.", nameof(allowedValues));
        }

        public string Name { get; }
        public ReactorValueType Type { get; }
        public bool Required { get; }
        public double? Minimum { get; }
        public double? Maximum { get; }
        public int MaximumLength { get; }
        public IReadOnlyList<string> AllowedValues { get; }

        internal void Validate(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                if (Required)
                    throw new ReactorValidationException("invalid_params", $"'{Name}' is required.");
                return;
            }

            var validType = Type == ReactorValueType.Boolean ? token.Type == JTokenType.Boolean :
                Type == ReactorValueType.Integer ? token.Type == JTokenType.Integer :
                Type == ReactorValueType.Number ? token.Type == JTokenType.Integer || token.Type == JTokenType.Float :
                Type == ReactorValueType.String ? token.Type == JTokenType.String :
                Type == ReactorValueType.Object ? token.Type == JTokenType.Object :
                token.Type == JTokenType.Array;
            if (!validType)
                throw new ReactorValidationException("invalid_params", $"'{Name}' has the wrong value type.");

            if (Type == ReactorValueType.String)
            {
                var value = token.Value<string>() ?? string.Empty;
                if (value.Length > MaximumLength)
                    throw new ReactorValidationException("invalid_params", $"'{Name}' exceeds its length limit.");
                if (AllowedValues.Count > 0 && !AllowedValues.Contains(value, StringComparer.Ordinal))
                    throw new ReactorValidationException("invalid_params", $"'{Name}' is not an allowed value.");
            }
            else if (Type == ReactorValueType.Integer || Type == ReactorValueType.Number)
            {
                var value = token.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value) ||
                    (Minimum.HasValue && value < Minimum.Value) ||
                    (Maximum.HasValue && value > Maximum.Value))
                    throw new ReactorValidationException("invalid_params", $"'{Name}' is outside its permitted range.");
            }
        }

        internal JObject ToJson()
        {
            var result = new JObject
            {
                ["name"] = Name,
                ["type"] = Type.ToString().ToLowerInvariant(),
                ["required"] = Required,
            };
            if (Minimum.HasValue) result["minimum"] = Minimum.Value;
            if (Maximum.HasValue) result["maximum"] = Maximum.Value;
            if (Type == ReactorValueType.String) result["maximumLength"] = MaximumLength;
            if (AllowedValues.Count > 0) result["allowedValues"] = new JArray(AllowedValues);
            return result;
        }
    }

    public sealed class ReactorActionDescriptor
    {
        public ReactorActionDescriptor(
            string id,
            string label,
            ReactorActionRisk risk,
            IEnumerable<ReactorParameterDescriptor>? parameters = null,
            bool requiresConfirmation = false,
            bool allowAdditionalParameters = false,
            string description = "")
        {
            if (!Enum.IsDefined(typeof(ReactorActionRisk), risk))
                throw new ArgumentOutOfRangeException(nameof(risk));
            Id = ReactorValidation.Identifier(id, nameof(id), 64, allowDots: true);
            Label = ReactorValidation.Text(label, nameof(label), 96, required: true);
            Description = ReactorValidation.Text(description, nameof(description), 512, required: false);
            Risk = risk;
            RequiresConfirmation = requiresConfirmation || risk == ReactorActionRisk.Persistent;
            AllowAdditionalParameters = allowAdditionalParameters;
            var values = (parameters ?? Array.Empty<ReactorParameterDescriptor>()).ToArray();
            if (values.Length > 64)
                throw new ArgumentException("An action may declare at most 64 parameters.", nameof(parameters));
            if (values.Any(value => value == null) ||
                values.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
                throw new ArgumentException("Action parameters must be non-null and uniquely named.", nameof(parameters));
            Parameters = values;
        }

        public string Id { get; }
        public string Label { get; }
        public string Description { get; }
        public ReactorActionRisk Risk { get; }
        public bool RequiresConfirmation { get; }
        public bool AllowAdditionalParameters { get; }
        public IReadOnlyList<ReactorParameterDescriptor> Parameters { get; }

        internal void ValidateParameters(JObject parameters)
        {
            foreach (var descriptor in Parameters) descriptor.Validate(parameters[descriptor.Name]);
            if (AllowAdditionalParameters) return;
            var known = new HashSet<string>(Parameters.Select(value => value.Name), StringComparer.Ordinal);
            var unknown = parameters.Properties().FirstOrDefault(property => !known.Contains(property.Name));
            if (unknown != null)
                throw new ReactorValidationException("invalid_params", $"Unknown parameter '{unknown.Name}'.");
        }

        internal JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["label"] = Label,
            ["description"] = Description,
            ["risk"] = Risk.ToString().ToLowerInvariant(),
            ["requiresConfirmation"] = RequiresConfirmation,
            ["allowAdditionalParameters"] = AllowAdditionalParameters,
            ["parameters"] = new JArray(Parameters.Select(value => value.ToJson())),
        };
    }

    public sealed class ReactorActionContext
    {
        internal ReactorActionContext(
            string extensionId,
            string actionId,
            bool confirmed,
            string? idempotencyKey)
        {
            ExtensionId = extensionId;
            ActionId = actionId;
            Confirmed = confirmed;
            IdempotencyKey = idempotencyKey;
            InvocationId = Guid.NewGuid().ToString("N");
        }

        public string ExtensionId { get; }
        public string ActionId { get; }
        public string InvocationId { get; }
        public bool Confirmed { get; }
        public string? IdempotencyKey { get; }
    }

    public delegate ReactorActionResult ReactorActionHandler(
        ReactorActionContext context,
        JObject parameters);

    public sealed class ReactorActionResult
    {
        private ReactorActionResult(
            bool succeeded,
            bool confirmationRequired,
            bool replayed,
            JToken? value,
            string? errorCode,
            string? errorMessage)
        {
            Succeeded = succeeded;
            ConfirmationRequired = confirmationRequired;
            Replayed = replayed;
            Value = value?.DeepClone();
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public bool Succeeded { get; }
        public bool ConfirmationRequired { get; }
        public bool Replayed { get; }
        public JToken? Value { get; }
        public string? ErrorCode { get; }
        public string? ErrorMessage { get; }

        public static ReactorActionResult Success(JToken? value = null) =>
            new ReactorActionResult(true, false, false, value ?? JValue.CreateNull(), null, null);

        public static ReactorActionResult Failure(string code, string message) =>
            new ReactorActionResult(
                false,
                false,
                false,
                null,
                ReactorValidation.Identifier(code, nameof(code), 64, allowDots: false),
                ReactorValidation.Text(message, nameof(message), 512, required: true));

        internal static ReactorActionResult RequireConfirmation() =>
            new ReactorActionResult(false, true, false, null, "confirmation_required", "This action requires confirmation.");

        internal ReactorActionResult AsReplay() =>
            new ReactorActionResult(Succeeded, ConfirmationRequired, true, Value, ErrorCode, ErrorMessage);

        internal JObject ToJson()
        {
            var result = new JObject
            {
                ["succeeded"] = Succeeded,
                ["confirmationRequired"] = ConfirmationRequired,
                ["replayed"] = Replayed,
            };
            if (Succeeded) result["value"] = Value?.DeepClone() ?? JValue.CreateNull();
            else result["error"] = new JObject { ["code"] = ErrorCode, ["message"] = ErrorMessage };
            return result;
        }
    }

    public sealed class ReactorEventDescriptor
    {
        public ReactorEventDescriptor(string id, string description = "", int maximumPayloadBytes = 16_384)
        {
            Id = ReactorValidation.Identifier(id, nameof(id), 64, allowDots: true);
            Description = ReactorValidation.Text(description, nameof(description), 512, required: false);
            if (maximumPayloadBytes < 64 || maximumPayloadBytes > ReactorExtensionLimits.MaximumTransportPayload)
                throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
            MaximumPayloadBytes = maximumPayloadBytes;
        }

        public string Id { get; }
        public string Description { get; }
        public int MaximumPayloadBytes { get; }

        internal JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["description"] = Description,
            ["maximumPayloadBytes"] = MaximumPayloadBytes,
        };
    }

    public sealed class ReactorLifecycleContext
    {
        internal ReactorLifecycleContext(string extensionId, ReactorLifecycleStage stage, JToken? payload)
        {
            ExtensionId = extensionId;
            Stage = stage;
            Payload = payload?.DeepClone();
        }

        public string ExtensionId { get; }
        public ReactorLifecycleStage Stage { get; }
        public JToken? Payload { get; }
    }

    public interface IReactorExtensionLifecycle
    {
        void OnLifecycle(ReactorLifecycleContext context);
    }

    internal sealed class ReactorValidationException : Exception
    {
        public ReactorValidationException(string code, string message) : base(message) => Code = code;
        public string Code { get; }
    }

    internal static class ReactorValidation
    {
        private static readonly Regex LocalIdentifier = new Regex(
            "^[a-z][a-z0-9]*(?:[_-][a-z0-9]+)*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex NamespacedIdentifier = new Regex(
            "^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Identifier(string value, string parameterName, int maximumLength, bool allowDots)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            var pattern = allowDots ? NamespacedIdentifier : LocalIdentifier;
            if (normalized.Length == 0 || normalized.Length > maximumLength || !pattern.IsMatch(normalized))
                throw new ArgumentException(
                    $"'{parameterName}' must be a lowercase safe identifier up to {maximumLength} characters.",
                    parameterName);
            return normalized;
        }

        public static string Text(string value, string parameterName, int maximumLength, bool required)
        {
            var normalized = (value ?? string.Empty).Trim();
            if ((required && normalized.Length == 0) || normalized.Length > maximumLength || normalized.IndexOf('\0') >= 0)
                throw new ArgumentException($"'{parameterName}' is invalid or exceeds {maximumLength} characters.", parameterName);
            return normalized;
        }

        public static IReadOnlyList<string> Capabilities(IEnumerable<string>? values)
        {
            var result = (values ?? Array.Empty<string>())
                .Select(value => Identifier(value, nameof(values), 64, allowDots: true))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (result.Length > 64)
                throw new ArgumentException("An extension may declare at most 64 capabilities.", nameof(values));
            return result;
        }

        public static string CanonicalJson(JToken value) =>
            Canonicalize(value).ToString(Formatting.None);

        public static bool IsWithinDepth(JToken value, int maximumDepth)
        {
            var pending = new Stack<Tuple<JToken, int>>();
            pending.Push(Tuple.Create(value, 1));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current.Item2 > maximumDepth) return false;
                foreach (var child in current.Item1.Children())
                    pending.Push(Tuple.Create(child, current.Item2 + 1));
            }
            return true;
        }

        private static JToken Canonicalize(JToken value)
        {
            if (value is JObject objectValue)
            {
                var result = new JObject();
                foreach (var property in objectValue.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
                    result.Add(property.Name, Canonicalize(property.Value));
                return result;
            }
            if (value is JArray arrayValue)
                return new JArray(arrayValue.Select(Canonicalize));
            return value.DeepClone();
        }

        public static string IdempotencyKey(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0 || normalized.Length > 128 || normalized.Any(character => character < 0x21 || character > 0x7e))
                throw new ReactorValidationException(
                    "idempotency_key_required",
                    "Persistent actions require a 1-128 character printable idempotency key.");
            return normalized;
        }
    }
}
