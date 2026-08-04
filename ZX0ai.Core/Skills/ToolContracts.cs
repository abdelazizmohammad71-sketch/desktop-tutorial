using System.Text.Json;

namespace ZX0ai.Core.Skills;

/// <summary>
/// A skill exposed to a model as a callable tool.
/// </summary>
/// <param name="Name">Stable identifier the model calls, e.g. <c>fetch_url</c>.</param>
/// <param name="Description">What it does, in the model's words.</param>
/// <param name="ParametersSchema">JSON Schema for the arguments object.</param>
public sealed record ToolDefinition(string Name, string Description, JsonElement ParametersSchema)
{
    /// <summary>Projects onto the OpenAI-compatible <c>tools</c> array element.</summary>
    public Dictionary<string, object?> ToWire() => new()
    {
        ["type"] = "function",
        ["function"] = new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = ParametersSchema,
        },
    };
}

/// <summary>One tool invocation requested by a model.</summary>
/// <param name="Id">Correlates the result back to the call.</param>
/// <param name="Name">Skill name.</param>
/// <param name="ArgumentsJson">Raw JSON object of arguments, as the model wrote it.</param>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson)
{
    /// <summary>
    /// Parses the arguments, tolerating the empty or malformed objects models
    /// occasionally emit. Returns an empty object rather than throwing.
    /// </summary>
    public JsonElement ParseArguments()
    {
        if (string.IsNullOrWhiteSpace(ArgumentsJson))
        {
            return EmptyObject();
        }

        try
        {
            using var document = JsonDocument.Parse(ArgumentsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

/// <summary>Outcome of running a skill.</summary>
/// <param name="Success">False when the skill could not complete.</param>
/// <param name="Content">Text fed back to the model as the tool result.</param>
/// <param name="Summary">One-line human-readable summary for the command card.</param>
public sealed record SkillResult(bool Success, string Content, string? Summary = null)
{
    public static SkillResult Ok(string content, string? summary = null) =>
        new(true, content, summary);

    public static SkillResult Fail(string message) =>
        new(false, message, message);
}
