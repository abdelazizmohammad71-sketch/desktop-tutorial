namespace ZX0ai.Core.Routing;

/// <summary>How much work a request represents.</summary>
public enum TaskSize
{
    /// <summary>A question, a chat turn, one small edit. One model, no helpers.</summary>
    Small,

    /// <summary>Real work, but not a project. One helper at most.</summary>
    Medium,

    /// <summary>An application, a system, a large refactor. The whole team.</summary>
    Large,
}

/// <summary>
/// Decides how much machinery a request is worth.
/// </summary>
/// <remarks>
/// <para>
/// This is the failsafe, not the decision. The orchestrator chooses whether to delegate
/// at all; this only caps how far it can go, so a misjudgement costs one wasted call
/// rather than six. Deterministic on purpose — asking a model how big a job is would add
/// a round trip to every "hi", and the answer would still need a ceiling.
/// </para>
/// <para>
/// Biased toward <see cref="TaskSize.Small"/>. Spinning up a team for a question is
/// slow, expensive and worse than answering it: the failure the user actually notices is
/// a trivial request taking thirty seconds, not a large one using fewer specialists than
/// it could have.
/// </para>
/// </remarks>
public static class TaskClassifier
{
    /// <summary>Words that only appear when someone wants something built.</summary>
    private static readonly string[] LargeMarkers =
    [
        "build", "create an app", "create a web", "make an app", "make a game",
        "application", "website", "web app", "saas", "backend", "frontend",
        "full-stack", "full stack", "architecture", "refactor", "migrate",
        "redesign", "audit", "optimi", "implement", "scaffold", "boilerplate",
        "microservice", "database schema", "rest api", "authentication system",
        "test suite", "ci/cd", "pipeline", "dashboard", "platform",
    ];

    /// <summary>Requests that are answered, not executed.</summary>
    private static readonly string[] SmallMarkers =
    [
        "hi", "hello", "hey", "thanks", "thank you", "what is", "what's",
        "who is", "explain", "translate", "summarise", "summarize", "define",
        "how do i", "how does", "why does", "what does", "tell me",
    ];

    /// <summary>
    /// Classifies a request.
    /// </summary>
    /// <param name="request">What the user asked for.</param>
    /// <param name="hasWorkspace">
    /// Whether a folder is bound. Without one the agent cannot create anything, so a
    /// request to build is a conversation about building and never warrants a team.
    /// </param>
    public static TaskSize Classify(string request, bool hasWorkspace)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            return TaskSize.Small;
        }

        var text = request.Trim();
        var lower = text.ToLowerInvariant();

        // A short message that opens with a conversational marker is conversation,
        // whatever words follow it. "hi, can you build me an app" is still a greeting
        // being answered, not a project being started.
        if (text.Length < 60 && SmallMarkers.Any(marker =>
                lower.StartsWith(marker, StringComparison.Ordinal)))
        {
            return TaskSize.Small;
        }

        if (!hasWorkspace)
        {
            // Nothing can be produced on disk, so the ceiling is one helper — enough to
            // give a considered answer, not enough to simulate a build.
            return text.Length > 240 ? TaskSize.Medium : TaskSize.Small;
        }

        var markers = LargeMarkers.Count(marker => lower.Contains(marker, StringComparison.Ordinal));

        // Two independent signals, because either alone is too easy to trip. A long
        // message is often just a long question, and one keyword is often incidental.
        if (markers >= 2 || (markers >= 1 && text.Length > 120))
        {
            return TaskSize.Large;
        }

        return markers >= 1 || text.Length > 200 ? TaskSize.Medium : TaskSize.Small;
    }

    /// <summary>How many specialists the orchestrator may call for a request this size.</summary>
    public static int HelperBudget(TaskSize size) => size switch
    {
        TaskSize.Small => 0,
        TaskSize.Medium => 1,
        _ => 6,
    };
}
