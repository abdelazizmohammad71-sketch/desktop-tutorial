using System.Collections.Concurrent;
using System.Text;
using ZX0ai.Core.Models;

namespace ZX0ai.Backend;

/// <summary>
/// Bounded, in-memory observability for active/recent runs. It stores only the
/// user-visible transcript metadata emitted by the orchestrator, never prompts,
/// credentials, tool arguments or project paths.
/// </summary>
internal sealed class AgentRunStore
{
    private const int MaximumRuns = 50;
    private readonly ConcurrentDictionary<string, MutableRun> _runs = new(StringComparer.Ordinal);

    internal AgentRunSnapshot Create(ModelTier tier)
    {
        var run = new MutableRun(Guid.NewGuid().ToString("N"), tier.Key, tier.DisplayName);
        _runs[run.Id] = run;
        Trim();
        return run.Snapshot();
    }

    internal void StartTurn(string runId, AgentTurn turn) =>
        Find(runId)?.StartTurn(turn);

    internal void AppendTurn(string runId, string turnId, string text) =>
        Find(runId)?.AppendTurn(turnId, text);

    internal void CompleteTurn(string runId, AgentTurn turn) =>
        Find(runId)?.CompleteTurn(turn);

    internal void AppendAnswer(string runId, string text) =>
        Find(runId)?.AppendAnswer(text);

    internal void Complete(string runId) => Find(runId)?.Finish(AgentRunStatus.Completed, null);

    internal void Fail(string runId, string code) => Find(runId)?.Finish(AgentRunStatus.Failed, code);

    internal void Cancel(string runId) => Find(runId)?.Finish(AgentRunStatus.Canceled, null);

    internal bool TryGet(string runId, out AgentRunSnapshot snapshot)
    {
        if (_runs.TryGetValue(runId, out var run))
        {
            snapshot = run.Snapshot();
            return true;
        }

        snapshot = null!;
        return false;
    }

    private MutableRun? Find(string runId) =>
        _runs.TryGetValue(runId, out var run) ? run : null;

    private void Trim()
    {
        var excess = _runs.Count - MaximumRuns;
        if (excess <= 0)
        {
            return;
        }

        foreach (var stale in _runs.Values
                     .OrderBy(run => run.StartedAt)
                     .Take(excess)
                     .ToList())
        {
            _runs.TryRemove(stale.Id, out _);
        }
    }

    private sealed class MutableRun(string id, string tier, string tierDisplayName)
    {
        private const int MaximumTurns = 100;
        private const int MaximumTurnCharacters = 128 * 1024;
        private const int MaximumAnswerCharacters = 256 * 1024;

        private readonly object _gate = new();
        private readonly List<MutableTurn> _turns = [];
        private readonly Dictionary<string, MutableTurn> _turnById = new(StringComparer.Ordinal);
        private readonly StringBuilder _answer = new();
        private bool _answerTruncated;
        private AgentRunStatus _status = AgentRunStatus.Running;
        private DateTimeOffset? _completedAt;
        private string? _failureCode;

        internal string Id { get; } = id;

        internal string Tier { get; } = tier;

        internal string TierDisplayName { get; } = tierDisplayName;

        internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        internal void StartTurn(AgentTurn turn)
        {
            lock (_gate)
            {
                if (_turnById.ContainsKey(turn.Id) || _turns.Count >= MaximumTurns)
                {
                    return;
                }

                var snapshot = new MutableTurn(turn);
                _turnById[turn.Id] = snapshot;
                _turns.Add(snapshot);
            }
        }

        internal void AppendTurn(string turnId, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_gate)
            {
                if (_turnById.TryGetValue(turnId, out var turn))
                {
                    turn.Append(text, MaximumTurnCharacters);
                }
            }
        }

        internal void CompleteTurn(AgentTurn turn)
        {
            lock (_gate)
            {
                if (_turnById.TryGetValue(turn.Id, out var stored))
                {
                    stored.Status = turn.Status;
                    stored.CompletedAt = turn.CompletedAt ?? DateTimeOffset.UtcNow;
                    stored.ReasoningSummary = SafeSummary(turn.ReasoningSummary);
                }
            }
        }

        internal void AppendAnswer(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_gate)
            {
                AppendBounded(_answer, text, MaximumAnswerCharacters, ref _answerTruncated);
            }
        }

        internal void Finish(AgentRunStatus status, string? failureCode)
        {
            lock (_gate)
            {
                if (_status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Canceled)
                {
                    return;
                }

                _status = status;
                _failureCode = failureCode;
                _completedAt = DateTimeOffset.UtcNow;
            }
        }

        internal AgentRunSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new AgentRunSnapshot(
                    Id,
                    Tier,
                    TierDisplayName,
                    _status,
                    StartedAt,
                    _completedAt,
                    _turns.Select(turn => turn.Snapshot()).ToList(),
                    _answer.ToString(),
                    _answerTruncated,
                    _failureCode);
            }
        }

        private static string SafeSummary(string? value)
        {
            const int maximum = 512;
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Length <= maximum ? value : value[..maximum];
        }

        private static void AppendBounded(
            StringBuilder target,
            string text,
            int maximum,
            ref bool truncated)
        {
            if (target.Length >= maximum)
            {
                truncated = true;
                return;
            }

            var remaining = maximum - target.Length;
            if (text.Length <= remaining)
            {
                target.Append(text);
                return;
            }

            target.Append(text.AsSpan(0, remaining));
            truncated = true;
        }

        private sealed class MutableTurn
        {
            private readonly StringBuilder _content = new();
            private bool _truncated;

            internal MutableTurn(AgentTurn turn)
            {
                Id = turn.Id;
                AgentId = turn.AgentId;
                AgentName = turn.AgentName;
                Role = turn.Role.ToString().ToLowerInvariant();
                Model = turn.Model;
                AccentArgb = turn.AccentArgb;
                ReasoningSummary = SafeSummary(turn.ReasoningSummary);
                Status = turn.Status;
                FinalAnswer = turn.IsFinalAnswer;
                StartedAt = turn.StartedAt;
                CompletedAt = turn.CompletedAt;
            }

            internal string Id { get; }

            internal string AgentId { get; }

            internal string AgentName { get; }

            internal string Role { get; }

            internal string Model { get; }

            internal uint AccentArgb { get; }

            internal string ReasoningSummary { get; set; }

            internal AgentStatus Status { get; set; }

            internal bool FinalAnswer { get; }

            internal DateTimeOffset StartedAt { get; }

            internal DateTimeOffset? CompletedAt { get; set; }

            internal void Append(string text, int maximum) =>
                AppendBounded(_content, text, maximum, ref _truncated);

            internal AgentTurnSnapshot Snapshot() => new(
                Id,
                AgentId,
                AgentName,
                Role,
                Model,
                AccentArgb,
                ReasoningSummary,
                _content.ToString(),
                Status,
                FinalAnswer,
                StartedAt,
                CompletedAt,
                _truncated);
        }
    }
}
