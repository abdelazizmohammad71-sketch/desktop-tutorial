using ZX0ai.Core.Models;
using ZX0ai.Core.Security;

namespace ZX0ai.Core.Sessions;

/// <summary>Persisted conversation bound to exactly one workspace policy.</summary>
public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string? ProjectId { get; set; }

    public string Title { get; set; } = "New chat";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public SandboxMode Sandbox { get; set; } = SandboxMode.ReadOnly;

    public ApprovalPolicy Approval { get; set; } = ApprovalPolicy.OnRequest;

    public bool NetworkEnabled { get; set; }

    public bool FullAccessConfirmed { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];
}
