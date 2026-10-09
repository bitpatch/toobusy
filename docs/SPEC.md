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
repository = "bitpatch/toobusy"
board = "https://github.com/orgs/bitpatch/projects/3"   # optional

[queue.milestone]
rule = "lowest-version"   # lowest-version | earliest-due | fixed | none
# title = "v.0.2.0"       # with rule = "fixed" only

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
| `tracker.repository` | the repository whose issues are the tasks | `owner/name` |
| `tracker.board` | the GitHub Projects board of those issues | optional; `https://github.com/orgs/<org>/projects/<n>` or `https://github.com/users/<user>/projects/<n>` |
| `queue.milestone.rule` | how the current milestone is chosen | one of the four rules below |
| `queue.milestone.title` | the milestone of the `fixed` rule | required with `fixed`, refused with any other rule |
| `queue.labels.blocking` | labels that keep a task out | list of label names; may be empty |
| `queue.labels.take` | labels that let a task in | list of label names; may be empty; must not share a label with `blocking` |
| `assistant.type` | the assistant adapter | `claude-code` only |

An unknown key, a missing required key and a value of the wrong type are errors that name the file, the key and the line. Validation of the file never calls the tracker: whether the repository, the labels and the milestone exist is `doctor`'s business.

The file that `init` writes carries a short comment over each section. `init` on an existing file changes the values it asks about and keeps every other key and comment as it is.

#### The current milestone

Only the tasks of the current milestone are taken. The rule says which milestone that is at the moment of a run:

| Rule | The current milestone is |
|---|---|
| `lowest-version` | the open milestone with the lowest version in its title. The version is the first run of dot-separated numbers in the title (`v.0.2.0`, `v1.4`, `Release 2.0`), compared number by number. Open milestones without a version are ignored. |
| `earliest-due` | the open milestone with the earliest due date. Open milestones without a due date are ignored. |
| `fixed` | the open milestone named by `title`. |
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

### Colours

Every command takes its colours from one palette, defined in one place. Each role has a value for a dark terminal and one for a light terminal:

| Role | Dark | Light | Marks |
|---|---|---|---|
| accent | `#D4503F` | `#B03A2E` | the `?` of a question, the selected line, step headings, a command in a message |
| error | `#F0647E` | `#C2255C` | `✘` and the text of a failure, removed lines of a diff |
| success | `#4EC97A` | `#1A7F37` | `✔`, added lines of a diff |
| warning | `#E5C07B` | `#9A6700` | `!` and the text of a warning |
| muted | `#808080` | `#767676` | `○` and skipped checks, `fix:`, explanations, `toobusy:` in a message |

The accent is Claude's clay shifted toward red; the error is pinker than the accent, so that the two are told apart. The palette is provisional: the brand colours are a later design decision. There is no colour when `NO_COLOR` is set or the output is not a terminal.

**Which value is used.** The terminal says what it can show; toobusy does not ask it for its background. A terminal with truecolor (`COLORTERM` is `truecolor` or `24bit`) gets the dark values, unless `COLORFGBG` names a light background (its last field is `7` or `15`), and then the light ones. A terminal without truecolor gets the nearest of the sixteen colours: red for the accent, bright magenta for the error, green, yellow and bright black for muted. Dark is the default; a light terminal that does not say so gets the dark values, and some of them, the warning above all, are hard to read on it.

### `toobusy init`

`init` asks what it needs, shows what it is about to write, and writes `.toobusy/settings.toml`. It changes nothing else: not the tracker, not the working copy, not git.

The steps, in order:

1. **Environment.** The checks of `doctor` that need no settings. Failed checks are shown with their fixes and do not stop the setup; when all pass, nothing is shown and the setup starts with the first question. Without a working `gh` the steps below cannot read the tracker: they accept typed values and say that nothing was verified.
2. **Tracker.** GitHub is the only one; it is shown, not asked.
3. **Repository.** Proposed from the `origin` remote; accepts `owner/name` or a GitHub URL. The answer is checked for access.
4. **Board.** An optional URL of a GitHub Projects board; empty skips it. The answer is checked for access and for the token scope the board needs.
5. **Milestone rule.** The four rules, each with the milestone it would choose right now among the open ones (`lowest-version → v.0.2.0`). The proposed rule is `lowest-version` when some open milestone has a version, otherwise `earliest-due` when some has a due date, otherwise `none`. `fixed` goes on to pick one of the open milestones.
6. **Blocking labels.** A multiple choice over the repository's labels; nothing is chosen at first.
7. **Labels to take.** A multiple choice over the remaining labels; nothing chosen means any task.
8. **Assistant.** Claude Code is the only one; it is shown, not asked.
9. **Summary.** What will change in the settings file and where, as a diff: a first setup shows the whole file as added lines, an existing setup only the keys whose values change. A confirmation follows. Declining writes nothing and exits with code 1. When nothing changes, `init` says `Nothing to change`, asks no confirmation, leaves the file as it is and exits with code 0.

After writing, `init` says that the file is to be committed and that `toobusy doctor` checks the setup.

**An existing setup.** When the settings exist, `init` runs the same steps with the current values proposed, so pressing Enter through it changes nothing. A settings file that does not validate is reported with its errors, and `init` proposes whatever it could read.

**Without questions.** Every answer has an option, and `--yes` accepts the proposed value of every question the options leave open, the confirmation included:

| Option | Answer |
|---|---|
| `--repo <owner/name or URL>` | the repository |
| `--board <URL>`, `--no-board` | the board, or none |
| `--milestone <rule>` | the milestone rule |
| `--milestone-title <title>` | the milestone of the `fixed` rule; implies `--milestone fixed` |
| `--blocking-label <name>` | a blocking label; repeatable |
| `--take-label <name>` | a label to take; repeatable |
| `--yes` | ask nothing |

Without `--yes` the options are the proposed answers of the interactive setup. With `--yes` a value that fails its check — a repository that cannot be reached, a label the repository does not have — is an error with exit code 1, and nothing is written. Without a terminal and without `--yes`, `init` fails and names the option.

**A dry run.** `init --dry-run` goes through the interactive setup without touching anything: the machine and the tracker are imitated, with made-up labels and milestones, the settings that exist are read, and nothing is written. It is there to try the setup and to see what it looks like.

**The interface.** The interactive setup uses a selection with the arrow keys, a multiple choice with the space bar, and a text prompt with a proposed value, in the manner of the Claude Code setup. Once a question is answered, its prompt is replaced by one line, `✔ Repository     bitpatch/toobusy`, so that the screen reads as a short history with the current question at the bottom. Colours come from the palette above. The setup is written against an interface for asking questions, so that tests answer them from a script and the terminal implementation can change without touching the steps.

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
| repository | the repository of the settings cannot be read | settings, GitHub login |
| board | the board cannot be read, or the token lacks the `project` scope | settings with a board, GitHub login |
| labels | a label of the settings does not exist in the repository | repository |
| milestone | the rule finds no open milestone — a warning, not a failure | repository |

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
| 1 | failed: a check did not pass, a value was refused, the setup was declined |
| 2 | there is no project, the project is not set up, or the command line is wrong |

### Tests

- The settings: reading, every validation rule, writing, and that a rewrite keeps unknown comments.
- The milestone rules, over made-up lists of milestones.
- The setup steps, with the questions answered from a script and the tracker and the environment faked: a first setup, an existing setup, a missing `gh`, every option, `--yes`, no terminal.
- `doctor`: each check passed, failed and skipped, and the fix for each platform with and without its package manager.
- The "not set up" message and the exit codes, through the command line.

### To be settled while building

- **The terminal library.** Spectre.Console is the first candidate for the prompts, under the same condition; otherwise the three prompts are written by hand.
- **How to tell that Claude Code is logged in** without starting a session.
