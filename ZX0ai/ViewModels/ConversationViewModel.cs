using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Models;
using ZX0ai.Core.Routing;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Services;
using ZX0ai.Core.Sessions;
using ZX0ai.Core.Skills;
using ZX0ai.Services;

namespace ZX0ai.ViewModels;

/// <summary>How much the agent may do before it has to ask.</summary>
public enum AccessMode
{
    /// <summary>Confirm every write and every command.</summary>
    AskForApproval,

    /// <summary>Act inside the folder; ask only for anything the policy flags.</summary>
    ApproveForMe,

    /// <summary>Act freely inside the folder. Dangerous commands still confirm.</summary>
    FullAccess,
}

/// <summary>What the run panel is currently reporting.</summary>
public enum RunState
{
    Idle,
    Sending,
    Streaming,

    /// <summary>Reading, writing or running something in the workspace.</summary>
    UsingTools,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// The conversation: the transcript, the turn that is running, and the history rail.
/// </summary>
/// <remarks>
/// <para>
/// One view model for the whole shell rather than one per region, because all three
/// regions are views onto a single fact — which conversation is open and what it is
/// doing. Splitting them would mean keeping three copies of that in sync.
/// </para>
/// <para>
/// Nothing here is illustrative. The rail lists conversations that exist on disk, the
/// model name is the one that will actually be called, and the run panel reports a turn
/// that really happened. When there is no credential the product says so instead of
/// showing a conversation that cannot occur.
/// </para>
/// </remarks>
public sealed partial class ConversationViewModel : ObservableObject
{
    /// <summary>
    /// How many stream-then-call-tools cycles one turn may take.
    /// </summary>
    /// <remarks>
    /// A ceiling, not a target. A model that has misread a failing tool result will
    /// happily retry the same call forever; this bounds the cost of that in tokens and
    /// in wall-clock. Reaching it is reported to the user rather than passed off as a
    /// finished answer.
    /// </remarks>
    private const int MaxToolRounds = 12;

    private readonly IChatProvider _provider;
    private readonly IConfigService _config;
    private readonly SessionStore _store;
    private readonly ToolRunner _tools;
    private readonly AgentTeam _team;
    private readonly AgentWorkspace _workspace;
    private readonly ILogger<ConversationViewModel> _logger;
    private readonly Stopwatch _turnClock = new();

    private CancellationTokenSource? _turn;
    private int _turnCharacters;
    private double _lastRateSample;

    [ObservableProperty]
    private ChatSession _session = new();

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private RunState _runState = RunState.Idle;

    /// <summary>Provider failure in the user's terms, or null.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Set when the turn actually ran on a different tier than the one selected.
    /// </summary>
    /// <remarks>
    /// The one honest way to do an automatic fallback: the answer is real, but it did
    /// not come from what the model chip says, and saying so out loud is what "never
    /// silently claim one model while another handled the request" actually means in
    /// practice.
    /// </remarks>
    [ObservableProperty]
    private string? _fallbackNotice;

    [ObservableProperty]
    private double _tokensPerSecond;

    [ObservableProperty]
    private int _turnTokens;

    [ObservableProperty]
    private double _elapsedSeconds;

    /// <summary>Text typed in the rail's search box; filters <see cref="History"/>.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// How hard the model is asked to think: light, medium, high, extra-high, ultra.
    /// </summary>
    /// <remarks>
    /// This is the real control. It maps onto the provider's reasoning settings through
    /// the capability adapter, so raising effort is what changes behaviour — it is not a
    /// second setting that has to be kept in sync with the capability choice.
    /// </remarks>
    [ObservableProperty]
    private string _effort = "high";

    /// <summary>Standard, or Fast for the provider's accelerated route.</summary>
    [ObservableProperty]
    private string _speed = "Standard";

    public ConversationViewModel(
        IChatProvider provider,
        IConfigService config,
        SessionStore store,
        ToolRunner tools,
        AgentTeam team,
        AgentWorkspace workspace,
        ILogger<ConversationViewModel> logger)
    {
        _provider = provider;
        _config = config;
        _store = store;
        _tools = tools;
        _team = team;
        _workspace = workspace;
        _logger = logger;

        _tools.ToolStarted += (_, run) => ToolRuns.Add(run);
        _tools.ToolFinished += (_, _) => OnPropertyChanged(nameof(ToolRuns));
        _workspace.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(WorkspaceName));
            OnPropertyChanged(nameof(IsWorkspaceBound));
            OnPropertyChanged(nameof(AccessMode));
        };

        RefreshHistory();
    }

    /// <summary>The open transcript.</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    /// <summary>Conversations on disk, newest first, filtered by <see cref="SearchText"/>.</summary>
    public ObservableCollection<HistoryRow> History { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    /// <summary>False before the first conversation is saved; the rail says so.</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>Tool calls made during the current turn, in order.</summary>
    public ObservableCollection<ToolRun> ToolRuns { get; } = [];

    /// <summary>Name of the bound folder, for the title-bar chip.</summary>
    public string WorkspaceName => _workspace.DisplayName;

    public bool IsWorkspaceBound => _workspace.IsBound;

    /// <summary>Full path of the bound folder, for the tooltip.</summary>
    public string? WorkspacePath => _workspace.Root;

    /// <summary>
    /// What the agent may do without stopping to ask.
    /// </summary>
    /// <remarks>
    /// Maps onto the execution policy. Only the top step lifts the sandbox to full
    /// access; none of the three widens the path boundary, and the confirmation for
    /// dangerous commands survives all of them.
    /// </remarks>
    public AccessMode AccessMode
    {
        get => _workspace.FullAccess ? AccessMode.FullAccess : AccessMode.ApproveForMe;
        set
        {
            _workspace.FullAccess = value == AccessMode.FullAccess;
            OnPropertyChanged();
        }
    }

    /// <summary>Plan without touching anything. Sticky across turns on purpose.</summary>
    [ObservableProperty]
    private bool _planMode;

    /// <summary>Binds a folder and re-advertises the tools that folder makes possible.</summary>
    public void BindWorkspace(string path)
    {
        _workspace.Bind(path);
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(IsWorkspaceBound));
        OnPropertyChanged(nameof(WorkspacePath));
    }

    /// <summary>False when no API key is present; the shell says so rather than pretending.</summary>
    public bool IsConfigured => _provider.IsConfigured;

    /// <summary>
    /// The capability the user selected, e.g. <c>zax-pro</c>.
    /// </summary>
    /// <remarks>
    /// The tier key, never the model behind it. Which provider and which slug answer a
    /// turn is a routing decision that changes as models are deprecated and fallbacks
    /// engage; surfacing it would make the product look like it changed when only the
    /// routing did. Every visible surface reads this or <see cref="ModelBadge"/>.
    /// </remarks>
    public string ModelName => _config.ActiveTier.Key;

    /// <summary>The effort word shown beside the capability on the composer pill.</summary>
    public string ModelBadge => Effort switch
    {
        "light" => "Light",
        "medium" => "Medium",
        "high" => "High",
        "extra-high" => "Extra High",
        "ultra" => "Ultra",
        _ => "High",
    };

    /// <summary>Every configured capability, in configuration order.</summary>
    public IReadOnlyList<string> Tiers => [.. _config.Tiers.Select(tier => tier.Key)];

    /// <summary>Selects a capability for this session.</summary>
    public void SelectTier(string key)
    {
        if (!_config.SelectActiveTier(key))
        {
            return;
        }

        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(ModelBadge));
    }

    /// <summary>
    /// The slug to actually call, and the effort profile to call it with.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="TeamMember.EffectiveModel"/> rather than the tier's raw
    /// <c>Leader</c> string. The configured slug is what someone asked for; the catalog
    /// validates it at startup and substitutes a declared fallback when the requested
    /// model no longer exists upstream. Reading the raw value would send a slug the
    /// provider has already been shown to reject — which is exactly the failure that
    /// made this necessary.
    /// </remarks>
    private (string Slug, string Requested, string Effort) Resolve(ModelTier tier)
    {
        if (tier.Mode == TeamMode.Single)
        {
            var single = tier.Model ?? string.Empty;
            return (single, single, Effort);
        }

        var leader = tier.LeaderMember;

        // The user's effort wins over the tier's configured default. The configured
        // value seeds the selector; once someone has chosen, that is the answer.
        return leader is null
            ? (tier.Leader ?? string.Empty, tier.Leader ?? string.Empty, Effort)
            : (leader.EffectiveModel, leader.RequestedSlug, Effort);
    }

    /// <summary>The signed-in name, from configuration. There is no account system.</summary>
    public string UserName =>
        string.IsNullOrWhiteSpace(_config.Options.Ui.UserName)
            ? Environment.UserName
            : _config.Options.Ui.UserName;

    /// <summary>Raised whenever the transcript grows or a token lands, so the view can scroll.</summary>
    public event EventHandler? TranscriptChanged;

    // ============================== History =============================

    /// <summary>Re-reads the rail from disk.</summary>
    public void RefreshHistory()
    {
        History.Clear();

        var query = SearchText.Trim();
        foreach (var stored in _store.List())
        {
            if (query.Length == 0 ||
                stored.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                History.Add(new HistoryRow(stored, isActive: stored.Id == Session.Id));
            }
        }

        OnPropertyChanged(nameof(HasHistory));
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        RefreshHistory();
    }

    /// <summary>Opens a stored conversation.</summary>
    public void Open(string sessionId)
    {
        if (IsStreaming)
        {
            Cancel();
        }

        if (_store.Load(sessionId) is not { } stored)
        {
            // Listed a moment ago, gone now: another window deleted it. Re-read rather
            // than surfacing an error for something the user cannot act on.
            RefreshHistory();
            return;
        }

        Session = stored;
        Messages.Clear();
        foreach (var message in stored.Messages)
        {
            Messages.Add(message);
        }

        ErrorMessage = null;
        RunState = RunState.Idle;
        OnPropertyChanged(nameof(HasMessages));
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts an empty conversation. Nothing is written until the first turn.</summary>
    public void StartNew()
    {
        if (IsStreaming)
        {
            Cancel();
        }

        Session = new ChatSession();
        Messages.Clear();
        ErrorMessage = null;
        RunState = RunState.Idle;
        TokensPerSecond = 0;
        TurnTokens = 0;
        ElapsedSeconds = 0;

        OnPropertyChanged(nameof(HasMessages));
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
        RefreshHistory();
    }

    public void DeleteCurrent()
    {
        _store.Delete(Session.Id);
        StartNew();
    }

    // ============================ Turn cycle ============================

    /// <summary>Appends the prompt and streams the reply. Never throws.</summary>
    public async Task SendAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || IsStreaming)
        {
            return;
        }

        if (!_provider.IsConfigured)
        {
            ErrorMessage = "No API key. Set OPENROUTER_API_KEY and restart.";
            RunState = RunState.Failed;
            return;
        }

        var tier = _config.ActiveTier;
        var (model, requested, effort) = Resolve(tier);
        if (string.IsNullOrWhiteSpace(model))
        {
            ErrorMessage = $"No model configured for {tier.DisplayName}.";
            RunState = RunState.Failed;
            return;
        }

        ErrorMessage = null;
        FallbackNotice = null;

        Messages.Add(new ChatMessage { Role = ChatRole.User, Content = prompt.Trim() });
        OnPropertyChanged(nameof(HasMessages));

        var reply = new ChatMessage
        {
            Role = ChatRole.Assistant,
            IsStreaming = true,
            Model = model,
        };

        Messages.Add(reply);

        _turnCharacters = 0;
        _lastRateSample = 0;
        TokensPerSecond = 0;
        TurnTokens = 0;
        ElapsedSeconds = 0;
        _turnClock.Restart();

        IsStreaming = true;
        RunState = RunState.Sending;

        _turn?.Dispose();
        _turn = new CancellationTokenSource();

        try
        {
            // Announced inside the guarded region: rendering is the caller's code, and a
            // failure there must not take the turn down with it.
            TranscriptChanged?.Invoke(this, EventArgs.Empty);

            // Requested and resolved stay distinct so a fallback is visible to the
            // adapter rather than looking like the model that was asked for.
            var invocation = new ModelInvocation(requested, model, effort, Speed);

            await RunAgenticTurnAsync(invocation, reply, _turn.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Stopping is an outcome, not a failure: whatever streamed is kept.
            RunState = RunState.Cancelled;
        }
        catch (ChatProviderException ex)
        {
            if (await TryFallbackAsync(ex, tier, reply, _turn.Token).ConfigureAwait(true))
            {
                RunState = RunState.Done;
            }
            else
            {
                _logger.LogError(ex, "Turn failed: {Reason}.", ex.Reason);
                ErrorMessage = Describe(ex);
                RunState = RunState.Failed;
                DropEmptyReply(reply);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogError(ex, "Turn failed to reach the provider.");
            ErrorMessage = "Could not reach the provider. Check the network and try again.";
            RunState = RunState.Failed;
            DropEmptyReply(reply);
        }
        catch (Exception ex)
        {
            // Anything left is a defect, not a condition. It still has to reach the
            // surface: a turn that vanishes silently is the worst possible outcome.
            _logger.LogError(ex, "Turn failed unexpectedly.");
            ErrorMessage = $"Unexpected failure: {ex.Message}";
            RunState = RunState.Failed;
            DropEmptyReply(reply);
        }
        finally
        {
            _turnClock.Stop();
            PublishRate(force: true);
            reply.IsStreaming = false;
            IsStreaming = false;
            TranscriptChanged?.Invoke(this, EventArgs.Empty);

            Persist();
        }
    }

    /// <summary>The one tier a costly turn falls back to: free, and always configured.</summary>
    private const string FallbackTierKey = "zax-low-free";

    /// <summary>
    /// Retries the same turn on the fallback tier after a failure the fallback could
    /// plausibly fix, then says so.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow about which failures qualify. A bad request or an invalid
    /// key fails identically on every tier, so retrying it elsewhere would just spend a
    /// second turn confirming the same problem — this exists only for the failures that
    /// are properties of the <em>one provider call</em> rather than of the request:
    /// rate limiting, a server-side outage, and a credit shortfall the automatic
    /// token-reduction retry could not resolve on its own.
    /// </remarks>
    private async Task<bool> TryFallbackAsync(
        ChatProviderException original,
        ModelTier failedTier,
        ChatMessage reply,
        CancellationToken cancellationToken)
    {
        if (original.Reason is not (ChatFailureReason.RateLimited or
                                     ChatFailureReason.ServerError or
                                     ChatFailureReason.InsufficientCredits))
        {
            return false;
        }

        if (string.Equals(failedTier.Key, FallbackTierKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_config.FindTier(FallbackTierKey) is not { IsRunnable: true } fallbackTier)
        {
            return false;
        }

        var (fallbackModel, fallbackRequested, fallbackEffort) = Resolve(fallbackTier);
        if (string.IsNullOrWhiteSpace(fallbackModel))
        {
            return false;
        }

        _logger.LogWarning(
            "Falling back from {Original} to {Fallback} after {Reason}.",
            failedTier.Key,
            fallbackTier.Key,
            original.Reason);

        // A partial answer from the failed attempt must not bleed into the fallback's —
        // the two never came from the same model, so nothing about the first one is
        // still true once the second is what actually answers. The message's own Model
        // field stays as originally recorded (it is init-only, by design — nothing else
        // mutates it mid-turn either); FallbackNotice is the disclosure that matters,
        // since it is what actually reaches the screen.
        reply.Content = string.Empty;

        var invocation = new ModelInvocation(fallbackRequested, fallbackModel, fallbackEffort, Speed);

        try
        {
            await RunAgenticTurnAsync(invocation, reply, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or IOException)
        {
            _logger.LogWarning(ex, "Fallback to {Fallback} also failed.", fallbackTier.Key);
            return false;
        }

        FallbackNotice = $"{failedTier.DisplayName} was unavailable — answered using {fallbackTier.DisplayName} instead.";
        return true;
    }

    /// <summary>
    /// Streams, runs whatever tools the model asks for, feeds the results back, and
    /// repeats until it stops asking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wire history and the visible transcript are deliberately different lists. The
    /// provider needs every assistant tool-call message and every correlated tool result
    /// or it rejects the next round; the user needs the answer. Tool activity is surfaced
    /// through <see cref="ToolRuns"/> and the execution panel instead of being pasted
    /// into the conversation.
    /// </para>
    /// <para>
    /// The system prompt is rebuilt each turn from the live workspace, so binding a
    /// folder or switching to full access takes effect on the next message rather than
    /// on the next launch.
    /// </para>
    /// </remarks>
    private async Task RunAgenticTurnAsync(
        ModelInvocation invocation,
        ChatMessage reply,
        CancellationToken cancellationToken)
    {
        ToolRuns.Clear();

        // Sized once, from the request that opened the turn. The orchestrator decides
        // whether to delegate at all; this only caps how far it can go, so a request
        // read as smaller than it was costs a missed specialist rather than a runaway.
        var request = Messages.LastOrDefault(message => message.Role == ChatRole.User)?.Content ?? string.Empty;
        var size = TaskClassifier.Classify(request, _workspace.IsBound);
        var budget = TaskClassifier.HelperBudget(size);

        _team.BeginTurn(size);
        _tools.HelperBudget = budget;

        var tools = _tools.Tools;

        var wire = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.System,
                Content = LeaderPrompt.Build(_workspace.Current, tools.Count > 0, PlanMode, budget),
            },
        };

        // The reply being written is excluded: sending an empty assistant turn back to
        // the provider is rejected by some models and confuses the rest.
        wire.AddRange(Messages.Where(message =>
            !message.IsStreaming && message.Content.Length > 0));

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var calls = new List<ToolCall>();
            var roundText = new StringBuilder();

            await foreach (var delta in _provider
                .StreamAsync(invocation, wire, tools, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(true))
            {
                switch (delta.Kind)
                {
                    case ChatDeltaKind.Content:
                        RunState = RunState.Streaming;
                        roundText.Append(delta.Text);
                        reply.Content += delta.Text;
                        CountTokens(delta.Text);
                        TranscriptChanged?.Invoke(this, EventArgs.Empty);
                        break;

                    case ChatDeltaKind.ToolCall when delta.ToolCall is { } call:
                        calls.Add(call);
                        break;

                    default:
                        break;
                }
            }

            if (calls.Count == 0)
            {
                RunState = RunState.Done;
                return;
            }

            RunState = RunState.UsingTools;

            // Replayed verbatim next round. A tool result with no matching call is a
            // protocol error to every provider that supports tools.
            wire.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = roundText.ToString(),
                ToolCalls = calls,
            });

            foreach (var call in calls)
            {
                var result = await _tools.ExecuteAsync(call, cancellationToken).ConfigureAwait(true);

                wire.Add(new ChatMessage
                {
                    Role = ChatRole.Tool,
                    ToolCallId = call.Id,
                    Content = result.Content,
                });
            }

            // Separates one round's prose from the next in the visible reply.
            if (roundText.Length > 0)
            {
                reply.Content += "\n\n";
            }

            TranscriptChanged?.Invoke(this, EventArgs.Empty);
        }

        // Ran out of rounds with the model still calling tools. Say so: silently
        // returning a partial answer would read as a finished one.
        RunState = RunState.Failed;
        ErrorMessage =
            $"Stopped after {MaxToolRounds} tool rounds without a final answer. " +
            "The work so far is kept — send another message to continue.";
    }

    public void Cancel() => _turn?.Cancel();

    private void DropEmptyReply(ChatMessage reply)
    {
        if (reply.Content.Length == 0)
        {
            Messages.Remove(reply);
            OnPropertyChanged(nameof(HasMessages));
        }
    }

    private void Persist()
    {
        Session.Messages = [.. Messages];
        Session.Title = SessionStore.TitleFrom(Messages);
        _store.Save(Session);
        RefreshHistory();
    }

    /// <summary>
    /// Turns a provider failure into something the user can act on.
    /// </summary>
    /// <remarks>
    /// The reason, not the exception text. A raw provider message names hosts, headers
    /// and model slugs, which tells the reader nothing about what to do next.
    /// </remarks>
    private static string Describe(ChatProviderException ex) => ex.Reason switch
    {
        ChatFailureReason.NotConfigured => "No API key. Set OPENROUTER_API_KEY and restart.",
        ChatFailureReason.Unauthorized => "The provider rejected the credential.",
        ChatFailureReason.RateLimited => "Rate limited. Wait a moment and try again.",
        ChatFailureReason.Network => "Could not reach the provider.",
        ChatFailureReason.InsufficientCredits =>
            "The account cannot afford this reply's length. Add credit, or try a shorter request.",

        // The provider's own words. A generic line here ("the model could not complete
        // this turn") hides the one thing that distinguishes a retryable outage from a
        // model slug that no longer exists upstream — and the user can act on only one
        // of those.
        _ => string.IsNullOrWhiteSpace(ex.Message)
            ? "The model could not complete this turn."
            : ex.Message,
    };

    /// <summary>
    /// Updates the throughput readout.
    /// </summary>
    /// <remarks>
    /// Counted in characters and divided by four. The provider reports usage only after
    /// the stream ends, and four characters per token is the usual rule of thumb; this
    /// is a speedometer, not billing, so an estimate is the right instrument.
    /// </remarks>
    private void CountTokens(string text)
    {
        _turnCharacters += text.Length;
        PublishRate(force: false);
    }

    private void PublishRate(bool force)
    {
        var seconds = _turnClock.Elapsed.TotalSeconds;
        if (seconds <= 0 || (!force && seconds - _lastRateSample < 0.25))
        {
            return;
        }

        _lastRateSample = seconds;
        ElapsedSeconds = seconds;
        TurnTokens = _turnCharacters / 4;
        TokensPerSecond = TurnTokens / seconds;
    }
}
