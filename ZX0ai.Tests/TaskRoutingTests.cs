using Xunit;
using ZX0ai.Core.Routing;

namespace ZX0ai.Tests;

/// <summary>
/// The routing failsafe.
/// </summary>
/// <remarks>
/// These pin the ceiling, not the decision. The orchestrator chooses whether to
/// delegate; the classifier only bounds how far it can go. The bias under test is
/// deliberate: a question answered by a team is a failure the user feels immediately,
/// while a large job done with fewer specialists than it could have used still ships.
/// </remarks>
public sealed class TaskRoutingTests
{
    [Theory]
    [InlineData("hi")]
    [InlineData("hello there")]
    [InlineData("thanks!")]
    [InlineData("what is a closure")]
    [InlineData("explain this function")]
    [InlineData("translate this to Arabic")]
    public void Conversation_NeverActivatesTheTeam(string request)
    {
        Assert.Equal(TaskSize.Small, TaskClassifier.Classify(request, hasWorkspace: true));
        Assert.Equal(0, TaskClassifier.HelperBudget(TaskSize.Small));
    }

    [Theory]
    [InlineData("build a complete task manager application with a backend and a database schema")]
    [InlineData("create a web app for invoicing with authentication system and a rest api")]
    [InlineData("do a full refactor of the architecture and then a security audit of the result")]
    public void RealProjects_ActivateTheFullTeam(string request)
    {
        Assert.Equal(TaskSize.Large, TaskClassifier.Classify(request, hasWorkspace: true));
        Assert.Equal(6, TaskClassifier.HelperBudget(TaskSize.Large));
    }

    /// <summary>
    /// A greeting stays a greeting even when it mentions building something.
    /// </summary>
    /// <remarks>
    /// The case that would otherwise trip the keyword scan. Someone opening with "hi,
    /// can you build apps?" is asking a question, and answering it with a six-model
    /// pipeline is the single worst outcome this classifier exists to prevent.
    /// </remarks>
    [Fact]
    public void GreetingThatMentionsBuilding_IsStillAGreeting()
    {
        Assert.Equal(
            TaskSize.Small,
            TaskClassifier.Classify("hi, can you build an app?", hasWorkspace: true));
    }

    /// <summary>
    /// Without a folder nothing can be produced, so no request warrants a team.
    /// </summary>
    [Fact]
    public void WithoutAWorkspace_TheTeamIsNeverActivated()
    {
        var request = "build a complete web application with a backend, a database schema " +
                      "and a full authentication system, then audit it for security";

        Assert.NotEqual(TaskSize.Large, TaskClassifier.Classify(request, hasWorkspace: false));
    }

    [Fact]
    public void MediumWork_GetsExactlyOneHelper()
    {
        Assert.Equal(1, TaskClassifier.HelperBudget(TaskSize.Medium));
    }

    /// <summary>One keyword on its own is not a project; it is usually a question about one.</summary>
    [Fact]
    public void SingleKeywordInAShortMessage_DoesNotEscalateToLarge()
    {
        Assert.NotEqual(
            TaskSize.Large,
            TaskClassifier.Classify("what does refactor mean", hasWorkspace: true));
    }

    [Fact]
    public void EmptyRequest_IsSmall()
    {
        Assert.Equal(TaskSize.Small, TaskClassifier.Classify("   ", hasWorkspace: true));
    }
}
