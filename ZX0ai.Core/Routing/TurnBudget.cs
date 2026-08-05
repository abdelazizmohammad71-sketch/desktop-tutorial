namespace ZX0ai.Core.Routing;

/// <summary>
/// Per-turn delegation budget, isolated from every other turn.
/// </summary>
/// <remarks>
/// A value type held by the caller: no shared mutable state survives between turns,
/// so two concurrent conversations cannot bleed budget into each other.
/// </remarks>
public readonly record struct TurnBudget(int Spent, int Limit)
{
    public int Remaining => Math.Max(0, Limit - Spent);
    public TurnBudget Spend() => this with { Spent = Spent + 1 };
}
