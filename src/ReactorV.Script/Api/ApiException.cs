using System;

namespace RageWebUI.Script.Api
{
    internal sealed class ApiException : Exception
    {
        public ApiException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}

