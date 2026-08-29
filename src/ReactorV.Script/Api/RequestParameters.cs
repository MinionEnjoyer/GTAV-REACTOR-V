using System;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Script.Api
{
    internal static class RequestParameters
    {
        public static string RequiredString(JObject parameters, string name, int maximumLength = 64)
        {
            var token = parameters[name];
            if (token == null || token.Type != JTokenType.String)
            {
                throw new ApiException("invalid_params", $"'{name}' must be a string.");
            }

            var value = token.Value<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value!.Length > maximumLength)
            {
                throw new ApiException("invalid_params", $"'{name}' must be a non-empty string up to {maximumLength} characters.");
            }

            return value;
        }

        public static bool OptionalBoolean(JObject parameters, string name, bool fallback = false)
        {
            var token = parameters[name];
            if (token == null)
            {
                return fallback;
            }
            if (token.Type != JTokenType.Boolean)
            {
                throw new ApiException("invalid_params", $"'{name}' must be a boolean.");
            }
            return token.Value<bool>();
        }

        public static string? OptionalString(
            JObject parameters,
            string name,
            int maximumLength = 64)
        {
            var token = parameters[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                throw new ApiException("invalid_params", $"'{name}' must be a string.");
            }
            var value = token.Value<string>()?.Trim() ?? string.Empty;
            if (value.Length > maximumLength)
            {
                throw new ApiException(
                    "invalid_params",
                    $"'{name}' must be no more than {maximumLength} characters.");
            }
            return value;
        }

        public static JObject OptionalObject(JObject parameters, string name)
        {
            var token = parameters[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return new JObject();
            }
            if (token.Type != JTokenType.Object)
            {
                throw new ApiException("invalid_params", $"'{name}' must be an object.");
            }
            return (JObject)token;
        }

        public static int RequiredInteger(JObject parameters, string name, int minimum, int maximum)
        {
            var token = parameters[name];
            if (token == null || token.Type != JTokenType.Integer)
            {
                throw new ApiException("invalid_params", $"'{name}' must be an integer.");
            }

            var value = token.Value<int>();
            if (value < minimum || value > maximum)
            {
                throw new ApiException("invalid_params", $"'{name}' must be between {minimum} and {maximum}.");
            }

            return value;
        }

        public static int OptionalInteger(
            JObject parameters,
            string name,
            int minimum,
            int maximum,
            int fallback)
        {
            var token = parameters[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }
            if (token.Type != JTokenType.Integer)
            {
                throw new ApiException("invalid_params", $"'{name}' must be an integer.");
            }
            var value = token.Value<int>();
            if (value < minimum || value > maximum)
            {
                throw new ApiException(
                    "invalid_params",
                    $"'{name}' must be between {minimum} and {maximum}.");
            }
            return value;
        }

        public static float RequiredNumber(JObject parameters, string name, float minimum, float maximum)
        {
            var token = parameters[name];
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
            {
                throw new ApiException("invalid_params", $"'{name}' must be a number.");
            }

            var value = token.Value<float>();
            if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum || value > maximum)
            {
                throw new ApiException("invalid_params", $"'{name}' is outside the permitted range.");
            }

            return value;
        }
    }
}
