using System;
using System.Text;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BridgeProtocolTests
{
    [Fact]
    public void ParsesAValidRequest()
    {
        const string json = "{\"kind\":\"request\",\"id\":\"req-1\",\"method\":\"player.teleport\",\"params\":{\"x\":1}}";

        var accepted = BridgeProtocol.TryParseRequest(json, out var request, out var error);

        Assert.True(accepted);
        Assert.Null(error);
        Assert.Equal("req-1", request!.Id);
        Assert.Equal("player.teleport", request.Method);
        Assert.Equal(1, request.Parameters.Value<int>("x"));
        Assert.Equal(1, request.ProtocolVersion);
        Assert.Equal(1, request.RequestedProtocolVersion);
        Assert.Equal(1, request.MinimumProtocolVersion);
        Assert.Null(request.DeadlineMs);
        Assert.Null(request.IdempotencyKey);
        Assert.False(request.Confirmed);
    }

    [Fact]
    public void ExistingThreeArgumentConstructorRemainsVersionOne()
    {
        var request = new BridgeRequest("legacy", "game.getState", new JObject());

        Assert.Equal(1, request.ProtocolVersion);
        Assert.Equal(1, request.RequestedProtocolVersion);
        Assert.Equal(1, request.MinimumProtocolVersion);
        Assert.Null(request.DeadlineMs);
        Assert.Null(request.IdempotencyKey);
        Assert.False(request.Confirmed);
    }

    [Fact]
    public void ParsesProtocolTwoActionMetadata()
    {
        const string json = """
            {
              "kind":"request",
              "id":"purchase-1",
              "method":"menu.invoke",
              "params":{"menuId":"allin1.gbay","actionId":"purchase"},
              "protocolVersion":2,
              "minimumProtocolVersion":2,
              "deadlineMs":5000,
              "idempotencyKey":"session-17:purchase-42",
              "confirmed":true
            }
            """;

        var accepted = BridgeProtocol.TryParseRequest(
            json,
            out var request,
            out var error);

        Assert.True(accepted);
        Assert.Null(error);
        Assert.Equal(2, request!.ProtocolVersion);
        Assert.Equal(2, request.RequestedProtocolVersion);
        Assert.Equal(2, request.MinimumProtocolVersion);
        Assert.Equal(5000, request.DeadlineMs);
        Assert.Equal("session-17:purchase-42", request.IdempotencyKey);
        Assert.True(request.Confirmed);
    }

    [Fact]
    public void NegotiatesHighestCommonVersionFromFutureRange()
    {
        const string json = """
            {"kind":"request","id":"r1","method":"runtime.handshake",
             "protocolVersion":3,"minimumProtocolVersion":2,"params":{}}
            """;

        Assert.True(BridgeProtocol.TryParseRequest(json, out var request, out var error));
        Assert.Null(error);
        Assert.Equal(2, request!.ProtocolVersion);
        Assert.Equal(3, request.RequestedProtocolVersion);
        Assert.Equal(2, request.MinimumProtocolVersion);
    }

    [Theory]
    [InlineData("runtime.handshake")]
    [InlineData("runtime.describe")]
    [InlineData("overlay.setVisibility")]
    [InlineData("overlay.setInputMode")]
    [InlineData("extensions.list")]
    [InlineData("extensions.invoke")]
    [InlineData("menu.list")]
    [InlineData("menu.get")]
    [InlineData("menu.invoke")]
    [InlineData("events.subscribe")]
    [InlineData("events.unsubscribe")]
    public void AcceptsSettledVersionTwoMethodIdentifiers(string method)
    {
        var json =
            $"{{\"kind\":\"request\",\"id\":\"r1\",\"method\":\"{method}\"," +
            "\"protocolVersion\":2,\"params\":{}}";

        Assert.True(BridgeProtocol.TryParseRequest(json, out var request, out var error));
        Assert.Null(error);
        Assert.Equal(method, request!.Method);
    }

    [Theory]
    [InlineData(1, 2, 2)]
    [InlineData(2, 2, 2)]
    [InlineData(2, 5, 2)]
    public void NegotiatesSupportedProtocolRanges(
        int minimum,
        int maximum,
        int expected)
    {
        Assert.True(BridgeProtocol.TryNegotiateProtocolVersion(
            minimum,
            maximum,
            out var selected));
        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(2, 1)]
    public void RejectsIncompatibleProtocolRanges(int minimum, int maximum)
    {
        Assert.False(BridgeProtocol.TryNegotiateProtocolVersion(
            minimum,
            maximum,
            out var selected));
        Assert.Equal(0, selected);
    }

    [Theory]
    [InlineData("{}", "invalid_request")]
    [InlineData("not-json", "invalid_json")]
    [InlineData("{\"kind\":\"event\",\"id\":\"a\",\"method\":\"game.getState\"}", "invalid_request")]
    [InlineData("{\"kind\":\"request\",\"id\":\"bad id\",\"method\":\"game.getState\"}", "invalid_id")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"NATIVE_CALL\"}", "invalid_method")]
    [InlineData("{\"kind\":1,\"id\":\"a\",\"method\":\"game.getState\"}", "invalid_request")]
    [InlineData("{\"kind\":\"request\",\"id\":1,\"method\":\"game.getState\"}", "invalid_id")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":1}", "invalid_method")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\",\"params\":[]}", "invalid_params")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\",\"protocolVersion\":\"2\"}", "invalid_protocol")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\",\"minimumProtocolVersion\":1}", "invalid_protocol")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\",\"protocolVersion\":2,\"deadlineMs\":false}", "invalid_deadline")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\",\"protocolVersion\":2,\"idempotencyKey\":7}", "invalid_idempotency_key")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\",\"protocolVersion\":2,\"confirmed\":\"yes\"}", "invalid_confirmation")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\",\"unexpected\":true}", "unknown_property")]
    public void RejectsMalformedRequests(string json, string expectedCode)
    {
        var accepted = BridgeProtocol.TryParseRequest(json, out _, out var error);

        Assert.False(accepted);
        Assert.Equal(expectedCode, error!.Code);
    }

    [Fact]
    public void RejectsVersionTwoMetadataOnLegacyEnvelope()
    {
        const string json =
            "{\"kind\":\"request\",\"id\":\"a\",\"method\":\"menu.invoke\"," +
            "\"protocolVersion\":1,\"deadlineMs\":1000}";

        Assert.False(BridgeProtocol.TryParseRequest(json, out _, out var error));
        Assert.Equal("unsupported_protocol", error!.Code);
        Assert.Equal(1, error.Details!.Value<int>("clientMinimum"));
    }

    [Fact]
    public void RejectsProtocolRangeWithoutCommonVersion()
    {
        const string json =
            "{\"kind\":\"request\",\"id\":\"a\",\"method\":\"runtime.handshake\"," +
            "\"protocolVersion\":4,\"minimumProtocolVersion\":3}";

        Assert.False(BridgeProtocol.TryParseRequest(json, out _, out var error));
        Assert.Equal("unsupported_protocol", error!.Code);
        Assert.Equal(3, error.Details!.Value<int>("clientMinimum"));
        Assert.Equal(2, error.Details.Value<int>("hostMaximum"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120001)]
    public void RejectsDeadlineOutsideBoundedRange(int deadlineMs)
    {
        var json =
            "{\"kind\":\"request\",\"id\":\"a\",\"method\":\"menu.invoke\"," +
            $"\"protocolVersion\":2,\"deadlineMs\":{deadlineMs}}}";

        Assert.False(BridgeProtocol.TryParseRequest(json, out _, out var error));
        Assert.Equal("invalid_deadline", error!.Code);
    }

    [Fact]
    public void RejectsIntegerOverflowWithoutThrowing()
    {
        const string json =
            "{\"kind\":\"request\",\"id\":\"a\",\"method\":\"menu.invoke\"," +
            "\"protocolVersion\":999999999999999999999999999999}";

        Assert.False(BridgeProtocol.TryParseRequest(json, out _, out var error));
        Assert.Equal("invalid_protocol", error!.Code);
    }

    [Theory]
    [InlineData("{\"kind\":\"request\",\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\"}")]
    [InlineData("{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\",\"params\":{\"x\":1,\"x\":2}}")]
    public void RejectsDuplicatePropertiesAtEveryDepth(string json)
    {
        Assert.False(BridgeProtocol.TryParseRequest(json, out _, out var error));
        Assert.Equal("invalid_json", error!.Code);
    }

    [Fact]
    public void RejectsCommentsAndTrailingContent()
    {
        const string commented =
            "{\"kind\":\"request\",/* comment */\"id\":\"a\",\"method\":\"game.getState\"}";
        const string trailing =
            "{\"kind\":\"request\",\"id\":\"a\",\"method\":\"game.getState\"} {}";

        Assert.False(BridgeProtocol.TryParseRequest(commented, out _, out var commentError));
        Assert.Equal("invalid_json", commentError!.Code);
        Assert.False(BridgeProtocol.TryParseRequest(trailing, out _, out var trailingError));
        Assert.Equal("invalid_json", trailingError!.Code);
    }

    [Fact]
    public void RejectsExcessiveNesting()
    {
        var nested = "0";
        for (var index = 0; index < BridgeProtocol.MaximumNestingDepth + 4; index++)
        {
            nested = "{\"value\":" + nested + "}";
        }
        var json =
            "{\"kind\":\"request\",\"id\":\"deep\",\"method\":\"menu.invoke\",\"params\":" +
            nested + "}";

        Assert.False(BridgeProtocol.TryParseRequest(json, out _, out var error));
        Assert.Equal("invalid_json", error!.Code);
    }

    [Fact]
    public void RejectsOversizedInboundMessageBeforeParsing()
    {
        var json = new string('x', BridgeProtocol.MaximumMessageLength + 1);

        Assert.False(BridgeProtocol.TryParseRequest(json, out _, out var error));
        Assert.Equal("invalid_request", error!.Code);
    }

    [Fact]
    public void ParsesProtocolTwoCancellationUsingOriginalRequestId()
    {
        const string json =
            "{\"kind\":\"cancel\",\"id\":\"purchase-1\",\"protocolVersion\":2," +
            "\"reason\":\"client_timeout\"}";

        Assert.True(BridgeProtocol.TryParseInbound(
            json,
            out var request,
            out var cancellation,
            out var error));
        Assert.Null(request);
        Assert.Null(error);
        Assert.Equal("purchase-1", cancellation!.Id);
        Assert.Equal(2, cancellation.ProtocolVersion);
        Assert.Equal("client_timeout", cancellation.Reason);
    }

    [Fact]
    public void RequestOnlyParserRejectsCancellation()
    {
        const string json =
            "{\"kind\":\"cancel\",\"id\":\"purchase-1\",\"protocolVersion\":2}";

        Assert.False(BridgeProtocol.TryParseRequest(json, out _, out var error));
        Assert.Equal("invalid_request", error!.Code);
    }

    [Theory]
    [InlineData("{\"kind\":\"cancel\",\"id\":\"a\"}", "unsupported_protocol")]
    [InlineData("{\"kind\":\"cancel\",\"id\":\"a\",\"protocolVersion\":2,\"reason\":7}", "invalid_cancel_reason")]
    [InlineData("{\"kind\":\"cancel\",\"id\":\"a\",\"protocolVersion\":2,\"reason\":\"Not Valid\"}", "invalid_cancel_reason")]
    [InlineData("{\"kind\":\"cancel\",\"id\":\"a\",\"protocolVersion\":2,\"method\":\"game.getState\"}", "unknown_property")]
    public void RejectsMalformedCancellation(string json, string expectedCode)
    {
        Assert.False(BridgeProtocol.TryParseInbound(
            json,
            out _,
            out _,
            out var error));
        Assert.Equal(expectedCode, error!.Code);
    }

    [Fact]
    public void SerializesErrorsWithoutAResultField()
    {
        var json = BridgeProtocol.SerializeResponse(BridgeResponse.Failure("r1", "no_vehicle", "Not in a vehicle."));
        var message = JObject.Parse(json);

        Assert.Equal("response", message.Value<string>("kind"));
        Assert.Equal("no_vehicle", message["error"]!.Value<string>("code"));
        Assert.Null(message["result"]);
    }

    [Fact]
    public void SerializesTypedProtocolTwoErrors()
    {
        var response = BridgeResponse.Failure(
            "r1",
            new BridgeError(
                "queue_full",
                "Try again.",
                retryable: true,
                details: new JObject { ["limit"] = 256 }),
            protocolVersion: 2);

        var message = JObject.Parse(BridgeProtocol.SerializeResponse(response));

        Assert.Equal(2, message.Value<int>("protocolVersion"));
        Assert.True(message["error"]!.Value<bool>("retryable"));
        Assert.Equal(256, message["error"]!["details"]!.Value<int>("limit"));
        Assert.Null(message["result"]);
    }

    [Fact]
    public void ReplacesOversizedResponseWithBoundedTypedFailure()
    {
        var response = BridgeResponse.Success(
            "r1",
            new JObject
            {
                ["value"] = new string('x', BridgeProtocol.MaximumMessageLength),
            },
            protocolVersion: 2);

        var json = BridgeProtocol.SerializeResponse(response);
        var message = JObject.Parse(json);

        Assert.True(json.Length <= BridgeProtocol.MaximumMessageLength);
        Assert.Equal("response_too_large", message["error"]!.Value<string>("code"));
        Assert.False(message["error"]!.Value<bool>("retryable"));
        Assert.Null(message["result"]);
    }

    [Fact]
    public void ReplacesOverlyDeepResponseWithBoundedTypedFailure()
    {
        JToken nested = new JValue(0);
        for (var index = 0; index < BridgeProtocol.MaximumNestingDepth + 4; index++)
        {
            nested = new JObject { ["value"] = nested };
        }

        var json = BridgeProtocol.SerializeResponse(
            BridgeResponse.Success("r1", nested, protocolVersion: 2));
        var message = JObject.Parse(json);

        Assert.Equal("response_too_large", message["error"]!.Value<string>("code"));
    }

    [Theory]
    [InlineData("game.state", true)]
    [InlineData("menu.itemSelected", true)]
    [InlineData("overlay.snapshot", true)]
    [InlineData("state", false)]
    [InlineData("Game.state", false)]
    [InlineData("game state", false)]
    [InlineData("game.__proto__!", false)]
    public void ValidatesEventNames(string eventName, bool expected)
    {
        Assert.Equal(expected, BridgeProtocol.IsValidEventName(eventName));
    }

    [Fact]
    public void SerializesBoundedProtocolTwoEvent()
    {
        var json = BridgeProtocol.SerializeEvent(
            "operation.completed",
            new JObject { ["operationId"] = "op-1" },
            protocolVersion: 2);
        var message = JObject.Parse(json);

        Assert.Equal(2, message.Value<int>("protocolVersion"));
        Assert.Equal("op-1", message["payload"]!.Value<string>("operationId"));
    }

    [Fact]
    public void RejectsInvalidOrOversizedEvents()
    {
        Assert.Throws<ArgumentException>(() =>
            BridgeProtocol.SerializeEvent("invalid", new JObject()));
        Assert.Throws<InvalidOperationException>(() =>
            BridgeProtocol.SerializeEvent(
                "game.state",
                new JValue(new string('x', BridgeProtocol.MaximumMessageLength))));

        JToken nested = new JValue(0);
        for (var index = 0; index < BridgeProtocol.MaximumNestingDepth + 4; index++)
        {
            nested = new JObject { ["value"] = nested };
        }
        Assert.Throws<InvalidOperationException>(() =>
            BridgeProtocol.SerializeEvent("game.state", nested));
    }

    [Fact]
    public void RejectsInvalidOutboundResponseId()
    {
        Assert.Throws<ArgumentException>(() =>
            BridgeProtocol.SerializeResponse(BridgeResponse.Success("bad id")));
    }

    [Fact]
    public void BridgeErrorValidatesAndClonesStructuredDetails()
    {
        var details = new JObject { ["field"] = "level" };
        var error = new BridgeError(
            "invalid_params",
            "Invalid level.",
            retryable: false,
            details: details);
        details["field"] = "changed";

        Assert.Equal("level", error.Details!.Value<string>("field"));
        Assert.Throws<ArgumentException>(() => new BridgeError("Bad Code", "Message"));
        var bounded = new BridgeError(
            "invalid_params",
            new string('x', BridgeError.MaximumMessageLength + 1));
        Assert.Equal(BridgeError.MaximumMessageLength, bounded.Message.Length);
    }
}
