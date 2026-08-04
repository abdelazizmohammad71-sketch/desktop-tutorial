using System.Text.Json;
using Xunit;
using ZX0ai.Core.Providers;

namespace ZX0ai.Tests;

/// <summary>Covers the SSE wire format without needing a socket.</summary>
public sealed class SseParserTests
{
    /// <summary>Feeds every line and collects the completed event payloads.</summary>
    private static List<string> Drain(params string?[] lines)
    {
        var parser = new SseParser();
        var events = new List<string>();

        foreach (var line in lines)
        {
            if (parser.TryFeedLine(line, out var payload))
            {
                events.Add(payload);
            }
        }

        return events;
    }

    [Fact]
    public void SingleEvent_IsEmittedOnTheBlankLine()
    {
        Assert.Equal(["hello"], Drain("data: hello", ""));
    }

    [Fact]
    public void EventIsNotEmittedUntilTerminated()
    {
        var parser = new SseParser();
        Assert.False(parser.TryFeedLine("data: partial", out _));
    }

    [Fact]
    public void MultipleEvents_AreEmittedInOrder()
    {
        Assert.Equal(["one", "two"], Drain("data: one", "", "data: two", ""));
    }

    [Fact]
    public void MultiLineData_IsJoinedWithNewlines()
    {
        Assert.Equal(["first\nsecond"], Drain("data: first", "data: second", ""));
    }

    [Fact]
    public void CommentLines_AreIgnored()
    {
        // OpenRouter sends ": OPENROUTER PROCESSING" as a keep-alive.
        Assert.Equal(["payload"], Drain(": OPENROUTER PROCESSING", "data: payload", ""));
    }

    [Fact]
    public void NonDataFields_AreIgnored()
    {
        Assert.Equal(["payload"], Drain("event: message", "id: 42", "retry: 100", "data: payload", ""));
    }

    [Fact]
    public void OnlyOneLeadingSpaceIsStripped()
    {
        Assert.Equal(["  padded"], Drain("data:   padded", ""));
    }

    [Fact]
    public void DataWithNoSpace_IsRead()
    {
        Assert.Equal(["tight"], Drain("data:tight", ""));
    }

    [Fact]
    public void JsonPayloadContainingColons_SurvivesIntact()
    {
        const string json = """{"choices":[{"delta":{"content":"a: b"}}]}""";
        Assert.Equal([json], Drain("data: " + json, ""));
    }

    [Fact]
    public void EndOfStream_FlushesAnUnterminatedEvent()
    {
        // A dropped connection must not silently discard the last chunk.
        Assert.Equal(["trailing"], Drain("data: trailing", null));
    }

    [Fact]
    public void EndOfStream_WithNothingBuffered_EmitsNothing()
    {
        // Bracketed so null is the single element, not the whole array.
        Assert.Empty(Drain([null]));
    }

    [Fact]
    public void BlankLines_BetweenEventsProduceNothing()
    {
        Assert.Equal(["one"], Drain("", "", "data: one", "", ""));
    }

    [Fact]
    public void EmptyDataLine_StillCountsAsAnEvent()
    {
        Assert.Equal([""], Drain("data:", ""));
    }

    [Theory]
    [InlineData("[DONE]", true)]
    [InlineData(" [DONE] ", true)]
    [InlineData("[done]", false)]
    [InlineData("{\"choices\":[]}", false)]
    public void IsDone_RecognisesTheSentinel(string payload, bool expected)
    {
        Assert.Equal(expected, SseParser.IsDone(payload));
    }

    [Fact]
    public void RealisticStream_ParsesToTheExpectedChunks()
    {
        var events = Drain(
            ": OPENROUTER PROCESSING",
            "",
            """data: {"id":"gen-1","choices":[{"delta":{"content":"Hel"}}]}""",
            "",
            """data: {"id":"gen-1","choices":[{"delta":{"content":"lo"}}]}""",
            "",
            "data: [DONE]",
            "");

        Assert.Equal(3, events.Count);
        Assert.True(SseParser.IsDone(events[2]));

        var text = string.Concat(events
            .Take(2)
            .Select(e => JsonDocument.Parse(e)
                .RootElement.GetProperty("choices")[0]
                .GetProperty("delta").GetProperty("content").GetString()));

        Assert.Equal("Hello", text);
    }
}

/// <summary>Covers reassembly of tool calls split across streamed chunks.</summary>
public sealed class ToolCallAccumulatorTests
{
    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void NoFragments_CompletesEmpty()
    {
        Assert.Empty(new ToolCallAccumulator().Complete());
    }

    [Fact]
    public void FragmentedArguments_AreConcatenated()
    {
        var accumulator = new ToolCallAccumulator();

        accumulator.Feed(Element("""
            [{"index":0,"id":"call_a","function":{"name":"fetch_url","arguments":"{\"url\":"}}]
            """));
        accumulator.Feed(Element("""
            [{"index":0,"function":{"arguments":"\"https://x.dev\"}"}}]
            """));

        var call = Assert.Single(accumulator.Complete());
        Assert.Equal("call_a", call.Id);
        Assert.Equal("fetch_url", call.Name);
        Assert.Equal("https://x.dev", call.ParseArguments().GetProperty("url").GetString());
    }

    [Fact]
    public void ParallelCalls_AreKeptSeparateAndOrdered()
    {
        var accumulator = new ToolCallAccumulator();

        accumulator.Feed(Element("""
            [{"index":1,"id":"b","function":{"name":"second","arguments":"{}"}},
             {"index":0,"id":"a","function":{"name":"first","arguments":"{}"}}]
            """));

        var calls = accumulator.Complete();

        Assert.Equal(2, calls.Count);
        Assert.Equal("first", calls[0].Name);
        Assert.Equal("second", calls[1].Name);
    }

    [Fact]
    public void FragmentWithoutAName_IsDropped()
    {
        var accumulator = new ToolCallAccumulator();
        accumulator.Feed(Element("""[{"index":0,"function":{"arguments":"{}"}}]"""));

        Assert.Empty(accumulator.Complete());
    }

    [Fact]
    public void MissingId_FallsBackToTheIndex()
    {
        var accumulator = new ToolCallAccumulator();
        accumulator.Feed(Element("""[{"index":3,"function":{"name":"run","arguments":"{}"}}]"""));

        Assert.Equal("call_3", Assert.Single(accumulator.Complete()).Id);
    }

    [Fact]
    public void Complete_ResetsForTheNextTurn()
    {
        var accumulator = new ToolCallAccumulator();
        accumulator.Feed(Element("""[{"index":0,"id":"a","function":{"name":"run","arguments":"{}"}}]"""));

        Assert.Single(accumulator.Complete());
        Assert.Empty(accumulator.Complete());
    }

    [Fact]
    public void MalformedArguments_ParseToAnEmptyObject()
    {
        // Models do emit truncated JSON; it must not throw at the call site.
        var call = new ZX0ai.Core.Skills.ToolCall("id", "run", "{\"broken\":");

        Assert.Equal(JsonValueKind.Object, call.ParseArguments().ValueKind);
        Assert.Empty(call.ParseArguments().EnumerateObject());
    }
}
