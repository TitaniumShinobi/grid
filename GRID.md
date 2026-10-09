// 09/30/2026 Canonical Library

**YOU ARE HERE**
GAME REGISTRATION
██████████  implemented

CANONICAL KNOWLEDGE
██████████  implemented

GTA KNOWLEDGE ADAPTERS
██████████  substantial implementation

DIF SELECTOR ENGINE
██████████  implemented
       │
       └── presentation/materialization still unfinished in actual product

INVESTIGATION TICKET CONTRACT
██████████  implemented
░░░░░░░░░░  NOT composed

POWERHELL INVESTIGATION PIPELINE
██████████  implemented separately

GTA EVIDENCE COLLECTION
████████░░  meaningful

GTA EVIDENCE ANALYSIS
██░░░░░░░░  minimal

CAUSE DIAGNOSIS
░░░░░░░░░░  not implemented

REPAIR
░░░░░░░░░░  not implemented for this scenario

VERIFY
░░░░░░░░░░  not implemented for this scenario

•••

# Core

The **Gaming, Repairs, Investigations & Development** Game Manager is free software licensed under the GNU General Public License, version 3 only (GPL-3.0-only).

Homepage URL: https://grid.thewreck.org
Application description: Gaming and modding workbench by LIFE Technology.
Authorization callback URL/URI: https://grid.thewreck.org/api/auth/github/callback

GRID target audience: streamers, gamers, modders, developers

---

# CSS Rubric

> *VS Code shell, MO2 workstation, Vortex catalog & modular dashboard, modern authentication, LIFE Technology-congruent components (authentication, chat symmetry, explorer, icons, scoreboard, startup, taskboard).*
> ```
> ┌──────────────────────────────────────────────────────────────────┐
> │ 30 DIP HEADER                                                    │
> ├──────┬──────────────┬──────────────────────────┬─────────────────┤
> │      │              │                          │                 │
> │ 60   │ LEFT PANEL   │      MAIN PANEL          │ RIGHT PANEL     │
> │ DIP  │ adjustable   │                          │ Chat / etc.     │
> │ RAIL │ bounded      │                          │ adjustable      │
> │      │              │                          │ + maximizable   │
> │      │              │                          │                 │
> │      │              ├──────────────────────────┤                 │
> │      │              │ BOTTOM CONSOLE           │                 │
> │      │              │ adjustable/maximizable   │                 │
> ├──────┴──────────────┴──────────────────────────┴─────────────────┤
> │ 30 DIP FOOTER                                                    │
> └──────────────────────────────────────────────────────────────────┘
> ```

Desktop GRID: faint grid behind the workbench/auth surfaces.
grid.thewreck.org: identical spacing, weight, and opacity.
Lines stay fixed and perfectly regular—no perspective or decorative distortion.
Content/panels sit on top of the grid rather than getting their own competing patterns.
Dark theme: grid only a few percentage points lighter than the background.
Eventually light theme gets the inverse treatment.
```
GRID_BACKGROUND
GRID_LINE
GRID_CELL_SIZE
GRID_LINE_WIDTH
GRID_LINE_OPACITY
```

**Measurements**
Line = 1px
Cell = 10px x 10px
Row = 30px
Column = 30px
Gutter = panels [5px], tabs [3px]

The entire shell sits on a uniform 10-DIP visual grid. No heavier every-third-line pattern. Hard lines exist only at actual structural divisions: rail, panels, console, header and footer.

The header and footer are 30 DIP. The permanent left rail is 60 DIP, with its contents centered.

**Adjustable Panels**
Universal Header:
3 px from the panel's interior top/left/right.
- Controls and title are vertically centered within the same header row.
- A tab belongs inside the surface it controls. It starts near that surface's top-left corner with a tight inset. It does not sit on top of/outside the surface border.
LEFT -- user game snapshot, search collections, file explorer, activity, control
MAIN -- home, catalog, workstation, settings, preview
RIGHT -- chat, data intake forms
CONSOLE -- problems, output, terminal

---

# GRID Startup

GRID startup must be fast, measurable, and truthful.

**Registration performs expensive discovery and resolution. Startup restores prepared state.**

Ordinary startup must not unnecessarily:

- re-register games or mods;
- rebuild canonical knowledge;
- rescan the machine;
- reconstruct persisted DIF knowledge.

`start/` owns startup measurement, readiness, timing, receipts, and regression detection through its reusable wizard. GRID owns product state and emits truthful startup-stage events.

Sequence:

**Observe → Measure → Optimize → Baseline**

Before optimization, measure one real Windows launch and identify per-stage cost.

## Loading Bar

During startup, show a truthful progress bar:

- 22px high;
- centered X/Y in the shell;
- 100px left/right margins;
- LCD-counter visual language;
- GRID accent-color fill;
- left-to-right thermometer progression.

Progress advances only from verified startup work—not timers or simulated percentages.

Shell readiness must not wait for optional background work.

**Target:** Vortex-class responsiveness for a comparable workload, with future regressions detected against measured GRID baselines.

---

# GRID-VVAULT Handshake

**`auth/` owns canonical LIFE Technology authentication lifecycle:**
- enrollment/signup
- login
- authorization identity
- policy reverification
- app-to-app connectivity

GRID consumes the authentication contract from Auth:
```
Authenticated Identity
- AccountID
- DisplayName
- Email
- Avatar
- Session
```
```
GridAccountData
└── GridInstance
    ├── games
    ├── profiles
    ├── options
    ├── shell state
    ├── workspace state
    └── dashboard state
```

VVAULT should be the durable transcript boundary now.
```
`vvault/instances/`:
shard_0000/
- {grid-001}/
-- assets/
--- ...ticket attachments...
-- config/
--- actions/
---- {actionName}.json
--- glyph.png
--- metadata.json (updated w/settings)
-- data/
--- ...GRID-owned durable structured data...
-- dif/
--- dif-tasks.md (canonical singleton thread separated by tasks -- should reflect Chatty transcript behavior)
-- logs/ (maybe?)
--- ~capsule.log~
--- chat.log
--- ~cns.log or "brain.log" or "reasoner.log"~
--- ~identity_guard.log~
--- ~independence.log~
--- ~ltm.log(?)~
--- ~self_improvement_agent.log~
--- server.log
--- ~stm.log(?)~
--- watchdog.log
```

Task-Sorted Append-Only Caonincal Singleton Thread:
```
# GRID

## Task grid-task-000001
### Casino Mission Limo Crash

[Ticket]
...

[GRID]
Evidence required...

[User]
Approved.

[GRID]
...

### Receipt
Edited 0 files
...

## Task grid-task-000002
### Skyrim Startup Crash
...
```

Refer to `chatty-cli/`, `code/` and `vvault/` documentation for transcript persistence.

Identity Chain:
```
Windows GRID
     ↓
AUTH session restoration
     ↓
authenticated StableAccountId
     ↓
VVAULT identity/account lookup
     ↓
GRID account/product state
     ↓
account+device local state
     ↓
UI
```

---

# Signin & Signup

Microsoft: #1F83D3, white Microsoft mark + white text.
GitHub: #252A31, white GitHub mark + white text.
Google: black, multicolor Google G + white text.
No Apple button.
Then email.
Then a normal-size Sign up hyperlink underneath.
```
                 Sign in to Grid

        [ Microsoft  Continue with Microsoft ]
        [ GitHub     Continue with GitHub    ]
        [ Google G   Continue with Google    ]

                    ─────

        [ Email                              ]
        [              Sign in               ]

              New here? Sign up
```
```
                         Sign up for Grid

             [ Microsoft  Continue with Microsoft ]
             [ GitHub     Continue with GitHub    ]
             [ Google G   Continue with Google    ]

                              or

             Name
             [ Your name                         ]

             Email
             [ you@example.com                   ]

             [ ] By continuing, I confirm I understand and agree to
                 the GRID Terms of Service and the GRID Privacy Notice.
                 If I am in the EEA or UK, I have read and agree to the
                 European Electronic Communications Code Disclosure.

                         [ Create account ]

                    Already have an account? Sign in
```

During sign-in/sign-up, the main workbench still exists structurally, but AUTH owns what the unauthenticated user is permitted to interact with.

For signup specifically:
```
SIGNED OUT

Activity rail / sidebar
→ locked
→ no games
→ no Explorer
→ no account data
→ no product panels

Main panel
┌─────────────────────────────────────────────┐
│ Home | GRID Terms of Service ×              │ ← usable
├─────────────────────────────────────────────┤
│                                             │
│           legal document viewer             │
│                                             │
└─────────────────────────────────────────────┘
```

The important detail is Home doesn't disappear. The login/signup surface remains the Home tab and keeps its position. Clicking a legal hyperlink opens another main-panel tab beside it.

ConsentRequired is a native application state:
```
SIGN UP
existing checkbox accepted
→ GitHub
→ AUTH
→ loopback
→ SignedIn

SIGN IN
→ GitHub
→ AUTH
→ account enrolled?
   YES → loopback → SignedIn
   NO  → loopback → ConsentRequired / Signup completion
                    inside GRID
```

Signed-in State:
```
Returning user
Sign in → authenticated → Home

New user
Sign up → authenticated/enrolled
        → Welcome to GRID
        → first-run/onboarding flow
        → discovery/setup
        → Home
```

Unowned ≠ current user. Never infer ownership from machine presence or sign-in order.
```
Machine A + Account Devon
→ Welcome completed
→ local installations/connections established
→ returning login → Home

Machine B + Account Devon
→ first GRID use on this machine
→ Welcome
→ discover/setup this machine
→ Home
```

Empty State Rule:
```
new account
    ↓
0 connected games
    ↓
GRID performs/requests actual discovery
    ↓
discovered candidates ≠ connected games
    ↓
user reviews/connects installation
    ↓
only then does it appear under Games
```

---

# Top Row 

**Left-to-Right (Dropdowns)**
1. File
	Manage Profiles...
	Connect game...
	Install mod...
	Open in manager...
	Exit
2. Edit
	Undo
	Redo
	Cut
	Copy
	Paste
	Find
	Replace
	Find in Files
	Replace in Files
3. View
	Command Palette...
	Open View...
	Appearance
	- Full Screen
	- Zen Mode
	- Centered Layout
	Editor Layout
	- Split Up, Split Down, Split Left, Split Right
	Explorer
	Search
	Source Control
	Run
	Extensions
	Chat
	Problems
	Output
	Debug Console
	Terminal
4. Go
	Back
	Forward
	Last Edit Location
	Go to File...
	Go to Symbol in Workspace...
	Go to Definition
	Go to Declaration
	Go to Type Definition
	Go to Implementation
	Go to References
	Go to Line/Columm...
	Go to Bracket
	Next Problem
	Previous PRoblem
	Next Change
	Previous Change
5. Run
	Start Debugging
	Run Without Debugging
	Stop Debugging
	Restart Debugging
	Open Configurations
	Add Configuration...
	Step Over
	Step Into
	Step Out
	Continue
	Toggle Breakpoint
	New Breakpoint
	- Conditional Breakpoint...
	- Edit Breakpoint...
	- Inline Breakpoint...
	- Function Breakpoint...
	- Logpoint...
	- Triggered Breakpoint...
	Enable All Breakpoints
	Disable All Breakpoints
	Remove All Breakpoints
	Install Additional Debuggers...
6. Terminal
	New Terminal
	Split Terminal
	New Terminal Window
	Run Task...
	Run Build Task...
	Run Active File
	Run Selected Text
	Show Runing Tasks...
	Restart Running Task...
	Terminate Task...
	Configure Tasks...
	Configure Default Build Task...
7. Tools
	Manage tools
	- Profile organizaion
	Plugins
	Settings...
8. Help
	Welcome
	Show All Commands
	Documentation
	Editor Playground
	Open Walkthrough...
	Show Release Notes
	Get Started with Accessibility Features
	Ask @grid
	Keyboard Shortcuts Reference
	Tips and Tricks
	Search Feature Requests
	Report Issue
	View License
	Privacy Statement
	Toggle Developer Tools
	Open Process Explorer
	Check for Updates...
	About
9. Back/Forward
10. Omni Search (account, files, nexus, wiki)
11. Open in...
12. Customize Layout...
13. L|C|R Panel Toggles
14. Minimize, Fullscreen, Quit

---

# Left Sidebar

**Top-to-Bottom (Shell Navigation)**
1. Home page: widgets, no side panel activation
2. Game snapshots: workstation button, more widgets
3. Add game [+add profile]: catalog & hyperlink, no side panel acivation
4. Search across games & profiles, search actual folder/files
5. File explorer: list multiple directories per profile
6. Activity receipts, activity board
7. GRID | AUTO/Model settings: side panel - tabs, main panel - content
8. User settings (email thumbnail): side panel - tabs, main panel - content

HOME / G
→ route main panel to Home
→ preserve side-panel open/closed state
→ preserve side-panel width
→ preserve whatever contextual content is already there
→ NEVER switch side-panel content to Home

HOME SIDE-PANEL PAGE
→ remove completely

GAME RAIL ITEM
→ selects game context
→ game snapshot may populate side panel
→ routes main panel to that game's workstation
→ uses the game's icon

TABS
→ main-panel navigation
→ do not independently redefine rail/side-panel context

GRID treats each discovered game/version as an independent game identity.
- **Add Game** registers only the game explicitly selected by the user.
- Sidebar entries represent registered games only.
- Games are sorted alphabetically by GRID display name.
- Favorites pin to top, alphabetically.
- Profiles remain inside their respective game workspaces.
- Enhanced/Legacy editions remain separate.
- MO2/Vortex/Grid origins never affect navigation.

**One game, one identity, one sidebar entry.**
Registered games are alphabetical. Profiles do not become rail entries; they're inside their game's context/workstation accessed through the snapshot.

---

# Left Panel

**Universal Header**
3 px from the panel's interior top/left/right.
- Controls and title are vertically centered within the same header row.

**Game Snapshots** ← PAGE
GRID sources each game’s snapshot artwork directly from Steam and persists it locally for immediate display without requiring Steam to be running.

Options:
- Add to favorites
- Manage
-- Add desktop shortcut
-- Browse local files (opens explorer)
-- Back up game files
-- Uninstall

Banner [Steam background](opens Steam game link)

Title [game logo]

Profiles:
- `Open Profile`

Game health [Security at a glance](CleanHouse)

Gameplay capture (Steam)

Activity stream (Steam):
- Achievements
- Dev updates
- Announcements
- Featured mods (GTA5 Mods, Mod DB, Nexus, Patreon, Schaken, )

Community Content

**Game Collection Search** ← PAGE
Search PC files across all games at any time.
Results window (compact explorer)

**File Explorer** ← PAGE
*Shared with `code/` IDE explorer.*
- VS Code nested row system. Display multiple directories at once.
```
`{gameRoot}/`
`{connectingInstance}/`
`My Games/`
`steamapps/`
```

**Activity** ← PAGE
LCD Scoreboard Counter Box:
- 5px gutters
- 1 row (22px), analog, full panel width, spaced, four individual counts of each phase
- Shared CSS with mod/plugin counter boxes
- Capcity: 2 digits/quarter

Target appearance:
`[   00   |   00   |   00   |   00   ]`

Recent Activity (sccorecards):
- 5px gutters (side rail | window | card)
- Four windows: Ledger, Ready, In Progress, Queue
- Shared symmetry with activityboard panels
- Controls: per window based on activityboard panels

Activityboard Toggle fixed to bottom right:
- Right-chevron (>)
- 3 px from the panel's interior bottom/right.

Receipts:
- [Re]Installed Mods receipt
- Merges Created/Edited/Deleted
- Game setup receipt
- Profile setup receipt

**Control** ← HIDDEN PAGE
*Shared with `cleanhouse/` ctrl panel.*
Copilot Insights

---

# Activity Board (Ledger & Lifecycle)

> *Shared with `code/` IDE taskboard and `cleanhouse/` mediation board.*

**Queue**
Header:
- 3px gutter (top, left, right)
- Congruent single individual counter box

Scorecards:
- 5px gutter
- Three-row (~90px) item list (auto-named titles & preview)
```
Title (standard font)
Verbatim text of submitted ticket request. (small font; 3 rows of text)
```
- Controls: preserve context, continue or delete drafts

Composer:
- Shared CSS and features with right panel chat composer
- One-row message bar, fully rounded sides
-- Left-chevron (<) taskboard toggle
-- "+" Attach | Blank circle send
```
 _________
(_hello___)
<		+ ●
```

**In Progress**
Header:
- 3px gutter (top, left, right)
- Congruent single individual counter box

Scorecards:
- 5px gutter
- Four-row (~120px) item list (same titles, sitrep & receipt [actions])
```
Canonical Title (standard font)
Current progress state of ticket. May prompt user action such as approval requests and agent questions. (small font; 3 rows of text)
(0) actions
```
- Controls: preserve context, restore interruptions, stop, modify and delete tasks

**Ready**
Header:
- 3px gutter (top, left, right)
- Congruent single individual counter box

Scorecards:
- 5px gutter
- Four-row (~120px) item list (same titles, conclusion & receipt [actions & changes])
```
Canonical Title (standard font)
Ticket outcome. May prompt user action such as human testing or further diagnosis/repair. (small font; 3 rows of text)
(0) actions | (0) files changed
```
- Controls: cta/next steps

**Ledger**
Header:
- 3px gutter (top, left, right)
- Congruent single individual counter box

Scorecards:
- 5px gutter
- Four-row (~120px) item list (same titles, conclusion & receipt [actions & changes])
```
Canonical Title (standard font)
Ticket outcome. Includes user taken action such as human testing or further diagnosis/repair. (small font; 3 rows of text)
(0) actions | (0) files changed
```
- Controls: restore previous context, reopen tickets

---

# Main Panel

**Tabs**
3 px from the panel's interior top/left/right.
- Controls and tabs are vertically centered within the same header row.

**Home** ← PAGE / TAB
> *Customizable, modular combination of notifications, activity & all snapshots*

> *Snapshot of it all*

Same as Vortex
- “Customize Dashboard”
- Drag components around
- Add/delete components

Components
- Featured Catalog [480x240]
```
┌─────────────────────────────────────┐
│            Featured Games           │
│  Game   Game   Game   Game   Game   │
│   --     --     --     --     --    │
│   --     --     --     --     --    │
└─────────────────────────────────────┘
```
- Contiue Playing [120x60]
```
┌──────────────────┐
│    Game Logo     │
│ Continue Playing │
└──────────────────┘
```
- Information
-- GRID version updates [240x120]
-- Gaming news [240x120]
-- Modding news [240x120]
-- Combined news & updates [240x480]
- Social
-- Live streaming [240x120]
-- Gameplay capture [240x120]
- Utility
-- Clock: Analog [120x120], Digital-S [60x30], Digital-M [120x60], Digital-L [240x120], Digital-XL [480x240]
-- Game health [240x120]
-- Tools per profile [120x30]
```
┌──────────────┬───┐
│ ●   Orange   │ › │
└──────────────┴───┘
```

Centered logo (`grid.svg`), bottom-aligned, snapped to 10px cell grid.
- Must fit 16x4 (160px x 40px) cell placement

**Workstation** ← PAGE / TAB
Tools:
- Manage Submenu (left: window, right: data intake form)
-- Add
--- From file
--- Empty
--- Clone selected
-- Delete
-- Sort
•••
-- Title (placeholder: "New Executable")
-- Binary
-- Start in
-- Arguments
-- Overwrite Steam AppID
-- Create files in mod instead of overwrite* SKSE
-- Force load libraries* | | Configure Libraries
-- Use application's icons for desktop shortcuts
-- Hide in user interface
* = Profile specific

Mod Window:
`Mod Name | Conflicts | Flags | Priority | Version | Category`

Plugin Window:
- Fixed tabs: 3 px from the window's interior top/left/right.
- Plugins
-- `Name (.esl, .esm, .esp, .yft, etc)| Flags | Priority | Mod Index`
- Archive (bundles of game assets)
- Data
-- `Name | Mod | Type | Size | Date modified`
- Save
-- `Name | File (.ess)`
- Downloads
-- `Name (.7z, .rar, .zip) | Status | Size | Filetime`

LCD Mod/Plugin Counter Box:
- Mod LCD: 59×22
- Plugin LCD: 49×22
- Mod capacity: 5 digits
- Plugin capacity: 4 digits
- Left-aligned footer placement
- Both counters should use the same visual digit language:
-- same digit height;
-- same vertical centering;
-- same per-digit spacing;
-- same interior edge clearance;
-- different box widths/capacities only.

Search Mods:
- Previewd list narrows per letter typed
- Separators independent from search
-- Stay ~in control (first seen; mods still nested)~ as list narrows
-- Open/closed regardless of search

Search Plugins: 
- Changes per tab [Plugins, Archives, Data, Saves, Downloads]
- Previewd list narrows per letter typed

Landscape (adjustable left-to-right):
```
GTA V Legacy
Profile: [ My GTA Setup ▼ ]

1234567890123456789012345678901212345678901234567890123456789012
┌──────────────────────────────┐   ┌─────────────────────┐┌────┐
│ MODS                         │ ↻ │ Executables         ││  ▶ │
│                              │   └─────────────────────┘└────┘
│ ☑ ScriptHookV                │┌──────────────────────────────┐
│ ☑ Menyoo                     ││ PLUGINS                      │
│ ☑ Forever Together           ││ ☑ example.asi                │
│ ☐ Vehicle Pack               ││ ☑ example2.asi               │
│                              ││                              │
│ Priority / conflicts / etc.  ││ Load/order/state/etc.        │
└──────────────────────────────┘└──────────────────────────────┘
[ 01393 ]    [Search mods......][ 1274 ]  [Search plugins......]
```

Portrait (adjustable up-and-down):
```
						REFRESH	[Tools...............]	RUN
MOD WINDOW PANE
[ 01393 ]	-ADJUSTS WINDOWS-	[Search mods................] ← GRAB & DRAG ROW
PLUGIN WINDOW PANE
[ 1274 ]						[Search plugins.............]
```

Neither first nor last digit may touch or clip against the box interior.

Refresh Workstation: manually refreshes the profile
```
Refresh
→ preserve GameId / ProfileId / installation
→ re-observe current game/profile/mod/tool state
→ refresh registered knowledge with the current registration engine
→ rebuild/migrate canonical knowledge as needed
→ verify the new generation
→ rebuild prepared DIF/navigation state
→ atomically publish it
→ refresh the Workstation UI
→ write a receipt
```

Refresh is incremental when possible, rebuilds when required.

**Catalog** ← PAGE / TAB
GRID sources each registered game’s catalog artwork directly from Steam and persists it locally so Steam does not need to be running.

Header:
- "Games" title
- "Add profile" hyperlink
- Sort toggle (Most popular, A-Z, Z-A, Most recent)
- Display toggle (thumbnail, list)
- Catalog searchbar

Full-view List (no window):
- Portrait game preview cards (50/page)
-- Options
-- Actions
-- Title (bottom-left aligned)

```
GTA V Legacy   ≠   GTA V Enhanced
Skyrim         ≠   Skyrim Special Edition
Skyrim SE      ≠   Skyrim VR
```

Add Game from Home should simply navigate/open the Game Catalog page. It should not open a modal containing the catalog. Welcome → Browse Game Catalog should navigate to that same Game Catalog page, not launch a special onboarding version of it.
```
Game Catalog page
→ select GTA V Legacy
→ connect/add flow
   → detected installation OR Browse...
   → validate
   → create new profile
```

**Document Viewer**  ← PAGE / TAB
*Shared with `code/` IDE preview panel.*

---

# Loading Bar

GRID will expose real operation state through a 26-DIP, full-width progress bar at the bottom of the Workstation main panel.

Initial supported operations:
- Profile import
- Game registration
- Game launch
- Tool launch
- Mod installation

Every operation follows an explicit lifecycle:

Queued → Preparing → Running → Verifying → Completed / Failed

Progress must reflect actual backend state—never simulated activity. Each stage produces a timestamped activity receipt containing the operation ID, type, target, stage, process information when applicable, and result.

GRID should lock only resources actively involved in an operation, not the entire application.

Rule: visible progress must always correspond to verifiable work.

---

# Right Panel

> *Shared with `code/` IDE ask panel and `chatty-ui/` chat page.*

**topRow**
> *3 px from the panel's interior top/left/right.*
> - Title and controls are vertically centered within the same header row.

pageTitle

Options
- Rename
- Archive
- Bookmark
- Copy
- Move Chat into Main Panel
- Move Chat into New Window
- Keyboard Shortcuts
- Show Chat Debug View (opens left panel)
- Show Agent Debug Logs (opens left panel)
- Show in VVAULT (opens vvault user's data in default browser)
- Chat Settings (opens main settins: chat page)

History (Taskboard Ledger)  ← QUICK MENU | WINDOW

newInvestigation DIF

newChat (right panel home)

Full-size toggle

**Chat Window**
User message bubbles:
- Copy
- Edit

Unconstrained assistant messages:
- Receipt
- Copy

New Chat: Suggestions
- 3 precise prompts tailored to user activity
- Bottom fixed
- 1 row preview → hover: full prompt popup
- Suggestions on new chat only (unaffected by DIF)

History:
- Two-row itemized list (title & stats)
- Restore previous context, reopen tickets, continue drafts

**Transforming Composer**
Scoreboard
Questions
Approval Gate
DIF Tags = [Class icon] Ticket Name
Attachments (later: plugins)
Later: Dictation & Voice Mode

Composer:
- Shared CSS and features with taskboard queue panel chat composer
- One-row message bar, fully rounded sides
-- "+" Attach | Blank circle send
```
   _________
+ (_hello___) ●
```

DIF Composer Preview:
`[class icon]  Ticket Name`

The lifecycle belongs completely in Chat:
```
Ticket Draft
   ↓
Submitted ticket
   ↓
Evidence collection / questions
   ↓
Findings
   ↓
Plan
   ↓
Approve | Modify | Reject
   ↓
Execution
   ↓
Verification
   ↓
Writeout receipt - proof of altered files. Stores in GRIDs activity ledger & VVAULT transcripts.
```

Every one of those becomes an event in one investigation thread. The Composer changes state when GRID needs structured input, authorization, or approval, but the history above it remains the history.

The receipt is not just prose. It should prove what changed:
```
Edited 8 files
Created 2 files
Removed 1 stale artifact
Verification: PASS
```

One Ticket/Request Object:
```
DIF ───────────────┐
                   │
Composer ──────────┼──→ Ticket Draft ─→ GRID (Activity Ledger)
                   │
Suggestions/etc. ──┘
```

Requests go through one channel, the composer. That's where a user should see it preview. That's where a user should control it.

---

# Console Panel

**Top Row**
3 px from the panel's interior top/left/right.
- Controls and tabs are vertically centered within the same header row.

Problems: workspace detection  ← PAGE / TAB
Controls
- Filter
- Collapse All
- View As Table

Output: logs  ← PAGE / TAB
Controls
- Filter
- Clear Output
- Turn Auto Scrolling Off
- Views and More Actions...
-- Open Output in Editor
-- Open Output in New Window
-- Save Output As...
-- Export Logs...
-- Add Compound Log...
-- Import Log...

Terminal: powershell  ← PAGE / TAB
Controls
- Current Terminal
- New Terminal / Launch Profile
- Split Terminal
- Kill Terminal
- Views and More Actions...
-- Scroll to Previous Command
-- Scroll to Next Command
-- Clear Terminal
-- Run Active File
-- Run Selected Text
-- Start Dictation
-- Go to Recent Directory...
-- Run Recent Command...

Universal full screen toggle

---

# Footer

**Account Status**
Side Panel Closed: `•` (green = online, yellow = away, red = offline)
Side Panel Open: `• {status}`
- Small solid circle stays put. Text left-aligned to left side panel guard rail.

**Discrepancies/Concerns**
`x(0)⚠(0)`

**Call-To-Action**
`Peview of most prominent notification.`

**Notifications**
> *Things to know about or act on*

> *Quick Mark As Read*

Opens Submenu.

Types
- New version notes for grid
- Game updates
- Nexus Mod updates
- Crashes ───────┬──→ reporting
- DDOS/Desyncs ──┘

---

# Security HQ

> *Powered by CleanHouse*

> *Accessed through Game Snapshot in Left Panel, and through notifications. ("Security at a glance.")*

Read/Write Tasks
Automation
Scans/Monitoring
Strategy
Mitigation
Prevention
Counter++
Quarantine. Purge. Bless.
Repair
Validation
Report

CleanHouse supplies investigation/integrity/security machinery; the individual game is an adapter.

---

# Data Intake Form (DIF): New Investigation

> *A structured ticket builder whose output lives in the composer.*

> *Accessed through toggle in Right Panel. (New Investigation, Mod Menu Creator)*

> *Alphabetical Nesting*

When GRID loads the canonical library data, it should look like the user is still in the game, outside of the game, right from our third-party environment. All the options should feel very familiar to the original development across location, mission, item and actor organization.

Game Catalog = everything GRID supports.
New Investigation = only things this account actually has connected.

A profile is a subcategory of the game, it should be ghosted until the game is selected.

Problem and Timing, while timing optional, are both children of Class and should be ghosted until the class is selected.

Tool and Mod are independent of one another and do not affect each other, same with Location, Mission/quest, Item and Actor/entity. 

Take notice to how the rows are independent from one another. A request could involve a location, mission, item, and entity all in one ticket without a mod or tool in question.

Timing helps our educated guess, but its not required to give an answer or produce a writeout with receipt.\
```
Game
- Optional
Profile
- Reliant on game
Class
1. Crash & Freeze 
2. Installation Integrity 
3. Asset Mismatch 
4. Missing Texture 
5. Missing Mesh 
6. Materials & Shaders 
7. Lighting & Weather 
8. Terrain & Object LOD 
9. Collision & Navmesh 
10. World Objects 
11. Animation 
12. Creature Behavior 
13. NPC Behavior 
14. Dialogue & Scenes 
15. Quests & Aliases 
16. Combat 
17. Magic & Effects 
18. NPC Appearance 
19. Outfits, Bodies & Physics 
20. UI, HUD & Input 
21. Audio & Voice 
22. SKSE Compatibility 
23. Scripts & Save State 
24. Plugins & Dependencies 
25. Record Conflicts 
26. Distribution & Leveled Lists 
27. Generated Outputs 
28. Performance 
29. Updates & Migration 
30. Archives & Metadata 
31. Metapatching 
32. NSFW
33. Account
34. GRID GUI & features
Problem
- Reliant on class
Timing
- Reliant on problem
- Occurence: once, conditional, every time
Tool
- Reliant on profile
Mod (optional category filtering)
- Reliant on profile
Location (reliant on profile) [L0]
- World.esl/esm/esp/yft [L1]
-- Continent.esl/esm/esp/yft [L2]
--- Country/Provinence.esl/esm/esp/yft [L3]
---- State.esl/esm/esp/yft [L4]
----- County/Region.esl/esm/esp/yft [L5]
------ City.esl/esm/esp/yft [L6]
------- Town/Neighborhood.esl/esm/esp/yft [L7]
-------- Street.esl/esm/esp/yft [L8]
--------- Structure.esl/esm/esp/yft [L9]
---------- Room.esl/esm/esp/yft [L10]
Mission (reliant on profile)
- DLC
-- Storyline/Job Type optional sorting
--- Job.esl/esm/esp/yft
- Mod
-- Storyline/Job Type optional sorting
--- Job.esl/esm/esp/yft
- Online
-- Storyline/Job Type optional sorting
--- Job.esl/esm/esp/yft
- Story Mode
-- Storyline/Job Type optional sorting
--- Job.esl/esm/esp/yft
Item (reliant on profile)
- Ammunition/Projectile
-- Ammo Types
--- Ammo.esl/esm/esp/yft
- Clutter/Props
-- Equipment
--- Equipment Type
---- Equipment.esl/esm/esp/yft
-- Furniture
--- Furniture Type
---- Furniture.esl/esm/esp/yft
-- Tools
--- Type
---- Tool Type
----- Tool.esl/esm/esp/yft
- Consumables
-- Drinks
--- Drink Type
---- Drink.esl/esm/esp/yft
-- Drugs
--- Drud Type
---- Drug.esl/esm/esp/yft
-- Food
--- Food Type
---- Food item.esl/esm/esp/yft
-- Ingredients
--- Ingredient Type (Elemental, Faunal, Floral, Mechanical)
---- Ingredient.esl/esm/esp
-- Posions
--- Posion Type
---- Posion.esl/esm/esp
-- Potions
--- Potion Type
---- Potion.esl/esm/esp
-- Soul Gems
--- Gem Type.esl/esm/esp
- Magic
-- Magic Class
--- Spell.esl/esm/esp/yft
-- Powers
--- Ability Class
---- Ability.esl/esm/esp/yft
- Vehicles
-- Vehicle Class/Manufacturer optional sorting
--- Model.esl/esm/esp/yft
- Weapons
-- Weapon Type/Class optional sorting
--- Weapon.esl/esm/esp/yft
- Wearables
-- Accessories
--- Type/Class
---- Item.esl/esm/esp/yft
-- Head
--- Type/Class
---- Item.esl/esm/esp/yft
-- Top
--- Type/Class
---- Item.esl/esm/esp/yft
-- Bottom
--- Type/Class
---- Item.esl/esm/esp/yft
-- Hands
--- Type/Class
---- Item.esl/esm/esp/yft
-- Feet
--- Type/Class
---- Item.esl/esm/esp/yft
-- Outfit
--- Outfit Class
---- Outfit.esl/esm/esp/yft
Actor (reliant on profile)
- Player Character
-- Actor.esl/esm/esp/yft
-- Actress.esl/esm/esp/yft
- Non Player Character
-- Faction
--- Actor.esl/esm/esp/yft
--- Actress.esl/esm/esp/yft
Goal (deterministic resolution)
- Reliant on problem and any optionals.
```

Fixed universal roots; evidence-derived specialization beneath them.
- never invent missing categories;
- never force every game to fill every branch;
- preserve source-owned names;
- allow arbitrary depth below the universal scaffold;
- one canonical entity can appear through multiple evidence-backed views without duplication;
- unresolved mappings stay unresolved instead of being guessed;
- new verified structures persist and become reusable for that game/source family;
- Workstation Refresh reruns the same contract against existing registrations.

**Fractional Levels**
Row Four uses **fixed integer levels as GRID semantic anchors** and **fractional levels as extensibility space between them**.
```text
L0        Selector domain/root
L1–Ln     Stable semantic anchors
n.x       Evidence-backed hierarchy between anchors
```

Integer levels define GRID's cross-game mold. Fractional levels allow a game's actual structure to exceed that mold **without changing the meaning of the integer anchors or forcing the game into an inaccurate hierarchy**.

Rules:
- Fractionals may exist between **any two integer anchors**.
- Fractionals are optional, sparse, and materialize only from admitted evidence.
- Their meaning is **not globally predefined**; registration determines what a fractional node represents.
- Empty integer and fractional levels are skipped.
- Multiple fractional depths may exist where the canonical graph requires them.
- Canonical graph depth is not limited by the displayed Row Four mold.
- `Miscellaneous` handles selectable leaves without a meaningful intermediate parent.
- Fractional levels represent **real intermediate hierarchy**.
- GRID never invents a fractional level merely to make a branch fit.

For Location specifically, `L0 = Location` because L0 represents the **entire selector domain**, not a geographic scale. `L1 = World` remains the first fixed geographic anchor.

Example:
```text
L7  Town / Neighborhood
    └─ Vespucci
       └─ L7.5
          ├─ Vespucci Beach
          └─ Vespucci Canals
L8  Street
```

`L7.5` proves the mechanism; it does **not** define a universal meaning for `.5`, nor is the mechanism specific to Vespucci, GTA, Location, or `.5`.

The same fractional-level capability applies to **Location, Mission/Quest, Item, and Actor**, according to each selector's semantic mold and admitted registration evidence.

> *GRID's mold adapts to the game's evidenced structure; the game is never coerced to fit GRID's mold.*

**Miscellaneous**
`Miscellaneous` prevents selectable leaves from crowding a selector branch when no meaningful intermediate parent exists.

At any level in **Location, Mission, Item, Actor, or another hierarchical selector**:

- Use evidence-backed/source-native organization when available.
- Never force an entity into an inaccurate category merely to satisfy the schema.
- When multiple selectable entities belong directly to the current branch without an appropriate intermediate parent, group them under `Miscellaneous`.
- `Miscellaneous` is presentation structure, not canonical identity or an unresolved-data state.
- If better organization becomes available, project the entity under its proper parent instead.

**Organize without inventing.**

**Dropdowns**
._____________.
| Class       |
| @ Selection |
| $ Selection |
| # Selection |
| % Selection |
| & Selection |
|_____________|

Class isn't merely text taxonomy. Each Class should have a stable IconId, and the same icon identity should render in both places:
```
Class
────────────────
! Crash & Freeze
⚔ Combat
♟ Creature Behavior
◈ Dialogue & Scenes
...
```

and then:
```
Composer preview

[!] Cashing Out Mission Crash
```
- **[Class icon] Ticket Name**

Select any level in the dropdowns:
click label → select this Location/mission/item/faction
click `>` → descend into sub-list/leaves

.________________. 
| Item           | 
| Armor        > |  ← nesting: category/subfolder-title promoted to first row
| Ammo         > | 
| Clothing     > | 
| Clutter      > | 
| Consumables  > |
|_...____________|

.________________. 
| Tongva Hills   | 
| [Street]     > | ← logical nesting
| [Street]     > |
| [Street]     > |
| [Street]     > |
| [Street]     > |
|_...____________|

._____________________.
| Galileo Observatory |
| [Area-Room]         | ← selectable leaves
| [Area-Room]         |
| [Area-Room]         |
| [Area-Room]         |
| [Area-Room]         |
|_..._________________|

"Other" Behavior -- List View At Root:
.________________. 
| [selectorName] |
| [Subfolder]  > |
| [Subfolder]  > |
| [Subfolder]  > |
| [Subfolder]  > |
| Other          |
|________________|

**Interactivity**
Click `Other`:
- flyout closes;
- exactly one 28px x 84px field appears directly below that selector;
- only that selector gains the extra row underneath;
- entered text becomes unresolved user context;
- it never magically becomes registered knowledge.

Like so:
```
             .__________. .__________.
             |___game___| |_profile__| ← 28px x 84x selector
      .__________. .__________. .__________.
      |__class*__| |_problem*_| |__timing__|
             .__________. .__________.
             |___tool^__| |___mod^___|
.__________. .__________. .__________. .__________.
|_location^| |_mission^_| |___item^__| |__actor^__|
.__________. .__________. .__________. .__________.
|_freeText_| |_freeText_| |_freeText_| |_freeText_| ← Free-Form Text Field
                   .__________.
                   |___goal*__|

* required
^ multi-select
```

Row 2 always three equal cells:
|   Class   |  Problem  |  Timing  |

and row 4 always four equal cells:
| Location | Mission | Item | Entity |

The flyouts can be much wider than those little selectors, so the narrow closed control does not need enough width to display every possible option in full. That's the key.

Multi-Select Preview Behavior:
List up to first three selected items neatly under each parent selector in the fourth row.
```
.__________.
|_location_|

x Strawberry
x DeSanta...
  x Davis
    ...
```

Hovering over "..." shows full selection as popup comment card at full length and width of the list. The free-form text field clears after each submission (`ENTER`); submitted text relocates to preview in order of selection. Order of selection stays first→last for all multi-select items.

---

# Registered Knowledge Graph [Canonical Reference Library](G,P,C,P,T,T,M,L,M,I,A,G)

> *Powered by Discord x Fandom x Nexus x Reddit x Steam*

Registration
Registration makes games, mods, and tools understood throughout GRID.

Registration is metadata-first, not DIF-first. Data must load across the product, **from the Workstation to the DIF**, using the canonical library as the shared source of truth.

Registration should:
- Discover and index supported sources.
- Extract identities, relationships, requirements, and instructions.
- Resolve the most likely answer deterministically when evidence supports it.
- Preserve ambiguity instead of guessing when it does not know.
- Record provenance for admitted knowledge.
- Make registered knowledge reusable throughout GRID.

The registration engine’s job is not to know every game beforehand. It should follow a deterministic checklist:
```
discover source
→ identify entity
→ identify parent/category relationships
→ map into nearest valid GRID root
→ preserve source-native deeper hierarchy
→ resolve names/aliases
→ preserve ambiguity
→ attach provenance
→ validate
→ register
→ prepare
```

Original instructions, wherever sourced (Nexus), are part of registration, including installation, configuration, usage, compatibility, update, uninstall, and troubleshooting guidance.

**Register once. Resolve once. Use everywhere.**
Catalog Support:
```
Upstream game definitions
        ↓
GRID import/generation pipeline
        ↓
canonical GRID game definitions
        ↓
Game Catalog
├── A...
├── B...
├── C...
├── ...
├── Skyrim Special Edition
├── ...
└── hundreds more
```

KnowledgeEntry:
- KnowledgeId
- GameId
- Type
- DisplayName
- CanonicalName
- Aliases
- SourceKind
- SourceUri
- FileEvidence
- ModOrigins
- ProfileApplicability
- ParentRegion
- Relationships
- VerificationState

**Sources should own names (including aliases):**
Canonical source value — copied exactly from the authoritative/reference source.
Source identity — stable identifier from that source where available: form ID, editor ID, wiki page ID/title, mission ID, plugin record identity, etc.
GRID identity — a deterministic internal key pointing at that source identity. It is not permission to rename the thing.

If GRID can't resolve what you selected, that's when the UI responds with the blocker:
- Profile not recognized
- GRID couldn't identify a supported mod-manager profile at this location.
- Choose the profile/instance directory containing the manager's configuration.
- Browse again

So a GTA location could look conceptually like:
```
Location:
  Tongva Hills

child:
  Vineyard

sources:
  - game files
  - GTA wiki

relationships:
  mission:
    - <mission ID>

verification:
  file-backed
```

And a modded Skyrim actor could be:
```
Actor:
  Aela the Huntress

base:
  Skyrim

affectedBy:
  - Pandorable's NPCs
  - armor replacer X

locations:
  - Jorrvaskr
  - Whiterun

records:
  NPC record IDs...

verification:
  plugin-backed
```

Do not assume every game exposes stable internal mission IDs. Some will, some won't, and mods may define their own quests. GRID can have its own canonical investigation IDs mapped to game/native/wiki/mod identifiers where evidence exists. That keeps the UI stable without pretending every game's internals are identical.

**Evidence Ruling Tags:**
FILE VERIFIED
- Directly demonstrated by game/mod files.
REFERENCE VERIFIED
- Supported by an approved external source such as a wiki/Nexus page.
CORRELATED
- A file identity and reference identity were deterministically matched.
UNRESOLVED
- User-entered `Other` or an unmatched reference.

**Lifecycle**
> *The user starts from the Game Catalog.*

Game Catalog:
```
ADD GAME
   ↓
Discover/select installation
   ↓
Register GAME to account
   ↓
No imported profile was selected
   ↓
Create ONE new default Grid profile
   ↓
Default name = game name
   ↓
User can rename/delete/add profiles later
```
```
ADD PROFILE
   ↓
Discover actual profiles from MO2 / Vortex
   ↓
User selects existing profile
   ↓
Is its GAME already registered?
   ├── YES → attach profile to that game
   └── NO  → register the game automatically
             and attach selected profile
```

Existing MO2/Vortex profiles do not change the rules. You chose Add Game, therefore GRID creates a new profile.
Detection can eventually insert the half-step:
```
ADD GAME
   ↓
Discover/register game installation
   ↓
AUTO-DETECT EXISTING PROFILES
   ↓
┌────────────────────────────────────────────┐
│ Existing profiles found                    │
│                                            │
│ MO2                                        │
│ - Profile A                                │
│ - Profile B                                │
│                                            │
│ Vortex                                     │
│ - Profile C                                │
│                                            │
│ [Import selected]   [Create new profile]   │
└────────────────────────────────────────────┘
```
```
GAME REGISTRATION
    ↓
Base game files
+ trusted web references
+ known game metadata
    ↓
GRID Game Knowledge Catalog
    ↓
Locations
Missions/Quests
Items
Actors
relationships between them
```

Automatic detection should not replace manual routing:
```
Local installation

○ Special Edition
  D:\SteamLibrary\steamapps\common\Skyrim Special Edition
  Steam · ReadyForReview

  Browse to another installation
```

You select Browse to another installation, choose:
`D:\Modding Skyrim\UNDEFEATED\Skyrim Special Edition`

Then GRID validates that installation and reruns profile discovery against that selection.

At that point:
```
GRID discovered 1 existing profile

UNDEFEATED
Mod Organizer 2
1,393 enabled mods · 1,274 active plugins

[Use UNDEFEATED]

[Create new profile]
```

And importantly, Browse should be present whether GRID found 0, 1, or 10 automatic candidates. Automatic detection must never strand the user inside GRID's assumptions.

Game = the actual installed game.
Profile = game + installation + mods + manager state + configuration + tools + the rest of the environment required to represent that game as the user actually plays it.
MO2/Vortex are sources/managers of those profiles.
GRID's job is to discover/connect and represent them, not invent a competing definition.

GRID must operate independently; importing MO2 or Vortex profiles is optional.
- **GRID owns** games, profiles, canonical mod identity, and native configuration.
- **MO2/Vortex are providers**, exposing existing installations, profiles, and mod state through adapters.
- GRID never merges their working directories.
- GRID-native profiles require neither manager.
- Deployment backends translate GRID profile state into playable game state.

> *One GRID model. Multiple providers. Independent operation.*
> ```
> GRID
> ├── Games
> │   └── Add Game
> │       └── creates default Profile: <Game Name>
> │
> └── Profiles
>     └── Add Profile
>         └── explicitly create another profile
> ```

Add Game: choose the game from the catalog → connect/detect the installation → GRID creates the initial/default profile named for that game when there isn't an existing manager profile being connected.

Add Profile: connect an existing MO2/Vortex profile → GRID must resolve its game, installation, manager/instance, mods, plugins, configuration, etc. during connection. It cannot create some empty GRID placeholder and fill it in later.

```
MOD REGISTRATION
    ↓
mod files
+ mod metadata
+ Nexus/wiki/source references
    ↓
extends / overrides / links into
the same Game Knowledge Catalog
```
```
Observed unknown tool
CheraxLoader
      ↓
inspect local artifacts
      ↓
Cherax v5.49.0 EE b10767
      ↓
"EE" meaning unresolved locally
      ↓
seek permitted compatibility evidence
      ↓
corroborate identity/version/edition
      ↓
produce provenance-backed compatibility record
      ↓
tool.cherax
  └─ game.grandtheftautov-enhanced
```

Cherax already identifies itself as GTAV EE, Steam, version 5.49.0, and explicitly says “Cherax for Grand Theft Auto V Enhanced Edition.” GRID doesn't need to certify Cherax before you're allowed to launch it. You point GRID at the executable, GRID remembers the launch configuration, and Run launches it.

The important separation is:
```
TOOL LAUNCH CONFIGURATION             GRID INTEGRATION KNOWLEDGE

Name                                  Canonical ToolID (if resolved)
Executable                            Known compatible games
Start in                              Version/build evidence
Arguments                             Capabilities GRID understands
Profile/game association              Evidence adapters
        ↓                                      ↓
     RUN IT                            richer GRID behavior
```

The left side must work even if the right side is completely unresolved.

So an unknown executable can still be:
`New Executable → Browse → select .exe → optionally name it → Apply → select from workstation dropdown → Run`

**Data Intake Form (DIF)**
- Game (G1) → Profile (P1)
- Class (C) → Problem (P2) → Timing (T1)
- Tool (T2) | Mod (M1)
- Location (L) | Mission (M2) | Item (I) | Actor (A)
- Goal (G2)

The DIF must resolve through the Canonical Reference Library:
```
DATA INTAKE FORM
    ↓
selected game/profile/mod context
    ↓
query local registered knowledge
    ↓
show coherent dropdown options
```

Codex shouldn't personally hallucinate all of Los Santos or all of Skyrim. GRID should acquire and register that knowledge deterministically once, then reuse it forever until its sources change.
```
Wiki:
Vineyard in Tongva Hills

Game evidence:
location/interior/mission identifiers

Correlation:
proven mapping
```

If you install Wearable Lanterns, then mod registration should discover:
```
Mod: Wearable Lanterns
adds:
- wearable lantern item(s)
possibly:
- recipes
- keywords
- equipment slots
- meshes/textures
- scripts
```

Everything should be selectable as investigation context, while GRID must distinguish context from capabilities it knows how to operate.

So selecting Cherax could truthfully tell the request:
CheraxLoader is part of this environment / relevant to this investigation.

That does not give GRID permission to run Cherax, understand Cherax, collect Cherax-specific evidence, or claim compatibility. Those require their own capabilities/evidence.

GRID should not only inspect Cherax when you submit a ticket. Once you've registered:
```
CheraxLoader
C:\GTA\CheraxLoader.exe
```

the Tool Manager can eventually also know its associated observation surfaces:
```
Executable
Logs
Console
Config
Version
Process
Child processes
Known output formats
```

Then while GTA is running, GRID can ingest observations continuously.

Not:
`duplicate network ID = attack`

but:
```
09:55:34.576
Tool: Cherax 5.49.0 EE
Severity: ERROR
Event:
Attempted to register a network object
with an ID that already exists: 138

Observed stack:
Cherax
GTA5_Enhanced
...

Classification:
UNRESOLVED

Correlation:
[future events]
```

If five minutes later GTA drops from the lobby, now GRID has chronology.
If the same signature happens across twelve sessions before twelve desyncs, now GRID has evidence of correlation.
If it happens 200 times while you play perfectly normally, GRID learns that it isn't a useful discriminator.

---

# Modular Deterministic Branching Orchestration

> *MDBO is a deterministic capability architecture for turning intent into governed, evidence-backed action.*

Using the Registered Knowledge Graph, GRID knows how to investigate a game failure on the machine it is managing, acquire deterministic evidence, narrow the cause, apply an authorized repair, and prove whether the repair worked.

Generated proposals may enter governance.
Only verified, admitted fixes may execute as canonical repairs.
```
User:
"Why am I severely thirsty after drinking four bottles?"
        ↓
GRID gathers evidence
├─ active MO2 profile
├─ enabled mod/plugin order
├─ save/runtime context where available
├─ uploaded screenshot/media
└─ relevant configuration
        ↓
GRID invokes deterministic plugin inspection
(SSEEdit/xEdit automation or equivalent xEdit-backed capability)
        ↓
Evidence:
├─ "Severe Thirst"
│  └─ FormID 0B0048FA
│     └─ iNeed.esp
│
├─ "Thirst: Sated"
│  └─ FormID 1305C479
│     └─ SunHelmSurvival.esp
│
├─ consumed water FormID
│  └─ originating/winning plugin
│
└─ relevant overrides / keywords / scripts / patches
        ↓
GRID reasons over THAT evidence
        ↓
"Two systems are independently tracking thirst.
The bottle you consumed is/isn't recognized by iNeed because..."
```

**MDBO — Core Contract**
1. **Universal semantics.** Every request is independently evaluated against seven universal L1 Skill Classes: **Entities & Artifacts; Structure & Relationships; Specification & Rules; Interfaces & Exchange; Execution & Behavior; Condition & Integrity; Lifecycle & Transitions.** L1 alone is universal; L2–L8 are product-specific specialization.

2. **Parentage.** Every capability below L1 has one semantic parent. Depth represents **specialization, not power**. Capabilities may be unequal in size or implementation.

3. **Composition.** Semantic lineage is a tree; execution is a graph. One request may simultaneously select capabilities from multiple branches and depths. Their evidence/results compose to satisfy the request.

4. **Select or Create.** For each obligation, MDBO selects sufficient existing capabilities. If none suffice, it returns a governed semantic gap. Capability creation follows: **Gap → Proposal → Scope → APPROVE | MODIFY | REJECT → Realize → Verify → Register.**

5. **Realizations.** Capabilities are semantic identities; Realizations perform them. Multiple capabilities may share a module/script, and one capability may have multiple Realizations. Filesystem structure does not define the Map.

6. **Operation.** All work is governed by OBSERVE → ASSESS → DECIDE → [ACT] → [VERIFY], with ACT and its corresponding VERIFY entered when state-changing or controlled-test work is selected.

7. **Determination.** MDBO exhausts permitted evidence until a proposition is **ESTABLISHED, REFUTED, INDETERMINATE, EXHAUSTED, or BLOCKED**.

8. **Growth.** Verified new capabilities persist as reusable Skills. Models may propose hypotheses or artifacts; **evidence determines truth, governance determines admission, and VERIFY determines completion.**

Universal L1 Class Scopes:
```
ENTITIES & ARTIFACTS
    → THING semantics

STRUCTURE & RELATIONSHIPS
    → RELATION semantics

SPECIFICATION & RULES
    → PRESCRIPTION semantics

INTERFACES & EXCHANGE
    → EXCHANGE semantics

EXECUTION & BEHAVIOR
    → BEHAVIOR semantics

CONDITION & INTEGRITY
    → CONDITION semantics

LIFECYCLE & TRANSITIONS
    → TRANSITION semantics
```

We want lower-level, reusable capabilities:
inspect file
compare file
inspect plugin
inspect config
query event
inspect dump
resolve dependency
compare baseline
isolate component
backup artifact
modify configuration
replace/restore artifact
verify integrity
launch controlled test
observe result
rollback
```
GRID AGENT (AUTO, DIF, BYOP)
    ↓
MDBO FOUNDATION
    ├── Observe
    ├── Assess
    ├── Decide
    ├── Act
    └── Verify
        ↓
RAW DETERMINISTIC CAPABILITIES
```

Then GRID can compose those capabilities according to evidence.

We create the tools and rules of scientific investigation. GRID determines how to use them on a particular game/problem. When GRID encounters something it genuinely cannot investigate, that's when a general missing capability should be added, not write the answer to that particular problem.
```
symptom
→ historical evidence
→ insufficient
→ GRID chooses next observation
→ authorize read
→ correlation
→ reassess
→ diagnosis supported?
     ├─ no → repeat
     └─ yes
          ↓
       choose canonical repair
          ↓
       authorize write
          ↓
       act
          ↓
       verify
          ↓
       PASS → receipt
       FAIL → reassess / rollback
```

MDBO Hive:
```
Feature-based folder structure
> *Globals & Centralized Code*

root/
- .github/
- attached_files/ *
- config/
- core/
-- api/
-- app/
-- assets/
-- features/ (centralization)
--- product/
--- users/
--- sales/
-- public/
-- src/ (MDBO)
- database/
- docs/
-- how-to/
-- images/
-- reference/
-- reports/
-- tutorials/
-- index.md
-- ...various categories...
- pkg/
-- lib/
-- utils/
- tests/
- tools/ (auth, auto, drbt, start)
-- scripts/
- .code *
- .code_integration_files *
- .env
- .gitignore
- code.md *
- LICENSE.md
- README.md
- requirements.txt

GitHub will show:
- config/
- core/
- database/
- docs/
- pkg/
- tests/
- tools/
- LICENSE.md
- README.md
- requirements.txt
```

**File & Media Ingestion**
```
                     SHARED CONTENT PROCESSOR
                              │
     ┌────────────────────────┼────────────────────────┐
     │                        │                        │
 Documents                  Images                 Media
 PDF/DOCX/TXT/etc.     screenshots/photos      audio/video/etc.
     │                        │                        │
     └────────────────────────┼────────────────────────┘
                              ▼
		     Motion Optical Character Recognition
                              ▼
                 normalized content/artifacts
                              ▼
                       ACTIVE CONTEXT
                              │
        ┌─────────┬───────────┼─────────┬─────────┐
        ▼         ▼           ▼         ▼         ▼
      Chatty     GRID        CODE    CleanHouse  Mirage ...
```
```
Upload
  ↓
Content Intake
  ↓
MOCR
  ↓
Context Artifact
  ↓
Assistant's active context
```

Reused Components:
```
Canonical modules
│
├── Upload
│   ├── file selection / drop
│   ├── validation
│   ├── intake lifecycle
│   └── attachment identity
│
├── Document Processing
│   ├── parse
│   ├── structure
│   └── provenance
│
├── Media Processing
│   ├── image
│   ├── audio
│   └── video
│
├── MOCR
│   └── motion / temporal perception
│
└── Context
    └── convert processed artifacts → active assistant context
```

Repository Assembly:
```
Chatty      = Upload + Documents + Media + MOCR + Context + ...
GRID        = Upload + Documents + Media + MOCR + Context + game capabilities
CODE        = Upload + Documents + Media + Context + repo capabilities
VVAULT      = Upload + Documents + Media + persistence + ...
CleanHouse  = Upload + Media + Context + ...
Mirage      = Upload + Media + MOCR + ...
Quantum     = Upload + Media + MOCR + Context + ...
```

---

# Future Development

**Mirage Integration/Succession**

**Community Game Snapshots**
Loads from games catalogue  or game snapshot banner into main panel.

Community insight, new mods, popular mod collections from nexus/wabbajack/others, [modded] streaming.
- Link to streams, not full live web player. Realtime screenshot of the stream itself though.

Refresh page button should show content updated.

**GRID GTA V Online Mod Menu — Protection & Mod Health**
GRID will eventually extend beyond mod management into **player protection, community safety, and proactive game health**, carrying forward the original Elite Custom Worldwide mission:

> *Restoring balance, one lobby at a time.*

**Player Protection**
For supported online games, GRID may provide non-injecting companion capabilities such as:
- Incident evidence and reporting
- Personal block/watch lists
- Reputation and documented interaction history
- Community and cooperative event organization
- Safe-session guidance and migration
- Optional external privacy/VPN integration

GRID Mod Menu must not claim protections it cannot technically provide.

**Mod Health & Updates**
GRID should detect problems **before the player discovers them through crashes or broken gameplay**.

Future capabilities include:
- Installed vs. available version tracking
- Multi-select mod updates
- Manual, assisted, automatic, and pinned update policies
- Runtime and dependency compatibility checks
- Detection of outdated DLLs and manually installed mods
- Hash-based package/version recognition
- Dependency-aware update ordering
- Pre-update snapshots and rollback
- Post-update validation
- Regenerated-output and patch revalidation
- Detection and explanation of unexpected mod/plugin state changes

GRID should evaluate the **entire update chain**, not force users into crash-by-crash troubleshooting.

**Future principle:** detect risk, explain impact, update safely, verify afterward, and never silently change the player's configuration.

---

# Conclusion and Next Steps

**GRID Milestones**
1. Add game support (DIF)
2. “Strong Arm Tactics” (GTAVEO)
3. Simple modding (GTAVLS)
4. Finish UNDEFEATED
   1. CleanHouse Scan
   2. Hydro Repair
   3. simDrive x Mantella
   4. Orange MetaPatcher
   5. Wabbajack distribution
5. Create GRID mod menu
   1. simDrive
   2. GTA V Online desyncs
   3. BattleEye immunity
   4. Capture, preserve & report
      —————————
   5. Injection
   6. Mitigation and prevention
   7. Whole lobby protection
   8. Scripts
6. Repair engine
7. Deterministic menu generator 
8. Full Steam Library
9. VoXoL
10. Game publishing

✦ ✦  After AG ✦ ✦

11. GRID Console
   • Gaming for developers
   • *It just makes sense.*