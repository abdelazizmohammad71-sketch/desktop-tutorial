You are a senior Windows desktop engineer and UI systems architect specializing in C#, .NET, WinUI 3, Windows App SDK, MVVM, custom window chrome, responsive layouts, and production-grade AI applications.

Your task is to completely redesign and repair the existing ZX0ai Windows application.

TECHNOLOGY
- C#
- .NET
- WinUI 3
- Windows App SDK
- XAML
- MVVM
- Dependency Injection
- Async/await
- Native Windows title bar integration

INPUT IMAGES
- Image 1 is the FINAL DESIGN REFERENCE and the single visual source of truth.
- Images 2, 3, and 4 show the CURRENT/OLD ZX0ai application.
- Replace the old visual design with the reference design.
- The final result must match Image 1 as closely as technically possible, down to spacing, borders, proportions, typography, alignment, corner radii, shadows, panel sizing, and interaction states.
- Do not merely “take inspiration” from the reference.
- Reconstruct it faithfully.

PRIMARY OBJECTIVE
Transform the current ZX0ai interface into a polished, pixel-accurate implementation of the reference design while preserving and improving all existing application functionality.

Do not create a website, Electron app, WebView wrapper, HTML page, React interface, or mockup.

This must remain a real native C# WinUI 3 Windows desktop application.

EXECUTION RULES
1. Inspect the real repository before editing anything.
2. Identify:
   - Solution and project files
   - Current WinUI 3 architecture
   - Views and pages
   - ViewModels
   - Models
   - Services
   - AI provider integrations
   - Navigation system
   - File explorer
   - Terminal execution
   - Run status panel
   - Chat persistence
   - Settings and configuration
3. Never assume a file exists without checking.
4. Preserve working backend functionality.
5. Replace obsolete UI code instead of stacking another UI layer over it.
6. Remove dead, duplicated, temporary, and unused UI components after confirming they are not required.
7. Do not leave placeholder buttons, fake data, disconnected controls, TODO screens, or decorative controls without behavior.
8. Continue until the application builds and runs successfully.
9. Do not stop after creating only the shell.
10. Do not ask for approval after every file. Complete the full implementation.

FINAL VISUAL STRUCTURE

Build one unified window containing:

1. CUSTOM TOP TITLE BAR
2. LEFT HISTORY SIDEBAR
3. CENTRAL CHAT WORKSPACE
4. RIGHT TOOL EXECUTION PANEL

The entire application must feel like one continuous, restrained, monochrome workspace.

CUSTOM WINDOW

Implement a native WinUI 3 custom title bar using AppWindow and ExtendsContentIntoTitleBar.

Requirements:
- Borderless native-looking frame
- Correct draggable title-bar region
- Native minimize, maximize/restore, and close behavior
- Proper title-bar interaction when maximized
- Correct DPI scaling
- Correct behavior on multiple monitors
- Rounded outer corners when windowed
- No fake Windows caption buttons
- No double title bar
- No obsolete WPF window APIs
- No WinForms host

Default window:
- Approximately 1280 × 820
- Minimum usable size approximately 1050 × 680
- Centered when opened for the first time
- Restore previous size and position safely
- Prevent restoration outside visible monitor bounds

REFERENCE DESIGN LANGUAGE

Use a soft monochrome visual system.

Base palette:
- Main application surface: #EFEFEF to #F3F3F3
- Elevated surfaces: rgba(255,255,255,0.45–0.72)
- Primary text: #111111
- Secondary text: #686868
- Muted text: #909090
- Dividers: #D3D3D3
- Strong border: #C8C8C8
- Hover fill: rgba(0,0,0,0.035)
- Selected fill: rgba(255,255,255,0.72)
- Pressed fill: rgba(0,0,0,0.065)
- Outer backdrop: dark neutral gray when applicable
- No bright blue default WinUI styling
- Do not use gradients except extremely subtle material lighting
- Do not use neon colors
- Do not retain the existing blue send button
- Do not use oversized cards or heavy drop shadows

Use:
- Segoe UI Variable
- Clean thin borders
- Low-contrast shadows
- Subtle transparency
- Soft acrylic/mica only where it improves the exact visual match
- Consistent 1 px divider lines
- Compact desktop spacing
- Crisp vector icons

Avoid:
- Generic Fluent demo styling
- Rounded rectangles everywhere
- Excessive blur
- High-saturation accent colors
- Thick outlines
- Large empty card containers
- Mobile-style layout
- Inconsistent radius values

GLOBAL GEOMETRY

Use a three-column Grid.

Suggested initial proportions:
- Left panel: approximately 20–21%
- Center workspace: approximately 52–54%
- Right tool panel: approximately 25–27%

At the default window width:
- Left sidebar: approximately 235–255 px
- Right panel: approximately 300–340 px
- Center panel occupies the remaining width

Behavior:
- Left and right panels use fixed constrained widths at normal desktop sizes.
- Center panel expands.
- Use 1 px vertical dividers.
- Do not allow columns to overlap.
- Do not allow content to become horizontally clipped.
- At smaller supported sizes, collapse the right panel before damaging the center workspace.
- Provide an explicit reopen button when the tool panel is closed.
- Preserve layout state between sessions.

CORNER RADII
Use a limited radius system:
- Outer application/window content: 16–18 px when windowed
- Large input capsule: 22–25 px
- Segmented controls: 18–20 px
- Search field: 16–18 px
- List selection: 8–10 px
- Small icon buttons: circular or 8 px
- Do not randomly mix radius values

TOP TITLE BAR

Height:
- Approximately 42–46 px

Left:
- Minimal ZX0ai brand mark
- “ZX0ai” text
- Compact spacing
- Match the reference logo position and weight

Right:
- Notification icon
- Settings icon
- Compact profile control
- Avatar
- User name
- Small dropdown chevron
- Native window controls at the far right if shown in the custom chrome

Requirements:
- Keep the title bar visually quiet
- Use monochrome icons
- No oversized logo
- No bright active underline
- All title-bar buttons require hover, pressed, focus, and tooltip states

LEFT HISTORY SIDEBAR

Header area:
- “History” label
- Compact collapse/sidebar icon aligned to the right

Mode switch:
- Two-segment pill control
- “Timeline”
- “Project”
- Icon and label in each segment
- Selected segment uses a soft white elevated surface
- Unselected segment remains transparent
- Preserve the current active mode

Search field:
- Compact pill-shaped search box
- Search icon on the left
- Placeholder: “Search”
- Keyboard shortcut hint on the right, such as Ctrl+K
- Correct focus ring
- Search conversations immediately with debouncing
- Escape clears or exits search correctly

Chat list:
- Section label such as “Your Chats”
- Compact rows
- Chat title on the left
- Relative time on the right
- Selected chat uses a subtle rounded white surface
- Hover state must be visible but restrained
- Text must trim with ellipsis
- Provide context menu:
  - Rename
  - Duplicate where supported
  - Pin
  - Delete
- Deletion must require confirmation or offer undo

Project mode:
- Display real projects
- Project folders must be functional
- Do not retain “No projects yet” when projects already exist
- Add project action must work
- Preserve chat-to-project association

Bottom account section:
- Compact profile row
- Avatar
- User name
- Subscription or account status
- Expand/collapse chevron
- Remove “Upgrade to Pro” unless it is connected to a real billing flow
- If billing exists, restyle the action to match the reference
- Keep this area anchored to the bottom

CENTER CHAT WORKSPACE

Empty state:
- Large centered multi-line prompt:
  “What’s on
   Your mind
   today?”
- Light embossed or low-contrast text treatment
- Text may sit behind the composer visually as in the reference
- Do not reduce readability excessively
- Keep it centered in the usable workspace

Chat state:
- Maintain the same central composition after messages exist
- User messages appear as compact right-aligned rounded bubbles
- AI responses are rendered directly on the canvas without a large enclosing card
- Maintain comfortable reading width
- Support:
  - Markdown
  - Headings
  - Lists
  - Tables
  - Inline code
  - Code blocks
  - Copy buttons
  - Links
  - Arabic
  - English
  - Mixed RTL and LTR content
- Do not display raw Markdown syntax when rendered mode is active
- Avoid the broken pipe-table appearance visible in the old screenshots

CHAT HEADER

When a conversation is active:
- Small conversation icon
- Conversation title
- Delete action
- Optional split/details action
- Match the restrained geometry of the target
- Do not consume excessive vertical space
- Add tooltips and accessible names

COMPOSER

The composer is a central element and must closely match the reference.

Empty-state placement:
- Horizontally centered
- Positioned across the central prompt text
- Approximately 65–72% of center-column width
- Single rounded capsule

Active-chat placement:
- Anchored near the bottom of the central workspace
- Maintain consistent side margins
- Expand vertically for multiple lines
- Maximum height before internal scrolling

Inside the composer:
- Circular plus button on the left
- Text input
- Placeholder: “Ask AI anything…” or the configured product wording
- Microphone icon
- Circular voice/audio action on the right
- Send action appears contextually when text exists
- Do not use the current bright blue circular send button
- Use a monochrome dark or glass-styled action matching the reference

Interaction:
- Enter sends
- Shift+Enter inserts a line break
- Escape cancels an active generation when appropriate
- Disable duplicate submissions
- Show generation state
- Support cancellation tokens
- Preserve unsent text when switching panels where reasonable
- Correct IME behavior
- Correct Arabic caret and selection behavior
- Correct keyboard navigation

Context controls currently shown under the chat:
- Workspace/project selector
- Local/remote context
- Approval mode
- Model selector
- Reasoning/quality selector

Do not scatter these controls across the bottom.

Move them into a compact context strip integrated with the composer or a subtle row immediately above it.

The strip must:
- Use small monochrome icons
- Avoid bright blue text
- Use consistent pills
- Collapse intelligently when space is limited
- Open real menus
- Display the active model and execution context accurately

RIGHT TOOL EXECUTION PANEL

Header:
- “Tool Execution”
- Close button aligned right

Segmented tabs:
- “Summary”
- “Details”

When tools such as Run, Terminal, or Files are available, integrate them into a coherent second-level mode selector without visually conflicting with Summary/Details.

The panel must support:

SUMMARY
- Current operation
- Status
- Model
- Token usage
- Rate
- Elapsed time
- Tool count
- Current phase
- Friendly error summary

DETAILS
- Full execution timeline
- Tool calls
- Arguments with secrets redacted
- Output
- Timing
- Errors
- Retry information
- Provider response metadata where safe

FILES
- Real project file tree
- Expand/collapse folders
- File icons
- Current-file highlight
- Refresh
- Filter
- Open file
- Reveal relevant path where supported
- Correct path normalization
- Lazy-load large trees
- Do not freeze the UI

TERMINAL
- Real terminal output
- Monospace font
- ANSI handling or sanitization
- Copy
- Clear
- Auto-scroll toggle
- Cancel running command where supported
- No fake terminal text

RUN
- Animated but restrained running indicator
- Current status
- Model
- Tokens
- Rate
- Elapsed time
- Final success or failure state
- No random colorful blurred blob
- Replace the old blob with a minimal monochrome progress visualization matching the target

PANEL BEHAVIOR
- Close button works
- Reopen action works
- Active tab persists during the session
- Panel content must not visually jump during status updates
- Use virtualization for long logs
- Use cancellation and background tasks correctly
- UI updates must run safely on the DispatcherQueue

RTL AND ARABIC FIXES

The old screenshots show Arabic and English mixed incorrectly.

Implement robust bidirectional content support:
- Detect message direction per message or block
- Arabic user messages align right
- English content aligns left
- Code always remains LTR
- File paths remain LTR
- Markdown tables preserve logical alignment
- Do not globally flip the entire application when a message is Arabic
- Navigation chrome can remain LTR while Arabic message content uses RTL
- Ensure punctuation, backticks, filenames, and C# identifiers display correctly
- Use FlowDirection at the smallest appropriate container level

AI PROVIDER AND EXECUTION ERROR REPAIR

The existing application exposes raw provider failures such as:
- Insufficient credits
- max_tokens affordability errors
- NVIDIA ResourceExhausted
- Worker request-limit failures
- 429 rate limits
- Provider quota exhaustion
- Upstream timeout
- Unsupported model
- Context-window overflow

Do not hide these problems and do not pretend they can be bypassed.

Implement a proper provider error architecture.

Create or improve:
- ProviderError
- ProviderErrorCategory
- ProviderExceptionMapper
- RetryPolicy
- TokenBudgetService
- ModelCapabilityRegistry
- ProviderHealthTracker
- RequestQueue
- ExecutionCancellationService
- UserFacingErrorFormatter

Map provider failures into categories:
- Authentication
- InsufficientCredits
- RateLimited
- ProviderCapacity
- ContextLengthExceeded
- InvalidRequest
- ModelUnavailable
- NetworkFailure
- Timeout
- Cancelled
- Unknown

TOKEN BUDGETING
Before sending a request:
- Estimate input tokens
- Read the selected model context limit
- Read configured output-token maximum
- Reduce max output tokens when the requested amount is unaffordable or unnecessary
- Reserve output capacity
- Warn before truncating important context
- Never repeatedly submit an 8192-token output request when only approximately 1327 tokens are affordable
- Prevent invalid requests locally where possible

RATE-LIMIT HANDLING
- Respect Retry-After headers
- Use bounded exponential backoff with jitter
- Never create an uncontrolled retry loop
- Do not retry non-retryable credit or authentication failures
- Queue requests where appropriate
- Disable repeated send actions while the same request is running
- Display next retry time
- Let the user manually retry after failure
- Record attempt count in Details

PROVIDER FALLBACK
Implement fallback only when configured and permitted:
- Try another configured model/provider for retryable capacity failures
- Never silently switch to a paid model
- Never silently switch quality tier
- Clearly display the actual model used
- Preserve privacy and local-execution requirements
- Do not fallback after authentication or billing errors unless the user explicitly configured another valid provider

ERROR UI
Replace raw error strings inside the chat with a compact, polished message containing:
- Clear title
- Plain-language explanation
- Provider/model involved
- Suggested action
- Retry button when valid
- Open Settings button when credentials or credits are required
- Copy technical details
- Expandable diagnostics

Examples:
- “This model does not have enough available credit for the requested output length.”
- “The provider is temporarily at capacity. Retry after the displayed delay or select another configured model.”
- “The request exceeded the provider’s worker limit and was not charged, when verifiable.”

Never expose:
- API keys
- Authorization headers
- Full sensitive request payloads
- Local secrets
- Personal file content in logs without explicit need

SETTINGS

Build or repair a real settings experience for:
- AI providers
- API keys using secure storage
- Default model
- Fallback policy
- Maximum output tokens
- Request timeout
- Appearance
- Language
- Data storage
- Diagnostics
- Privacy
- Terminal permissions
- Approval mode

Secrets:
- Never store API keys in plain-text JSON
- Use Windows Credential Locker, PasswordVault, or an appropriate encrypted Windows storage mechanism
- Redact secrets in logs and UI
- Migrate insecure legacy storage safely when detected

ARCHITECTURE

Use a maintainable structure similar to:

/App
/Assets
/Controls
/Converters
/Models
/Services
/Services/AI
/Services/Execution
/Services/Storage
/Services/Navigation
/Services/Security
/ViewModels
/Views
/Theme
/Utilities

Recommended components:
- MainWindow
- AppShell
- HistorySidebar
- ChatWorkspace
- ChatMessageView
- ChatComposer
- ContextStrip
- ToolExecutionPanel
- ExecutionSummaryView
- ExecutionDetailsView
- FilesView
- TerminalView
- RunView
- SettingsView
- ProfileFlyout
- ConfirmationDialog
- UserFacingErrorView

Recommended ViewModels:
- ShellViewModel
- HistoryViewModel
- ChatViewModel
- ComposerViewModel
- ToolExecutionViewModel
- FilesViewModel
- TerminalViewModel
- SettingsViewModel

Do not place all behavior in MainWindow.xaml.cs.

Use:
- Observable properties
- ICommand/IAsyncRelayCommand or an equivalent existing MVVM implementation
- Dependency injection
- Interfaces for services
- CancellationToken
- IAsyncDisposable where required
- Structured logging
- UI-safe state transitions

If CommunityToolkit.Mvvm is already installed and correctly used, preserve it.

Do not introduce unnecessary frameworks merely to rewrite working architecture.

PERFORMANCE

The application must:
- Start quickly
- Remain responsive during AI streaming
- Stream tokens without rebuilding the entire message tree
- Virtualize long conversation lists
- Virtualize long execution logs
- Load file trees asynchronously
- Avoid blocking .Result or .Wait()
- Avoid memory leaks from events
- Unsubscribe or use weak event patterns where appropriate
- Dispose streams, HTTP responses, and cancellation sources
- Reuse HttpClient correctly
- Avoid excessive DispatcherQueue calls
- Debounce search and resize operations
- Preserve smooth resizing

ACCESSIBILITY

Add:
- AutomationProperties.Name
- Tooltips
- Keyboard focus order
- Visible keyboard focus
- Proper contrast
- Screen-reader labels
- Keyboard-accessible segmented controls
- Minimum practical target sizes
- High-DPI support
- 100%, 125%, 150%, and 200% scaling verification

THEME RESOURCES

Centralize all reusable design values.

Create or repair:
- Color resources
- Brushes
- Typography styles
- Spacing constants
- Corner radii
- Button styles
- Icon-button styles
- Segmented-control styles
- Search-box styles
- Chat bubble styles
- Composer styles
- Divider styles
- Panel styles
- List item states
- Error states

Do not hardcode slightly different values repeatedly across XAML files.

Use theme resources and reusable controls.

ANIMATIONS

Use only subtle animations:
- 120–180 ms hover/fade transitions
- Smooth panel opening/closing
- Soft selection movement
- Small loading indicator
- Message appearance
- No bouncing
- No large zoom
- No flashy gradients
- Respect Windows reduced-motion preferences

FUNCTIONAL REQUIREMENTS

The completed application must support:
- Create chat
- Select chat
- Rename chat
- Delete chat
- Search history
- Project mode
- Open project
- Switch project/workspace
- Send message
- Stream assistant response
- Stop generation
- Retry response
- Select model
- Display actual model used
- Approval mode
- Files browsing
- Terminal output
- Run status
- Tool execution summary
- Tool execution details
- Copy response
- Copy code
- Open settings
- Open profile menu
- Collapse history sidebar
- Close/reopen tool panel
- Window resize
- Session restoration
- Error recovery

Do not break existing features while redesigning them.

BUILD AND REPAIR PROCESS

Perform the work in this order:

PHASE 1 — AUDIT
- Inspect the full repository.
- Build the current solution.
- Record existing errors and warnings.
- Identify the actual startup project.
- Identify duplicated or obsolete UI.
- Identify current provider integrations.
- Identify any WPF or WinForms remnants that conflict with WinUI 3.

PHASE 2 — FOUNDATION
- Repair project references and packages.
- Repair the application startup path.
- Establish design tokens.
- Establish custom title bar.
- Establish shell grid.
- Confirm resizing and DPI behavior.

PHASE 3 — VISUAL REBUILD
- Rebuild the sidebar.
- Rebuild the central workspace.
- Rebuild the composer.
- Rebuild the tool execution panel.
- Match the reference image at the target window size.

PHASE 4 — FUNCTIONAL RECONNECTION
- Reconnect navigation.
- Reconnect chat state.
- Reconnect model selection.
- Reconnect project context.
- Reconnect file tree.
- Reconnect terminal.
- Reconnect run execution.
- Reconnect settings.

PHASE 5 — ERROR SYSTEM
- Add token budgeting.
- Add provider error mapping.
- Add safe retry behavior.
- Add capacity and credit handling.
- Add user-facing diagnostics.
- Remove raw upstream messages from the normal chat surface.

PHASE 6 — QUALITY
- Fix RTL and Markdown rendering.
- Fix async and cancellation issues.
- Fix memory leaks.
- Fix resizing.
- Fix keyboard interaction.
- Fix accessibility.
- Remove dead code.

PHASE 7 — VALIDATION
Run:
- dotnet restore
- dotnet build
- dotnet test, when tests exist
- launch the actual application
- verify major workflows manually

Treat warnings seriously.
Do not suppress warnings simply to obtain a clean build.

VISUAL VALIDATION

At minimum, compare the implemented app against Image 1 at:
- Default window size
- 125% display scaling
- Narrow supported window
- Maximized window

Check:
- Title-bar height
- Column widths
- Divider positions
- Sidebar row height
- Composer size and placement
- Prompt text position
- Right-panel padding
- Tab geometry
- Border opacity
- Corner radius
- Icon alignment
- Baseline alignment
- Empty-state balance

Use screenshot comparison where practical.

The final result must not resemble the current old design with only changed colors.

It must visibly and structurally match the supplied reference.

DEFINITION OF DONE

The task is complete only when:
- The program is a native WinUI 3 application.
- The old visual system has been replaced.
- The result closely matches Image 1.
- All primary controls work.
- Arabic and English render correctly.
- Provider errors are handled cleanly.
- Token-limit failures are prevented where possible.
- Rate-limit and capacity errors have safe behavior.
- No secrets are exposed.
- No placeholder interface remains.
- No duplicate shell remains.
- The project builds with zero errors.
- Tests pass where available.
- The application launches successfully.
- Resizing does not break the layout.
- The final UI is polished enough for production use.

FINAL RESPONSE FORMAT

After completing the implementation, report:
1. Repository files inspected
2. Files added
3. Files modified
4. Files removed
5. UI architecture implemented
6. Functional systems preserved or repaired
7. Provider and token errors repaired
8. Build result
9. Test result
10. Remaining limitations, only if genuinely unresolved

Do not respond with only a plan.
Do not stop after analysis.
Do not provide pseudo-code instead of implementation.
Do not claim success without building and launching the real application.
Begin by inspecting the repository and continue through full implementation.