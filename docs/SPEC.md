# Specification

This is the detailed description of what toobusy does. It grows one part at a time; [IDEA.md](IDEA.md) says what the tool is meant to be, and where the two disagree, this file wins. So far it covers setting a project up: the settings file, `toobusy init`, `toobusy doctor`, and what the tool says in a project that is not set up.

## Setting a project up

### Scope

In scope:

- the settings file in `.toobusy/`: its format, its first sections, reading and validating it;
- `toobusy init`: an interactive setup, and the same setup driven by options alone;
- `toobusy doctor`: the check that everything a run needs is installed and logged in;
- the message of `toobusy` and `toobusy run` in a project that is not set up.

Out of scope, each a later part of this specification:

- reading the queue and running tasks; the settings below are written and validated, and nothing consumes them yet;
- the board's statuses, the order of tasks, blocking links between tasks;
- the prompts and the contract with the assistant; Claude Code skills are not used;
- the assistant's model, effort and permissions;
- choosing an assistant per task, for example by a label;
- the checks of the working copy before and after a task;
- local state of a run and the `.gitignore` that keeps it out of the repository.

### The project and its settings

A project is a git repository. toobusy finds it by walking up from the current folder to the root of the working copy; the settings live in `.toobusy/settings.toml` at that root. Outside a git working copy every command but `--help` and `--version` fails with `toobusy: not inside a git repository` and exit code 2.

The settings are committed, so that everyone who clones the repository gets them. They hold nothing personal: no tokens, no paths of one machine.

```toml
version = 1

[tracker]
type = "github"
board = "https://github.com/orgs/bitpatch/projects/3"   # optional

[queue.labels]
blocking = ["manual", "draft"]              # a task with any of these is never taken
take = ["feature", "bug", "chore", "docs"]  # a task needs one of these; empty means any task

[assistant]
type = "claude-code"
```

| Key | Meaning | Validation |
|---|---|---|
| `version` | version of the settings schema | must be `1`; a higher one fails with "made by a newer toobusy" |
| `tracker.type` | the tracker adapter | `github` only |
| `tracker.board` | the GitHub Projects board of the tasks | optional; `https://github.com/orgs/<org>/projects/<n>` or `https://github.com/users/<user>/projects/<n>` |
| `queue.labels.blocking` | labels that keep a task out | list of label names; may be empty |
| `queue.labels.take` | labels that let a task in | list of label names; may be empty; must not share a label with `blocking` |
| `assistant.type` | the assistant adapter | `claude-code` only |

The settings do not name the repository. The tasks are the issues of the repository the project is cloned from: the GitHub repository of the `origin` remote. A project whose `origin` is not a GitHub repository has no tasks to take.

An unknown key, a missing required key and a value of the wrong type are errors that name the file, the key and the line. Validation of the file never calls the tracker: whether the board and the labels exist is `doctor`'s business.

The file that `init` writes carries a short comment over each section. `init` on an existing file changes the values it asks about and keeps every other key and comment as it is.

#### The current milestone

Only the tasks of the current milestone are taken. Which milestone that is, is not a setting of the project: it is decided when a run starts, by one of the rules below. How a run is told the rule is to be settled with the run itself.

| Rule | The current milestone is |
|---|---|
| `lowest-version` | the open milestone with the lowest version in its title. The version is the first run of dot-separated numbers in the title (`v.0.2.0`, `v1.4`, `Release 2.0`), compared number by number. Open milestones without a version are ignored. |
| `earliest-due` | the open milestone with the earliest due date. Open milestones without a due date are ignored. |
| `fixed` | the open milestone that is named. |
| `none` | none: milestones play no role and tasks are taken from the whole repository. |

When a rule other than `none` finds no milestone, no task is ready, and the tool says which rule found nothing. `lowest-version` is what the original script does.

### Not set up

In a git repository without `.toobusy/settings.toml`, `toobusy` and `toobusy run` (with any options) print to the error stream and exit with code 2:

```
toobusy: this project is not set up yet.
Run `toobusy init` to set it up.
```

The message is coloured: `toobusy:` in the muted colour, the command between backticks in the accent, the rest in the terminal's own colour. The text is the same without colour.

`toobusy` without a command in a project that is set up shows the help, as it does now. `--help` and `--version` work everywhere.

### The screen

In a terminal `init` and `run` open a screen of their own, the alternate screen that editors use. What the terminal showed before stays untouched under it. When the command ends, however it ends, the screen is closed, the terminal is back as it was, and under its old lines the command leaves a short report of what was done.

From top to bottom the screen is:

```
                                                                         the bar: three lines on grey
  toobusy · ~/Projects/toobusy                 Setting up this project · demo

 ✔ Tracker          GitHub                                what is answered or done already
 ✔ Project          Toobusy  https://github.com/orgs/…

 Blocking labels                                          the question: what is asked
 Tasks with any of these labels are never taken. Choose none to block nothing.
 ────────────────────────────────────────────────────
 ❯ ◯ bug                                                  the choice: where the user acts
   ◉ manual
 ────────────────────────────────────────────────────
 ↑↓ move · space select · enter confirm · esc back · ctrl+c exit      the keys

 ●──◉──○                                                  the steps, on grey
```

- The bar names the tool and the folder of the project, shown from `~` when it is under the home folder, and says at its right what the command is doing. It is three lines on a grey ground: an empty one, the text, an empty one.
- The question, the choice and the keys are three parts with a rule between them. The question is its name and a line that says what to do. The choice is the only part the user acts in, and what waits for the user there blinks, smoothly, once in a second and a half. The rule is the same on every screen:
  - A line that is chosen, the one the pointer `❯` stands on, blinks whole: its text glows in the accent and fades to the colour of any text, never to a grey. The pointer stays where it is, and the line is read as well at every moment.
  - Where a text is typed there is a cursor of the classic shape, a block in the accent with the character it stands on drawn over it, that fades away to nothing and comes back. A line where a text is typed has no pointer.
  - A screen may have both, as the list of the boards has under its filter.
  - The blink keeps its time: a key or a change of the chosen line does not start it again.
  - The cursor of the terminal is hidden, because whether it blinks is up to the terminal. A terminal of sixteen colours has the two ends of the blink without the fade, and without colours nothing blinks: the terminal's own cursor stands in the text or on the pointer.
- The keys are hints of the form `enter choose`: the name of each key is a little lighter than what it does, so that the eye finds it.
- Choices stand one under another, never side by side.
- The body between the bar and the question gives way when the window is short, its oldest lines first.
- The last line of the setup, after an empty one, shows its steps on the same grey as the bar, as marks joined by lines and without their names: `●` for those that are done, `◉` for the one that is asked, `○` for those to come. The confirmation is not a step: when it is asked, every mark is done. Marks that do not fit go on to the next line.
- No line is longer than the window is wide: what does not fit is cut with `…`. The screen is drawn again when the size of the window changes.

**Leaving.** Ctrl+C leaves the screen, but only when it is pressed twice in a row: after the first one the line of the keys says `press ctrl+c again to exit`, and any other key takes the question back.

Without a terminal there is no screen: `init` fails, as described below, and `run` prints plain lines.

### Colours

Every command takes its colours from one palette, defined in one place. Each role has a value for a dark terminal and one for a light terminal:

| Role | Dark | Light | Marks |
|---|---|---|---|
| accent | `#D4503F` | `#B03A2E` | the selected line, the cursor, the chosen tab (as its ground), the mark of a text field, the step that is asked, a command in a message |
| error | `#F0647E` | `#C2255C` | `✘` and the text of a failure, the reason an answer is refused |
| success | `#4EC97A` | `#1A7F37` | `✔`, steps that are done, a board that is linked |
| warning | `#E5C07B` | `#9A6700` | `!` and the text of a warning |
| muted | `#808080` | `#767676` | `○`, steps to come and skipped checks, `fix:`, explanations, rules, what a key does, `toobusy:` in a message |
| quiet | `#B0B0B0` | `#585858` | the name of a key among the hints, a tab that is not chosen |

The bar and the steps of a screen stand on a grey a little off the background, `#303030` in a dark terminal and `#E4E4E4` in a light one; a terminal of sixteen colours has no such ground. The accent is Claude's clay shifted toward red; the error is pinker than the accent, so that the two are told apart. The palette is provisional: the brand colours are a later design decision. There is no colour when `NO_COLOR` is set or the output is not a terminal.

**Which value is used.** The terminal says what it can show; toobusy does not ask it for its background. A terminal with truecolor (`COLORTERM` is `truecolor` or `24bit`) gets the dark values, unless `COLORFGBG` names a light background (its last field is `7` or `15`), and then the light ones. A terminal without truecolor gets the nearest of the sixteen colours: red for the accent, bright magenta for the error, green, yellow and bright black for muted. Dark is the default; a light terminal that does not say so gets the dark values, and some of them, the warning above all, are hard to read on it.

### `toobusy init`

`init` asks what it needs, shows what it is about to do, and on a yes does it: it writes `.toobusy/settings.toml` and, when the user asked for that, makes a GitHub project and links the project of the tasks to the repository. Until that yes it changes nothing, and it never touches the working copy or git.

The steps, in order:

1. **Environment.** The checks of `doctor` that need no settings. Failed checks are shown with their fixes and do not stop the setup; when all pass, nothing is shown and the setup starts with the first question. Without a working `gh`, or when the `origin` remote is not a GitHub repository, the steps below cannot read the tracker: they accept typed values and say that nothing was verified.
2. **Tracker.** GitHub is the only one; it is shown, not asked.
3. **Project.** The GitHub Projects board of the tasks, optional. A project that has one already, the board of the settings or one that is linked to the repository, shows it as the first of two choices, as it stands in the list of the boards, with `Choose another project` under it; one Enter keeps it. That choice, and a project without a board, open four ways to answer, as tabs that Tab goes through: one of the boards the user can reach, the address of a board, a new board with its owner and title, or no board. The address of any page of a project is taken as its board. A chosen board is checked for access and for the token scope it needs.
4. **Blocking labels.** A multiple choice over the repository's labels; nothing is chosen at first.
5. **Labels to take.** A multiple choice over the remaining labels; nothing chosen means any task.
6. **Assistant.** Claude Code is the only one; it is shown, not asked.
7. **Confirmation.** What a yes will do, in the words of the questions and not as the text of the file. A first setup shows the answers and says that they will be written. A setup that exists says what changes in it, each setting as it was and as it will be: `Project: Rocket → none`. Under it stand the things to be done on GitHub: a new board to make, a board to link to the repository. A board that is not linked to the repository yet is linked; one that is linked is left alone, and no link of another board is ever taken away. A no does nothing and exits with code 1. On a yes the board is made, the board is linked and the file is written, in that order. When there is nothing to write and nothing to link, `init` asks no confirmation, says `Nothing to change: the settings already say this.` and exits with code 0.

**Going back.** Escape goes back from every question to the one before, which proposes what was answered there. Escape from the first question leaves the setup with nothing changed and exit code 1, as leaving with Ctrl+C does, and it is asked about in the same way: the keys say `esc exit` there, the first Escape gives `press esc again to exit`, and the second one leaves.

After writing, `init` says that the file is to be committed and that `toobusy doctor` checks the setup.

**An existing setup.** When the settings exist, `init` runs the same steps with the current values proposed, so pressing Enter through it changes nothing. A settings file that does not validate is reported with its errors, and `init` proposes whatever it could read.

**Without questions.** Every answer has an option, and `--yes` accepts the proposed value of every question the options leave open, the confirmation included:

| Option | Answer |
|---|---|
| `--board <URL>`, `--no-board` | the board, or none |
| `--blocking-label <name>` | a blocking label; repeatable |
| `--take-label <name>` | a label to take; repeatable |
| `--yes` | ask nothing |

Without `--yes` the options are the proposed answers of the interactive setup. With `--yes` a value that fails its check — a board that cannot be reached, a label the repository does not have — is an error with exit code 1, and nothing is written. Without a terminal and without `--yes`, `init` fails and names the option.

**A demo.** `init --demo` goes through the interactive setup without touching anything. What it reads for the question about the project is real: the `origin` remote and, through `gh`, the boards of the user. When there are fewer than ten of them, made-up boards follow the real ones, so that there is a list to scroll; the owners a new project can be made for are filled up to ten in the same way. The rest is imitated: the labels are made up, the settings that exist are read, and nothing is made, linked or written. It is there to try the setup and to see what it looks like.

**The questions.** Every question has the shape the screen gives it: its name and a line that says what it is about and what to do, then the choices or the text, then the keys it understands. A refused answer stays in its question, and the reason takes the place of the line under the name.

- A selection moves with Up and Down, a multiple choice marks with the space bar, a confirmation is `Yes` over `No` and also takes `y` and `n`. A list that does not fit shows eight rows, five for the boards, and says above and below how many rows are beyond them: `↑ 2 more`, `↓ 4 more`.
- A text is edited where the cursor is: Left, Right, Home and End move it, Backspace and Delete remove a character, and Ctrl+A, Ctrl+E, Ctrl+U and Ctrl+W do what they do in a shell. A pasted text is drawn once.

The question about the project, when the ways to answer are open:

```
 Project
 The GitHub project of the tasks: their statuses are kept on its board.
 ────────────────────────────────────────────────────
  Your projects   By URL   New project   No project   press tab to switch

 Filter  too
 ❯ Toobusy  https://github.com/orgs/bitpatch/projects/4  linked
 ────────────────────────────────────────────────────
 type to filter · ↑↓ move · enter choose · esc back · ctrl+c exit
```

Every tab stands on the grey of the bar, its name a little quieter than a plain text, and the chosen one is white on the deeper accent; without colours the chosen one is in brackets. The place under the tabs is as tall as the list of the boards, whatever tab is open, so that nothing moves when the tab changes: a list starts at its top, and the line where a text is typed, with what is said about it, stands at its bottom, with empty lines between.

- **Your projects** lists the open boards of the user and of their organisations, those linked to the repository first, each by its title with its address beside it. Typing narrows the list, the best fit first: the board of that address, a title that starts with the text, a title that has it inside, an address that has it, as the name of the owner is. The boards come from one request through `gh` with a timeout of three seconds; when `gh` is missing, not logged in, lacks the scope to read projects or is too slow, this tab is not there.
- **By URL** takes the address of a board or of any page of it.
- **New project** asks whose the board will be and for its title. The owners, the user and their organisations, are a list at the top of the tab, as the boards are in theirs: Up and Down move the pointer, which starts on the owner of the repository. The title is typed in the line at the bottom. The board is made only when the setup is confirmed. The tab is not there when `gh` gave no owners.
- **No project** leaves the setting out.

Escape from these tabs comes back to those two choices when the project has a board, and goes back a step when it has none.

**The report.** When the screen is closed, the terminal gets the name of the command and the folder, the answers as `✔ Blocking labels  manual, draft` lines, and one line that says how the setup ended.

The setup is written against an interface for asking questions, so that tests answer them from a script and the terminal implementation can change without touching the steps.

### `toobusy run`

Only the demo exists so far, and it has nothing to imitate yet. In a terminal `run --demo` opens the screen of a run. Its choice is a line where commands are typed:

```
 ────────────────────────────────────────────────────
 /ex
 ❯ /exit  leave toobusy
 ────────────────────────────────────────────────────
 tab complete · enter run · ctrl+c exit
```

An empty line says `Type / for commands.` A slash lists the commands that fit what is typed after it, those that start with it first; Tab completes the first of them and Enter runs it. A text that is not a command is not run. `/exit` is the one command so far: it closes the screen, as Ctrl+C twice does.

### `toobusy doctor`

`doctor` checks everything a run needs and prints one line per check: passed, failed or skipped, and for a failed one what is wrong and the command that fixes it. It exits with 0 when nothing failed and with 1 otherwise. It changes nothing.

In a terminal the check that is running shows a spinner on its line, and the line is replaced by the result when the check finishes. Without a terminal each line is printed when its check finishes.

| Check | Fails when | Needs |
|---|---|---|
| git | `git` is not on the path | — |
| GitHub CLI | `gh` is not on the path | — |
| GitHub login | `gh auth status` reports no login for github.com | GitHub CLI |
| Claude Code | `claude` is not on the path | — |
| Claude Code login | Claude Code is not logged in | Claude Code |
| settings | the file is missing or does not validate | — |
| repository | the `origin` remote is not a GitHub repository, or that repository cannot be read | GitHub login |
| board | the board cannot be read, or the token lacks the `project` scope | settings with a board, GitHub login |
| labels | a label of the settings does not exist in the repository | repository |

A check whose need is not met is skipped and says what it waits for. Without settings the first five checks run, and the settings check fails with the "not set up" message.

**Fixes.** Each failure names a fix for the platform the tool runs on:

| | macOS | Linux | Windows |
|---|---|---|---|
| git | `xcode-select --install` | the distribution's package | `winget install Git.Git` |
| GitHub CLI | `brew install gh` | link to the official instructions | `winget install GitHub.cli` |
| Claude Code | `curl -fsSL https://claude.ai/install.sh \| bash` | the same | `irm https://claude.ai/install.ps1 \| iex` |
| GitHub login | `gh auth login` | | |
| `project` scope | `gh auth refresh -s project` | | |
| Claude Code login | `claude`, then `/login` | | |

A command of a package manager is proposed only when that manager is on the path (`brew`, `winget`); otherwise the fix is the link to the tool's official installation page. Only macOS is verified by hand in the first version.

**Use by other commands.** `init` runs the checks that need no settings and goes on whatever they say. `run` runs all of them before anything else and, when one fails, prints the failures and exits with code 1 without starting.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | done |
| 1 | failed: a check did not pass, a value was refused, the setup was declined or left |
| 2 | there is no project, the project is not set up, or the command line is wrong |

### Tests

- The settings: reading, every validation rule, writing, and that a rewrite keeps unknown comments.
- The milestone rules, over made-up lists of milestones.
- The setup steps, with the questions answered from a script and the tracker and the environment faked: a first setup, an existing setup, going back, a new board, a missing `gh`, every option, `--yes`, no terminal.
- The screens, through a terminal of a test: scripted keys, a size, and the frames that were drawn.
- `doctor`: each check passed, failed and skipped, and the fix for each platform with and without its package manager.
- The "not set up" message and the exit codes, through the command line.

### To be settled while building

- **How a run is told the milestone rule.** The rule left the settings and the setup; it comes back with `run`.
- **How to tell that Claude Code is logged in** without starting a session.
