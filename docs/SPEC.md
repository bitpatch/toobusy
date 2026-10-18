# Specification

This is the detailed description of what toobusy does. It grows one part at a time; [IDEA.md](IDEA.md) says what the tool is meant to be, and where the two disagree, this file wins. It has two parts. [Setting a project up](#setting-a-project-up) covers the settings file, `toobusy init`, the walk through the setup that `toobusy init --assist` tells an assistant, the milestone of the user, the model and the effort the tasks are done with, the page `toobusy` opens with, `toobusy queue`, `toobusy about`, `toobusy doctor`, and what the tool says in a project that is not set up. [Running the tasks](#running-the-tasks) covers `toobusy run`: the queue, the branch of the tasks, the sessions of the assistant and what they are told, what a run does to the tracker, the limits of usage, and the page of a run.

## Setting a project up

### Scope

In scope:

- the settings file in `.toobusy/`: its format, its first sections, reading and validating it;
- `toobusy init`: an interactive setup, and the same setup driven by options alone;
- `toobusy init --assist`: the walk through the setup, told to the assistant of the owner one step at a time;
- the milestone to work on: the choice of the user, kept on their machine, and `toobusy milestone`;
- the model and the effort the tasks are done with by default: choices of the user too, `toobusy model` and `toobusy effort`;
- `toobusy` without a command: the page with the menu;
- `toobusy queue`: the queue as a run reads it, shown without running it;
- `toobusy status`: whether a run is going on in the working copy;
- `toobusy about`: the text that tells how a run works;
- `toobusy doctor`: the check that everything a run needs is installed and logged in;
- the message of `toobusy`, `toobusy run` and `toobusy queue` in a project that is not set up, and where it names `init --assist`.

Out of scope, each a later part of this specification:

- more than one status of a board to take tasks from, and statuses beyond the three a run knows, as one for a review;
- the messages a session gets as templates of the project: they are texts of the tool;
- a permission mode of the assistant that checks nothing, as `bypassPermissions` of Claude Code: whether toobusy offers one is for the owner of toobusy to decide;
- choosing an assistant per task, for example by a label.

### The project and its settings

A project is a git repository. toobusy finds it by walking up from the current folder to the root of the working copy; the settings live in `.toobusy/settings.toml` at that root. Outside a git working copy every command but `--help`, `--version` and `about` fails with `toobusy: not inside a git repository` and exit code 2.

The settings are committed, so that everyone who clones the repository gets them. They hold nothing personal: no tokens, no paths of one machine.

```toml
version = 1

[tracker]
type = "github"
board = "https://github.com/orgs/bitpatch/projects/3"   # optional

[tracker.statuses]                          # optional: what the board calls the statuses a run knows
todo = "Ready"                              # a task is taken only from it; `Todo` when it is not named
in-progress = "Doing"                       # a task is moved to it before its session; `In Progress`
done = "Shipped"                            # a task is moved to it when it is done; `Done`

[queue.labels]
blocking = ["manual", "draft"]              # a task with any of these is never taken
take = ["feature", "bug", "chore", "docs"]  # a task needs one of these; empty means any task
manual = "manual"                           # put on a task that waits for the owner
interrupted = "interrupted"                 # put on a task that a run had to stop

[assistant]
type = "claude-code"
permission-mode = "dontAsk"                 # optional: the permission mode a session works in; `auto` when it is not named

[session]
skill = "task-auto"                         # optional: the skill of the project a session starts with

[task]
before = "scripts/dev.sh ports free"        # optional: a command a run runs before a task is taken
after = "scripts/dev.sh server stop"        # optional: a command a run runs after the session of a task

[workspace]
branch = "develop"                          # optional: the branch the tasks are done on
```

| Key | Meaning | Validation |
|---|---|---|
| `version` | version of the settings schema | must be `1`; a higher one fails with "made by a newer toobusy" |
| `tracker.type` | the tracker adapter | `github` only |
| `tracker.board` | the GitHub Projects board of the tasks | optional; `https://github.com/orgs/<org>/projects/<n>` or `https://github.com/users/<user>/projects/<n>` |
| `tracker.statuses.todo` | what the board calls the status a task is taken from | optional, `Todo` when it is not there; a name that is not empty; only with `tracker.board` |
| `tracker.statuses.in-progress` | what the board calls the status a run moves a task to before its session | optional, `In Progress` when it is not there; the same |
| `tracker.statuses.done` | what the board calls the status a run moves a task to when it is done | optional, `Done` when it is not there; the same |
| `queue.labels.blocking` | labels that keep a task out | list of label names; may be empty |
| `queue.labels.take` | labels that let a task in | list of label names; may be empty; must not share a label with `blocking` |
| `queue.labels.manual` | the label a run puts on a task that waits for the owner; a task that has it is never taken, whether it is one of `blocking` or not | a label name; not one of `take`; may be one of `blocking`, which keeps out nothing more, and the settings of the menu take it out of them. `queue.labels.owner`, the name it had before, is read in its place in a file that has it; a file that has both does not validate, and `owner` is renamed to `manual` where it stands when the file is written |
| `queue.labels.interrupted` | the label a run puts on a task it had to stop; such a task is taken first | a label name; not one of `take` or `blocking`, and not the label of `manual` |
| `assistant.type` | the assistant adapter | `claude-code` only |
| `assistant.permission-mode` | the permission mode of the assistant a session works in, as [The permission mode of a session](#the-permission-mode-of-a-session) describes | optional; a mode of the assistant that `assistant.type` names, written as the assistant writes it: for Claude Code `auto` or `dontAsk` |
| `session.skill` | the skill of the project a session starts with, as [What a session is told](#what-a-session-is-told) describes | optional; the name of a skill as its command is written, without the `/` before it: letters, digits, `-`, `_`, `.` and `:`, the first a letter or a digit |
| `task.before` | the command a run runs before a task is taken, as [The commands of the project](#the-commands-of-the-project) describes | optional; a line for the shell, not empty |
| `task.after` | the command a run runs after the session of a task, however the task went | optional; a line for the shell, not empty |
| `workspace.branch` | the branch the tasks are done on, as [The branch of the tasks](#the-branch-of-the-tasks) describes | optional; the name of a branch as git takes it, by the rules of `git check-ref-format --branch`: no space, none of `~^:?*[\`, no `..` and no `@{`, no part that starts with `.` or ends with `.lock`, neither `/` nor `.` at either end, not starting with `-`, and not `@` or `HEAD` alone |

The settings do not name the repository. The tasks are the issues of the repository the project is cloned from: the GitHub repository of the `origin` remote. A project whose `origin` is not a GitHub repository has no tasks to take.

An unknown key, an unknown section, a key that is set twice and a value of the wrong type are errors that name the file, the key and the line. A missing required key is named with the line of its section, and without a line when the file has no such section; a file that is not TOML is an error with its line and no key. A label name must not be empty, and where the table says that two settings must not share a label, the labels are compared whatever their case. Validation of the file never calls the tracker: whether the board, its statuses and the labels exist is `doctor`'s business.

**The statuses of a board.** A run knows three statuses of a board: the one it takes a task from, the one it moves a task to before its session, and the one it moves a task to when it is done. `[tracker.statuses]` says what the board of the project calls them, and each of the three that it does not name is called as GitHub calls it on a new board: `Todo`, `In Progress` and `Done`. So a board that keeps the names of GitHub needs no such section, and a board that calls one status otherwise names that one. A name is compared with the statuses of the board whatever the case of its letters and whatever stands between its letters and digits: `in-progress` is `In Progress`, and `Stage 1` is not `Stage 2`; a name that has neither a letter nor a digit, as an emoji, is compared as it is. Two of the three must not name one status, whether the file names them or GitHub does: a run would take a task from where it moves it to. The three mean something only in a project with a board, so a file that names one and no board does not validate. Everything that names a status, to a session and to the owner, names it as the settings do: the first message of a session, the log of a run and `toobusy about`. This document names the three as GitHub does, `Todo`, `In Progress` and `Done`: in a project whose settings call them otherwise, read its names there.

**The permission mode of a session.** Nobody answers the prompts of a session of a run, so it works in a mode of the assistant that goes on with nobody at the keyboard and still checks what it does. `assistant.permission-mode` names it, and the modes are those of the assistant `assistant.type` names: the validator knows them by it, and another assistant will have modes of its own. Of Claude Code there are two:

| Mode | What it checks |
|---|---|
| `auto` | a model of Claude Code looks at every action before it is done, and refuses what goes beyond the task or looks dangerous. A session is started in it when the settings name no mode |
| `dontAsk` | whatever the settings of Claude Code in the project do not allow is refused, and nothing is asked: a session does only what is allowed there |

The other modes of Claude Code are refused, each with the reason: `manual`, its other name `default`, `acceptEdits` and `plan` ask before what they do not allow, or wait for a plan to be approved, and a session in one of them would wait for an answer that never comes: ``must be `auto` or `dontAsk`: in `plan` a session waits for an answer, and nobody answers a session of a run``. `bypassPermissions` checks nothing a session does, and toobusy starts no session in it. A mode is written as Claude Code writes it, `dontAsk` and not `dontask`. What a session is allowed and refused in particular stays where Claude Code keeps it, in the settings of the project in `.claude/`: toobusy does not say it a second time.

No question of `init` asks for the commands, the branch or the permission mode: its options `--before`, `--no-before`, `--after`, `--no-after`, `--branch`, `--no-branch` and `--permission-mode` set them, as [`toobusy init`](#toobusy-init) describes, and without an option `init` keeps them when it writes the file. `Settings` of the menu of [`toobusy`](#toobusy) asks for them, as for the others. `doctor` does not run the commands, and looks at nothing of them. Nor does it look at the branch: which branch is checked out, and whether it is level with its remote, is how the working copy stands at one moment and not what the project has, and a run checks it before every task.

The file that `init` writes carries a short comment over each section. `init` on an existing file changes the values it asks about and keeps every other key and comment as it is; a key the settings have no more, as the board of a project that gives it up, with the statuses that were named for it, or its skill, loses its line, and a section that is left without keys stays. A status is written only when the board calls it otherwise than GitHub does; one that the file names as GitHub does stays as it is written. A file that cannot be changed in place, because it is not TOML or is laid out in a way toobusy cannot edit, is written anew, and nothing of what it had is kept.

#### The milestone to work on

Only the tasks of one milestone are taken, and the user says which: one of the open milestones of the repository, or none, and then tasks are taken whatever their milestone. toobusy never chooses a milestone on its own.

The choice is personal. It is not a setting of the project and is not committed: it is kept on the machine of the user, for each project apart, in `projects.toml` of the user's own toobusy folder. That folder is `$XDG_CONFIG_HOME/toobusy` when the variable is set, `%APPDATA%\toobusy` on Windows, and `~/.config/toobusy` otherwise. On a machine that names none of them, every command that works in a project fails with `toobusy: there is no home folder to keep your choices in` and exit code 2; a demo, which keeps nothing, does not.

```toml
["/Users/ann/Projects/rocket"]
milestone = "v0.3.0"    # an empty title means working without a milestone
model = "opus"          # an empty name means the assistant's own model
effort = "high"
```

A project is known by the path of its root, so a second clone and a worktree have choices of their own. The milestone is kept by its title. The file holds every personal choice of a project, the model and the effort among them, each under its own key; a key that is not there is a choice that is not made. toobusy writes the file whole; a file that is not TOML counts as one that says nothing, and is replaced when a choice is saved.

A choice is checked against the open milestones whenever it is used:

| The choice | Means |
|---|---|
| not made | nothing can be run: the user is asked to choose |
| a milestone that is open | its tasks are taken |
| no milestone | tasks are taken whatever their milestone |
| a milestone that is not open any more: closed, renamed or deleted | nothing can be run: toobusy says so, and the user is asked to choose again |
| a milestone, while the milestones cannot be read | the choice stands, and the page says that it was not checked |

Where milestones are listed, those with a version in the title come first, the lowest version first, and the rest follow by their titles. The version is the first run of dot-separated numbers in the title (`v.0.2.0`, `v1.4`, `Release 2.0`), compared number by number. Each is shown with its due date, when it has one, and the number of its open tasks. `No milestone` is the last of the list.

#### The model and the effort

The model and the effort a task is done with by default are choices of the user as well: they are kept in the same file, for each project apart, and are not committed. Nothing can be run before both are chosen.

- **The model** is the assistant's own, which names no model and leaves the choice to the assistant, or a model by its name. The list offers `Assistant's own`, the names `fable`, `opus`, `sonnet` and `haiku`, and `Other…`, where any name is typed as the assistant takes it. A name is not checked.
- **The effort** is one of the levels `low`, `medium`, `high`, `xhigh` and `max`. A user who has not chosen is proposed `high`. A level is taken whatever the case of its letters; an effort in the file that is not a level counts as one that is not chosen.


A session of the assistant is started with the model and the effort.

### Not set up

In a git repository without `.toobusy/settings.toml`, `toobusy run`, `toobusy queue` and, without a terminal, `toobusy` print to the error stream and exit with code 2:

```
toobusy: this project is not set up yet.
Run `toobusy init` to set it up.
```

Where there is no terminal, the one who reads it may be an assistant, and a third line names where it starts:

```
An assistant that sets a project up for its owner starts with `toobusy init --assist`.
```

The message is coloured: `toobusy:` in the muted colour, the command between backticks in the accent, the rest in the terminal's own colour. The text is the same without colour.

In a terminal `toobusy` without a command does not stop there: it opens its page and sets the project up, as described below. Nor do `run --demo` and `queue --demo`, which make the settings up, as [a demo](#toobusy-init) of a project that is ready does. `--help`, `--version` and `about` work everywhere.

### The screen

In a terminal `toobusy`, `init`, `milestone`, `model` and `effort` open a screen of their own, the alternate screen that editors use; it is entered with the first thing that is drawn on it. What the terminal showed before stays untouched under it. When the command ends, however it ends, the screen is closed, the terminal is back as it was, and under its old lines the command leaves a short report of what was done. A run is not shown on that screen: it is a tape on the terminal's own screen, which stays in its history, as [the page of a run](#the-page-of-a-run) describes.

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
   Back  esc
 ────────────────────────────────────────────────────
 ↑↓ move · space select · enter confirm · ctrl+c exit     the keys

 ●──◉──○──○──○──○                                         the steps, on grey
```

- The bar names the tool and the folder of the project, shown from `~` when it is under the home folder, and says at its right what the command is doing. Where it has nothing of that to say, as over the menu of [`toobusy`](#toobusy) and over the wait while the milestones are read, it says there which toobusy is running: its version after a `v`, `v1.0.0`, the one `toobusy --version` prints. A build in development, whose version has a suffix, `1.0.0.dev`, is told apart by the commit it is built from: `--version` prints it after a plus, shortened to seven characters as git shortens it, `1.0.0.dev+c56f6c2`, and the bar leaves it out. A release prints its version alone. What stands at the right is muted, and it is what gives way when the window is too narrow for both: the folder keeps its room. It is three lines on a grey ground: an empty one, the text, an empty one.
- The question, the choice and the keys are three parts with a rule between them. The question is its name and a line that says what to do. The choice is the only part the user acts in, and what waits for the user there blinks, smoothly, once in 1.6 seconds. The rule is the same on every screen:
  - On the line that is chosen, the one the pointer `❯` stands on, the pointer and the name blink: they glow in the accent and fade to the colour of any text, never to a grey. What explains the name at its right does not blink. The pointer stays where it is, and the line is read as well at every moment.
  - Where a text is typed there is a cursor of the classic shape, a block in the accent with the character it stands on drawn over it, that fades away to nothing and comes back. A line where a text is typed has no pointer.
  - A screen may have both, as the list of the boards has under its filter.
  - The blink keeps its time: a key or a change of the chosen line does not start it again.
  - The cursor of the terminal is hidden, because whether it blinks is up to the terminal. A terminal of sixteen colours has the two ends of the blink without the fade, and without colours nothing blinks: the terminal's own cursor stands in the text or on the pointer.
- While toobusy reads something from the tracker, the choice says what it reads and, under it, shows eleven dots that a light runs along, from the first to the last and back, 1.6 seconds each way: the dot it is on is in the accent, and those it has left fade behind it to the muted colour of the rest. A terminal of sixteen colours has the lit dot without the fade, and without colours the dots stand still. Nobody has to wait: the keys work while toobusy reads. Where there is somewhere to go back to, `Back` stands under the dots from the first moment, and Enter or Escape goes back at once; where Escape leaves the page it leaves it from here too, asked twice, and so does Ctrl+C twice. What was being read is told to stop and is not waited for, so a tracker that never answers holds nobody. Where what is read belongs to one row of a list, the dots stand on that row, after its name, and the rest of the list is used meanwhile.
- The keys are hints of the form `enter choose`: the name of each key is a little lighter than what it does, so that the eye finds it.
- Choices stand one under another, never side by side.
- The question, the choice and the keys stand at the bottom of the window, over the steps of a setup, and the body stands under the bar: in a window taller than the picture above the empty lines are between the body and the question. The body gives way when the window is short, its oldest lines first.
- The last line of the setup, after an empty one, shows its steps on the same grey as the bar, as marks joined by lines and without their names: `●` for those that are done, `◉` for the one that is asked, `○` for those to come. The confirmation is not a step: when it is asked, every mark is done. Marks that do not fit go on to the next line.
- No line is longer than the window is wide: what does not fit is cut with `…`. The screen is drawn again when the size of the window changes.

**Leaving.** Ctrl+C leaves the screen, but only when it is pressed twice in a row: after the first one the line of the keys says `press ctrl+c again to exit`, and any other key takes the question back.

Without a terminal there is no screen: `init` fails unless it is told to ask nothing, as described below, and the other commands print plain lines.

### Colours

Every command takes its colours from one palette, defined in one place. Each role has a value for a dark terminal and one for a light terminal:

| Role | Dark | Light | Marks |
|---|---|---|---|
| accent | `#D4503F` | `#B03A2E` | the selected line, the cursor, the chosen tab (as its ground, which is the light value in a dark terminal too), the step that is asked, the mark that turns and the steps of a plan that are done on the page of a run, the sign of a check that is running, a command in a message |
| error | `#F0647E` | `#C2255C` | `✘` and the text of a failure, the reason an answer is refused, the blocking labels in the settings of the menu |
| success | `#4EC97A` | `#1A7F37` | `✔`, steps that are done, a board that is linked, a value in a list, `current`, the mark of a label of the settings in a multiple choice |
| warning | `#E5C07B` | `#9A6700` | `!` and the text of a warning |
| muted | `#808080` | `#767676` | `○`, steps to come and skipped checks, `fix:`, explanations, rules, what a key does, `toobusy:` in a message, a choice that is off, as a label that another setting holds |
| quiet | `#B0B0B0` | `#585858` | the name of a key among the hints, a tab that is not chosen |

The bar and the steps of a screen stand on a grey a little off the background, `#303030` in a dark terminal and `#E4E4E4` in a light one; a terminal of sixteen colours has no such ground. The accent is Claude's clay shifted toward red; the error is pinker than the accent, so that the two are told apart. The palette is provisional: the brand colours are a later design decision. There is no colour when `NO_COLOR` is set or the output is not a terminal.

**Which value is used.** The terminal says what it can show; toobusy does not ask it for its background. A terminal with truecolor (`COLORTERM` is `truecolor` or `24bit`) gets the dark values, unless `COLORFGBG` names a light background (its last field is `7` or `15`), and then the light ones. A terminal without truecolor gets the nearest of the sixteen colours: red for the accent, bright magenta for the error, green, yellow, bright black for muted and white for quiet; the chosen tab is shown reversed. Dark is the default; a light terminal that does not say so gets the dark values, and some of them, the warning above all, are hard to read on it.

### `toobusy init`

`init` asks what it needs, shows what it is about to do, and does it when the user saves: it writes `.toobusy/settings.toml`, makes a GitHub project when the user asked for a new one, links the project of the tasks to the repository when it is not linked yet, and makes the two labels a run puts on tasks when the repository does not have them. Until then it changes nothing, and it never touches the working copy or git.

The steps, in order:

1. **Environment.** The checks of [`doctor`](#toobusy-doctor) that need no settings: whether `git`, `gh` and `claude` are installed and whether `gh` and Claude Code are logged in; and whether the `origin` remote is a GitHub repository. Failed checks are shown with their fixes and do not stop the setup; when all pass, nothing is shown and the setup starts with the first question. Without a `gh` that is logged in, or when the `origin` remote is not a GitHub repository, nothing is verified, and the setup says so: the labels are typed and not chosen, a board is taken by its address without being checked, nothing is linked and no label is made. No board is made there either: the question about the board has no `New project` tab, and `--new-board` cannot be taken. With a `gh` that is logged in the boards of the user are listed all the same.
2. **Tracker.** GitHub is the only one; it is shown, not asked.
3. **Project.** The GitHub Projects board of the tasks, optional. A project that has one already, the board of the settings or, in a first setup, one that is linked to the repository, shows it as the first of two choices, as it stands in the list of the boards, with `Choose another project` under it; one Enter keeps it. That choice, and a project without a board, open four ways to answer, as tabs that Tab goes through: one of the boards the user can reach, the address of a board, a new board with its owner and title, or no board. Settings that exist and name no board open them on `No project`, so that Enter keeps the project without one; a board that is linked to the repository stands in the list of the boards all the same. The address of any page of a project is taken as its board. A chosen board is checked for access and for the token scope it needs. Its statuses are read then too, and the step may go on with three questions more, as **The statuses of the board** below tells.
4. **Blocking labels.** A multiple choice over the repository's labels; nothing is chosen at first. A label of the settings that the repository has no more stays among the choices, here and in the questions below. The marks of the labels of the settings that exist are in the colour of success, here and in the next question.
5. **Labels to take.** A multiple choice over the labels; nothing chosen means any task. A blocking label stands among them muted, with `blocking` after it, and cannot be chosen; so does a label that an earlier question holds in the questions below, with what holds it, as [the settings of the menu](#toobusy) show it.
6. **Manual label.** The label a run puts on a task that cannot go on without the owner: one of the labels of the repository that are not labels to take, a blocking one among them with `blocking` after it, or `New label…`, the last of the list, where a name is typed. A first setup proposes `manual`: the label of that name when the repository has it, a new one otherwise.
7. **Interrupt label.** The label a run puts on a task it had to stop, chosen in the same way among the labels that neither block nor let a task in and are not the owner's. A first setup proposes `interrupted`.
8. **Skill.** The skill of the project a session starts with, by its name, typed with the `/` of its command or without it; an empty answer is a project without a skill. A first setup proposes `task-auto`; settings that exist propose the skill they name, and none when they name none. A text that is not the name of a skill is refused. The skill is not made by the setup: it is a file of the project, which its owner or their assistant writes.
9. **Assistant.** Claude Code is the only one; it is shown, not asked.
10. **The last page.** What saving will do, in the words of the questions and not as the text of the file, and under it the question with three choices: `Save and exit`, `Exit without saving` and `Back`. A first setup shows the answers and says that they will be written. A setup that exists says what changes in it, each setting as it was and as it will be, a board by its title and its address: `Project: Rocket  https://github.com/orgs/acme/projects/1 → none`. A command around a task, the branch or the permission mode that an option sets, changes or takes away stands among them, `Before a task: none → scripts/dev.sh ports free`, `Branch: none → develop`, `Permission mode: auto by default → dontAsk`, and in a first setup under the line that says that the answers will be written. A permission mode the settings do not name is `auto by default` there, and not none: a session is started in `auto` then. Under it stand the things to be done on GitHub: a new board to make, a board to link to the repository, a label to make. A board that is not linked to the repository yet is linked; one that is linked is left alone, and no link of another board is ever taken away. A label of the two that the repository does not have is made: `The label “manual” will be made in acme/rocket.` A change of the statuses is one line among them, the three as they were and as they will be: `Statuses: Todo, In Progress, Done → Ready, Doing, Shipped`. A board that is chosen and has fewer than three statuses cannot be asked about; it is said as a warning, with the statuses the board has, and under it stands what to do: `! The board has no statuses “Todo”, “In Progress”: it has Open, Done.`, `` A run needs three statuses of a board: add them on GitHub and run `toobusy init` again. `toobusy doctor` fails until then. `` It does not hold the setup back either: the settings are written, and [`doctor`](#toobusy-doctor) fails over the statuses until the board has those the settings name. A new board has the three of GitHub, and nothing is said where nothing is verified or the statuses cannot be read. A skill that is named and is not written yet is said last, as a warning, with where it is to be written, and under it stands where to read what it is to say: ``! There is no skill “task-auto” yet: write .claude/skills/task-auto/SKILL.md before a run.``, `` `toobusy about` tells what the skill is to say. `` It does not hold the setup back: the settings are written, and [`doctor`](#toobusy-doctor) fails over the skill until it is there. `Exit without saving` does nothing and exits with code 1. On `Save and exit` the board is made, the board is linked, the labels are made and the file is written, in that order. When GitHub refuses any of it, the file is not written, and `init` says what was refused, naming the board when it was made already, and exits with code 1. When there is nothing to write and nothing to do on GitHub, `init` has no last page, says `Nothing to change: the settings already say this.` and exits with code 0.

**The statuses of the board.** The step of the project ends with which status of the board is which, as [The project and its settings](#the-project-and-its-settings) tells of them. A board that has the three names GitHub gives is asked nothing about, and neither is a new board, which has them, nor a project without a board. Another board is asked about in three questions, each a choice among the statuses of the board, in the order the board lists them:

| Question | What it says |
|---|---|
| `Todo status` | `A task is taken only from this status of the board.` |
| `In Progress status` | `A run moves a task to this status before its session.` |
| `Done status` | `A run moves a task to this status when it is done.` |

A status that is one of the three already is not among the choices of the next question. Each question proposes what was answered there before, else the status its option names, else the one the settings name, else the one GitHub names, when the board has it. Where the board has none of that name the proposal goes by the place of the statuses on the board, which lists them in the order a task goes through them: the first one for a task to do, the one after it for a task in progress, and the last one for a task that is done. The board is asked about as well when it has the names of GitHub and the settings or an option call a status otherwise, so that a board with a `Ready` beside its `Todo` can take its tasks from there. While the three are asked for, the board stands among the answers over the question; once they are answered and are not the names of GitHub, they are an answer of their own, `Statuses         Ready, Doing, Shipped`. Escape goes back from a status to the one before, and from the first of them to the question about the board; a board that was asked about is asked about again when the user comes back to it. The names are written to the settings as the board writes them.

Where nothing is verified the statuses of a board cannot be read, so the three are typed: each question proposes the name of its option, of the settings or of GitHub, says `Type what the board calls it.` after its line, and refuses an empty name and the name of one of the three before it. A board whose statuses cannot be read, where the rest is verified, is asked nothing about: it keeps the names that are proposed, and `doctor` says what is wrong with it. A board with fewer than three statuses is asked nothing about either: the last page says it.

**Going back.** Escape goes back from every question to the one before, which proposes what was answered there. Escape from the first question leaves the setup with nothing changed and exit code 1, as leaving with Ctrl+C does, and it is asked about in the same way: the keys say `esc exit` there, the first Escape gives `press esc again to exit`, and the second one leaves.

After writing, `init` says that the file is to be committed.

**An existing setup.** When the settings exist, `init` runs the same steps with the current values proposed, so pressing Enter through it changes nothing, but for one thing:

- The labels are written in the order and the spelling the repository lists them in. A list of the settings that stands in another order is shown as a change, and is written in the order of the repository.

A settings file that does not validate is reported with its errors, and nothing of it is proposed: the questions propose what a first setup does.

**Without questions.** Every answer has an option, and `--yes` accepts the proposed value of every question the options leave open, the confirmation included:

| Option | Answer |
|---|---|
| `--board <URL>` | the board, by its address or the address of any page of it |
| `--new-board <title>` | a new board with this title |
| `--board-owner <login>` | whose the new board will be: the user or one of their organisations; without it, the owner of the repository when a board can be made there, and otherwise the first GitHub names |
| `--no-board` | no board |
| `--todo-status <name>` | what the board calls the status a task is taken from |
| `--in-progress-status <name>` | what the board calls the status a run moves a task to before its session |
| `--done-status <name>` | what the board calls the status a run moves a task to when it is done |
| `--blocking-label <name>` | a blocking label; repeatable, and several names can be given at once, separated by commas |
| `--no-blocking-labels` | no blocking label |
| `--take-label <name>` | a label to take; repeatable, with commas in the same way |
| `--no-take-labels` | no label to take: any task |
| `--manual-label <name>` | the manual label |
| `--interrupt-label <name>` | the interrupt label |
| `--skill <name>` | the skill a session starts with |
| `--no-skill` | no skill |
| `--before <command>` | the command a run runs before a task is taken |
| `--no-before` | no command before a task |
| `--after <command>` | the command a run runs after the session of a task |
| `--no-after` | no command after a task |
| `--branch <name>` | the branch the tasks are done on |
| `--no-branch` | no branch: the tasks are done on whatever is checked out |
| `--permission-mode <mode>` | the permission mode a session works in, `auto` or `dontAsk`, whatever the case of its letters; it is written as Claude Code writes it |
| `--yes`, `-y` | ask nothing |

An option that names labels names all of them: `--blocking-label manual` over settings that block two labels leaves one. Options that answer one question differently, such as `--board` with `--no-board`, `--take-label` with `--no-take-labels`, `--skill` with `--no-skill`, `--before` with `--no-before` or `--branch` with `--no-branch`, `--board-owner` without `--new-board`, and an option of a status with `--no-board`, are a wrong command line: exit code 2.

`toobusy init --help` says in short what the list of the options does not show. The description of `init` says that it changes a setup that exists as well, and that `--before`, `--after`, `--branch` and `--permission-mode` are not asked but given as options; the one of `--yes` says that every other setting stays as it is; and after the options stand two examples, each a line that says what it does over the command:

```
Examples:
  Set the command before a task; the other settings stay as they are:
    toobusy init --before "scripts/dev.sh ports free" -y
  Take the command after a task away:
    toobusy init --no-after -y
```

Without `--yes` the options are the proposed answers of the interactive setup, before those of the settings that exist: the board of `--board` is the one Enter keeps, `--new-board` and `--no-board` open the question on their tab, the status of an option is the one its question points at, and the three questions are asked whatever the board calls its statuses, the labels are marked or pointed at, the name of the skill stands in its line, which `--no-skill` leaves empty. The commands around a task, the branch and the permission mode have no question: what their options say stands on the last page among the things that change, and is written on `Save and exit`. An option that cannot be taken, a text that is not the address of a board, a new board where nothing is verified, an owner a board cannot be made for, a status the board does not have, the same status named twice, a status named for a project without a board or otherwise than GitHub does for a new one, a label the repository does not have, a command that is empty, a branch whose name git does not take, a mode a session cannot be started in, is said as a warning over the questions. A board or an owner that cannot be taken leaves its question as if the option were not given; of the labels an option names, those the repository has are proposed, and the others are left out; a command that is empty, a branch that cannot be one and a mode that cannot be taken leave the setting as the settings have it.

With `--yes` there is no screen, with a terminal or without one. Every question gets what it proposes: the option, else the value of the settings that exist, else what a first setup proposes, where the board is the one linked to the repository or none, no label blocks, any task is taken, the two labels are `manual` and `interrupted`, and the skill is `task-auto`. The statuses of a board are those of the options, else those of the settings that exist, else those GitHub names: nobody is there to say which status is which, so none is chosen by its place on the board. The commands around a task, the branch and the permission mode are those of the options, else those of the settings that exist, and a first setup has none: its sessions work in `auto`. Settings without a board keep the project without one: the board that is linked to the repository is taken by a first setup only. Then the same things are done as on `Save and exit`: a new board is made, the board is linked, the labels are made, the file is written. What comes out is plain lines: the name of the command and the folder, what is wrong with the machine as the setup says it, that the board lacks a status the settings will name, `! The board has no status “In Progress”: it has Backlog, Ready, Done.`, with what to do about it, `` Say which of its statuses is which with `--todo-status`, `--in-progress-status` and `--done-status`, or with `toobusy init`: `toobusy doctor` fails until then. ``, and that the skill is not written yet, the answers as in the report, a command around a task, the branch or the permission mode that an option set, changed or took away, as it was and as it is now, `✔ Before a task    none → scripts/dev.sh ports free`, `✔ Branch           none → develop`, `✔ Permission mode  auto by default → dontAsk`, a line for each thing that was done on GitHub, `The label “manual” was made in acme/rocket.`, and how the setup ended. Settings that already say all of it end with `Nothing to change: the settings already say this.` and exit code 0.

With `--yes` a value that fails the check of its question is an error with exit code 1, and nothing is made, linked or written. The last line names the question and says why: `✘ Labels to take: acme/rocket has no label “nice”. The settings were not written.` The checks are those of the questions:

- a board whose address is not one, or that cannot be reached; a new board without a title, where nothing is verified, or for an owner it cannot be made for. A board that lacks a status no option names, one of the settings or of GitHub, is no error: it is said, and the settings are written;
- a status of an option that the board does not have, `✘ Done status: The board has no status “Closed”: it has Backlog, Ready, Doing, Shipped. The settings were not written.`, that is empty, or that is one of the three already, whether another option names that one or the settings or GitHub do: `✘ In Progress status: ready is the Todo status too.` A status that is named for a new board otherwise than GitHub names it is an error too, as the board is made with the names of GitHub. An option names a status whatever the case of its letters, and it is written as the board writes it;
- a blocking label or a label to take that the repository does not have, and a label to take that is a blocking label, whether an option names it or the settings do;
- a manual label that is a label to take, and an interrupt label that is a label to take, a blocking label or the manual label. These two need not be in the repository: one that is not there is made, as the interactive setup makes it;
- a skill whose name is not the name of one. The skill need not be written yet: one that is not there is said, and the settings are written;
- a command that is empty or all spaces, as the settings refuse it. The last line names the option: `✘ --before: A command must not be empty: give --no-before to run none. The settings were not written.`
- a branch that is empty, or whose name git does not take, as the settings refuse it: ``✘ --branch: “my branch” must be the name of a branch as git takes it, as `develop`: …. The settings were not written.`` Whether the repository has the branch is not looked at.
- a permission mode a session cannot be started in, as the settings refuse it: ``✘ --permission-mode: “plan” must be `auto` or `dontAsk`: in `plan` a session waits for an answer, and nobody answers a session of a run. The settings were not written.``

Settings that exist and do not validate are an error too: their errors are printed, and the file stays as it is, because nobody is there to see a first setup written in its place. What GitHub refuses on saving fails as it does in the interactive setup, after the lines of what was done before it. Where nothing is verified, without a `gh` that is logged in or a GitHub `origin`, `--yes` does what the interactive setup does: it says that nothing is verified, takes the options as they are typed, checks the labels against each other only and the statuses too, links nothing, makes no label, and writes the file. A new board is not a typed value, and none is made where nothing is verified: `--new-board` is an error there, and nothing is written.

Without a terminal and without `--yes`, `init` changes nothing and fails with exit code 1:

```
toobusy: `init` asks questions and needs a terminal.
Run `toobusy init --yes` to ask nothing: it takes what the options say and what the setup proposes. `toobusy init --help` lists the options.
An assistant that sets a project up for its owner starts with `toobusy init --assist`.
```

**A demo.** `--demo` is an option of every command: it shows the tool over made-up data and changes nothing. What it reads is real where reading changes nothing: the `origin` remote and, through `gh`, the boards of the user and the open milestones of the repository. When there are fewer than ten boards, made-up ones follow the real ones, so that there is a list to scroll; the owners a new project can be made for and the milestones are filled up to ten in the same way. The rest is imitated: every other made-up board calls its statuses `Backlog`, `Ready`, `Doing`, `Review` and `Shipped`, so that the questions about them can be tried, and every other board of a demo has the three of GitHub; the labels are made up, the project has whatever skill it names, the settings that exist are read, nothing is made, linked or written, with `--yes` as well as on the screen, and the settings, the milestone, the model and the effort that are chosen are remembered only until the demo ends. In a folder whose `origin` remote is not a GitHub repository the setup of a demo says, as the real one does, that nothing is verified, and its labels are typed. `toobusy --demo` and `run --demo` show a project that is ready, whatever the project is: where there are no settings they are made up, the first of the milestones is the chosen one, the model is the assistant's own and the effort is `high`, so that the menu opens at once; the setup and the choices are tried from the menu, or with `init --demo`, `milestone --demo`, `model --demo` and `effort --demo`, where nothing is made up for them: the settings are those the project has, when it has any, and nothing is chosen yet. Without a terminal `toobusy --demo` is no different from `toobusy`: it prints the help or the "not set up" message. A run of a demo is the real run over made-up tasks and made-up sessions, described with [the page of a run](#a-demo-of-a-run). The bar of the screen says `demo` after what it says at its right: `Setting up this project · demo`, and `v1.0.0 · demo` over the menu. It is there to try the tool and to see what it looks like.

**The questions.** Every question has the shape the screen gives it: its name and a line that says what it is about and what to do, then the choices or the text, then the keys it understands. A refused answer stays in its question, and the reason takes the place of the line under the name; in the question about the project it takes the place of the line that explains the tab, and a board that cannot be reached is said over the question.

- **`Back`.** Wherever Escape goes back and does not leave the page, a list ends with a row `Back` that does what Escape does: in the lists the menu opens, the settings among them, and in the questions they open, in a selection, a multiple choice, a confirmation and the lists of the boards. Where Escape would leave the page there is no `Back`: in the menu, which has `Exit`, in the first question of `init`, which is the one about the project and not those about its statuses, on the first run and in `toobusy milestone`, `model` and `effort`. A page that waits for the tracker has it under its dots. A question that is answered with a text has no list to end with it, and a list of the boards that a filter has emptied has none either; Escape goes back from them. The row names its key at its right, `Back  esc`, and the line of the keys does not say `esc back` then: it says so only where there is no such row. Where Escape leaves the page, the keys go on saying `esc exit`, except in the menu, whose `Exit` row names the key at its right in the same way: `Exit  esc`.
- **`current`.** In a list where one choice is made, the one that the settings or the choices of the user have now is marked `current`, in the colour of success, wherever the pointer goes: in the questions of a setup over settings that exist, those of the menu among them, the label, the permission mode, the statuses of the board of the settings and the board itself, and in the lists of the milestone, the model and the effort, where it marks `Other…` for a model of another name. The mark stands right after the name, in a column of its own that the other rows keep empty, so that what explains the names stands in one column after it, and `Back` names its key in that column:

  ```
    auto     current  a model of Claude Code looks at every action before it is done
  ❯ dontAsk           only what the settings of Claude Code in the project allow is done
    Back     esc
  ```

  Where nothing is chosen yet, as in a first setup, nothing is marked, and the pointer starts on what is proposed all the same; a status of another board than that of the settings is not marked either. A multiple choice shows what is chosen by its marks, and a text has no list.
- A selection moves with Up and Down, or with `k` and `j`, a multiple choice marks with the space bar, and the last question of a setup is `Save and exit` over `Exit without saving`. A list that does not fit shows eight rows, five for the boards, and says above and below how many rows are beyond them: `↑ 2 more`, `↓ 4 more`.
- A text is edited where the cursor is: Left, Right, Home and End move it, Backspace and Delete remove a character, and Ctrl+A, Ctrl+E, Ctrl+U and Ctrl+W do what they do in a shell. A pasted text is drawn once.

The question about the project, when the ways to answer are open:

```
 Project
 The GitHub project of the tasks: their statuses are kept on its board.
 ────────────────────────────────────────────────────
  Your projects   By URL   New project   No project   press tab to switch

 Filter  too
 ❯ Toobusy  https://github.com/orgs/bitpatch/projects/4  linked
   Back  esc
 ────────────────────────────────────────────────────
 type to filter · ↑↓ move · enter choose · ctrl+c exit
```

Every tab stands on the grey of the bar, its name a little quieter than a plain text, and the chosen one is white on the deeper accent; without colours the chosen one is in brackets. The place under the tabs is as tall as the tallest of the tabs, whatever tab is open, so that nothing moves when the tab changes: a list starts at its top, and the line where a text is typed, with what is said about it, stands at its bottom, with empty lines between.

- **Your projects** lists the open boards of the user and of their organisations, those linked to the repository first, each by its title with its address beside it. Typing narrows the list, the best fit first: the board of that address, a title that starts with the text, a title that has it inside, an address that has it, as the name of the owner is. The boards come from one request through `gh` with a timeout of three seconds, which is made once more without the repository when it fails, as it does over a repository that cannot be read; when `gh` is missing, not logged in, lacks the scope to read projects or is too slow, this tab is not there.
- **By URL** takes the address of a board or of any page of it.
- **New project** asks whose the board will be and for its title. The owners, the user and their organisations, are a list at the top of the tab, as the boards are in theirs: Up and Down move the pointer, which starts on the owner of the repository when a board can be made there, and on the user otherwise. The title is typed in the line at the bottom. The board is made only when the setup is confirmed. The tab is not there when `gh` gave no owners, or when the `origin` remote is not a GitHub repository: there is nothing to link a new board to.
- **No project** leaves the setting out.

Escape from these tabs comes back to those two choices when the project has a board, and goes back a step when it has none.

**The report.** When the screen is closed, the terminal gets the name of the command and the folder, the answers as `✔ Blocking labels  manual, draft` lines, under them a command around a task, the branch or the permission mode that an option set, changed or took away, as it was and as it is now, `✔ Before a task    none → scripts/dev.sh ports free`, and one line that says how the setup ended. After a setup that wrote the settings the last line names the command that tells how a run works: `` `toobusy about` tells how a run works, for you and for your assistant. ``

The setup is written against an interface for asking questions, so that tests answer them from a script and the terminal implementation can change without touching the steps.

#### With an assistant

The owner of a project has their assistant set toobusy up: “set toobusy up here: run `toobusy init --assist` and follow it”. The command tells the assistant how, one step at a time, so that an assistant in a project it knows, and that toobusy has never seen, fits the whole of toobusy to that project: the settings, the choices of the owner, the instructions of the project, its skill and its commands around a task. [`toobusy about`](#toobusy-about) stays what it is: it describes how a run works and asks for nothing. `init --assist` is the walk through the setup, and points at `about` instead of saying it again.

| Command | Prints |
|---|---|
| `toobusy init --assist` | the introduction, the list of the steps with how each of them stands, and the first step |
| `toobusy init --assist <n>` | step `n` alone |
| `toobusy init --assist <n>` after the last step | `All the steps are done: the setup is complete.` and nothing else; exit code 0 |

- Every step ends with the next one: ``Next: `toobusy init --assist 3` ``. The last step ends so too, and the number after it gives that one line.
- A number that is not 1 or more, or not a number, is a wrong command line, and so is `--assist` with any other option of `init`: exit code 2.
- It changes nothing and keeps nothing: there is no record of the steps that are done. A step can be asked for again at any time, and it tells how things stand then, so the same walk sets a project up for the first time and changes a setup that exists.
- It needs a git working copy, as `init` does, and no terminal: in one it prints the same and opens no screen. It prints Markdown, in English, to the output, and exits with 0.
- `--demo` changes nothing of it: it changes nothing as it is.

**What it reads.** The settings: missing, valid, or not valid, and then their errors stand at the head of every step, and over the list of the introduction, with that the file is put right by hand first. The choices of the user. Whether the skill of the settings is there. The sign of a run: while a run goes on in the working copy, the introduction says so, and that the setup waits for it. The `origin` remote. And from the tracker, for the steps that choose among what it has: for step 2 the labels of the repository, for step 3 the boards linked to it and the others the user can reach, each with its statuses, and for step 4 the open milestones. These are the readings of `init` and of `doctor`, with their timeouts; the statuses of the boards are asked a few boards at a time, and the board of the settings stands among the boards when the user does not reach it. What cannot be read is said, with the reason and the step that puts it right, and the step is told without it: `gh` is not installed or not logged in, which is asked before anything is read, the `origin` remote is not a GitHub repository, or GitHub did not answer. The introduction and the other steps ask nothing of the tracker.

**The introduction.** A few lines on what toobusy is and what a setup is made of: the settings of the project in `.toobusy/settings.toml`, which are committed; the choices of the owner, which are kept on their machine; and what is the project's own, its instructions, its skill and its commands. Then the rules of the walk:

- Go step by step, in order. A step whose “As things are” already says what the project needs is passed.
- Ask the owner whatever you do not know, and whatever a step says is theirs to decide.
- Set things with the commands the steps name, and do not write `.toobusy/settings.toml` by hand; a file that does not validate is the exception.
- Start no run: a run commits, pushes and closes tasks. Commit nothing of the setup before the owner agrees, where the instructions of the project say so.
- The steps are written in English; speak to the owner in their language.
- The walk changes nothing and has no record of the steps that are done.

Then the list of the steps, each with a few words on how it stands, from what is read without the tracker: `Not set: there are no settings yet`, the labels, the board, `Milestone: not chosen`, the name of the skill and whether it is written, the branch and the commands around a task, and the permission mode.

**A step.** Every step has the same parts, under the same names and in this order, and those that have nothing to say are left out:

- **As things are**: what is set already and what the project has to choose from.
- **Decide**: what is decided here, and what each answer means for a run.
- **Look at**: where the answer is likely to be found in the project.
- **Ask the owner**: what is theirs to decide or to do.
- **Set it**: the command, written out with the options of this step, on a line of its own.
- **Check**: how to see that it is set as meant.
- **Next**: the next step.

```text
## Step 2 of 9: Which tasks are taken

As things are: not set: there are no settings yet. The labels of acme/rocket: `bug`, `feature`, `docs`, `question`, `wontfix`.

Decide: …

Look at: `CONTRIBUTING.md`, `AGENTS.md`, `CLAUDE.md`, the issue templates, and how the open tasks are labelled: `gh issue list --limit 50`.

Ask the owner: show the four answers before you set them, and say that the last two labels are made in the repository when it does not have them: everyone who works there sees them.

Set it:

    toobusy init --yes --no-board --no-skill --take-label <label>,<label> --blocking-label <label>,<label> --manual-label manual --interrupt-label interrupted

… `--no-board` and `--no-skill` stand there only because nothing is set yet: steps 3 and 6 set them.

Check: the command ends with `The settings are written to .toobusy/settings.toml.`, or with `Nothing to change`, and `toobusy doctor` passes `settings` and `labels`.

Next: `toobusy init --assist 3`
```

What the assistant puts in stands in angle brackets. Over settings that exist a command names what is set now, as `--take-label feature,bug`, and a value with a space in it stands in quotes. In a project without settings every command of a step that writes them names, beside its own options, those that keep a first setup from proposing a board or a skill, `--no-board` and `--no-skill`, and the step says why they stand there.

**The steps.**

1. **The machine.** `toobusy doctor`: `git`, `gh` and Claude Code, their logins, and an `origin` remote that is a GitHub repository. What only the owner can do is asked of them: the logins, the `project` scope of the token, and running `claude` once in the folder so that it is trusted. In a project without settings the step says that the check of the settings fails until the next step.
2. **Which tasks are taken.** The four settings of the labels: the labels to take, the blocking ones, the manual label and the label of interrupted tasks, with what each does to a run and which of them must not share a label. It is the first step that writes, and it comes before the board because the first writing of the settings makes the two labels of a run in the repository: they are decided first. The labels of the repository stand there, and those the settings name and the repository does not have. In a project without settings the command says `--no-board --no-skill`; over settings that exist it names the options of the labels alone, with the labels as they are set. The owner sees the four answers before they are set.
3. **The board.** One of the boards the user can reach, a new one, or none, and what a board means: a task is taken only from one status, a task that is not on the board is held, and the token needs the `project` scope. It says what the settings call the statuses of the board they name, and that a run knows the three by those names. The boards stand there with their statuses, and one is marked when it has fewer than three, when it is the board of the settings and lacks a status they name, and when it is another board that is not named as GitHub names one. The owner is always asked: a board is seen by everyone, a new one is made on GitHub, and which status is which is theirs to say. `toobusy init --yes --board <URL>`, the same with `--todo-status <name> --in-progress-status <name> --done-status <name>`, `--new-board "<title>"` with `--board-owner <login>`, or `--no-board`. Whether the open tasks are put on the board is the owner's to decide.
4. **The owner's own choices.** The milestone to work on, the model and the effort: `toobusy milestone`, `toobusy model`, `toobusy effort`. The open milestones stand there with their open tasks, and a chosen one that is not open any more is said. All three are asked of the owner, and the step says that they are kept on the machine and not committed, so everyone who clones the project chooses their own. Then `toobusy queue`: the owner is shown which tasks a run would take, in which order, and which are held and why; when that is not what they expect, the walk goes back to step 2 or 3.
5. **The instructions of the project.** `toobusy about` is read first. The instructions of the project, `CLAUDE.md`, `AGENTS.md`, its skills and the like, are compared with what holds over them in a session, and get what a project with toobusy needs: that an assistant that works with the owner looks at `toobusy status` before it commits, that in a session of a run the task is kept by toobusy, where they tell an assistant to move, label or close its task, and how a session brings its branch up to date when the branch has moved on the remote by the time it pushes, by a rebase or by a merge. Where they describe another way of running the tasks of the project without its owner, a script of the project, the sessions it started, the line it read at the end of a reply, the labels and the statuses it set, they are changed to describe toobusy, or taken out. Such a way is looked for in a script of the project that starts sessions of the assistant, a section of its process on it, a folder of its state, and tasks of the tracker that are about it. The owner is asked what a session may do alone in this project; a kind of task that it must not do gets a blocking label, back in step 2. Where there is an old way, the owner is asked whether the script, its state and its instructions are taken out of the project, and what becomes of the tasks about it: nothing is deleted without them. No command of toobusy sets this step: the changes are shown to the owner. The step names no script and no tool: it speaks of a script of the project and of another tool.
6. **The skill a session starts with.** Whether the project needs one: it does when work without the owner differs from work with them. What the skill says and what it leaves to the first message, as `about` tells; where it is written, `.claude/skills/<name>/SKILL.md`, and what the file needs to be loaded by its command: a front matter with the name of its folder and a description. The step says whether the skill the settings name is written, and where. A skill that is written may be older than toobusy, one that the sessions of a script of the project or of another tool started with, so the step tells to read it against `about`, under “A session starts with the skill of the project”, and to change it: what the first message says already is taken out, that nobody answers, that the task is read in the tracker and kept by toobusy, the working copy and the plan, what becomes of what cannot be done without the owner and how the last reply ends; what says against the first message is taken out or changed, a line a session ends its reply with for another tool, commands that move, assign, label, comment on or close its own task, asking the owner, a worktree; what is the project's stays, how the work is done and checked without the owner, what is done before and after it but for what the run does itself with the branch and the commands, the language of the report and the other tasks a session makes; and where the skill names the old tool, its script, its files or its commands, it names toobusy and its commands instead, `toobusy queue` and `toobusy status`, or leaves them out. The owner is shown the skill as it will be, with what is taken out and why, and the check is that it says nothing that `about` lists under what the first message says already, and nothing against it. A skill that is not written yet, one of a plugin and none keep the step without this. Then `toobusy init --yes --skill <name>`, or `--no-skill` for a project that needs none. `toobusy doctor` checks that the skill is there, and `toobusy about` shows the first message with it.
7. **The branch and the commands around a task.** What the run itself does around every task, whatever its session does. The branch the tasks are done on: with one named, the working copy is checked to be on it and brought up to date with its upstream before a task; without one, a run does the tasks on whatever is checked out. The commands: what is to be so around every task, free ports, services that are up, and what is stopped afterwards; the branch and its sync with the remote are not for a command, as the branch does them. The rules of a command: a line for the shell at the root of the working copy, run after the branch is brought up to date, exit code 0, its time, the task in its environment, no input, and a working tree that is clean after it. The owner is asked which branch the tasks are done on. `toobusy init --yes --branch <name> --before "<command>" --after "<command>"`, or `--no-branch`, `--no-before` and `--no-after`. The check is `git status --short --branch`, which shows the branch and the upstream it follows, and the command run by hand once.
8. **What a session may do.** A session is a background one, and nobody answers its prompts: what it is refused is not done. The step says the permission mode the settings name, or that a session is started in `auto` as they name none, and what the two modes of [the setting](#the-project-and-its-settings) check, `assistant.permission-mode`; that a mode that asks or waits for a plan is not taken, nor one that checks nothing. The settings of Claude Code in the project are looked at: what every task needs must be allowed there, the build, the tests, `git` and `gh` among it, and in `dontAsk` nothing else is done; hooks and servers that wait for a person do not work. What a session is allowed and refused in particular stays there. The owner is asked which mode a session works in, and before anything more is allowed. `toobusy init --yes --permission-mode <mode>` sets the mode; the settings of Claude Code are edited as any file of the project. The check is that `toobusy about` names the mode.
9. **The last check.** `toobusy doctor` passes every check. The step names what is to be committed: `.toobusy/settings.toml`, the skill, the instructions and the settings of Claude Code that were changed, and what was taken out of the project in steps 5 and 6, the script of the old way of running the tasks, its state and its instructions, where the owner agreed to it, as the instructions of the project say a commit is made. The assistant tells the owner in a few lines what was set, and that the setup is complete. The step proposes nothing more: no demo, no first run, no next thing to try.

A step tells what the tool does at the time. A later change of what can be set changes its step in the same commit.

**Where an assistant finds it.** So that an assistant finds the command without being told of it, one sentence, `` An assistant that sets a project up for its owner starts with `toobusy init --assist`. ``, stands in the description of `toobusy` in `--help` and in the description of `init`, where `--assist` is among the options; as the last line of two messages where there is no terminal, `` `init` asks questions and needs a terminal `` and the message of [a project that is not set up](#not-set-up); and `toobusy about` names the command at its head, for the assistant that reads `about` first. An assistant does not take the output of a tool for an order, so it is the owner who tells it to follow the steps.

The steps are a function of what was read that gives the text, with nothing of the terminal in it.

### `toobusy`

In a terminal `toobusy` without a command opens its page and goes on from wherever the project is:

1. **A project that is not set up** is set up: the steps of `init`, on the same page. Leaving them, or declining at the end, leaves the page.
2. **A user without a milestone to work on**, one who has not chosen or whose milestone is not open any more, chooses one from the list. Leaving the list leaves the page.
3. **A user who has not chosen the model or the effort** chooses them, the model first. Leaving either list leaves the page.
4. **The menu.** Above it stand the settings, with which status of the board is which in a project with a board, as the settings of the menu tell it, the milestone, the model and the effort as they are, and the labels to skip when some are chosen; the milestone stands again, in the colour of success, after the choice that changes it. The bar of the page says the version of the tool at its right, as [the screen](#the-screen) describes.

```
  toobusy · ~/Projects/toobusy                         v1.0.0

 ✔ Project          https://github.com/orgs/bitpatch/projects/4
 ✔ Statuses         Todo: Todo · In Progress: In Progress · Done: Done
 ✔ Blocking labels  manual, draft
 ✔ Labels to take   any task
 ✔ Manual label     manual
 ✔ Interrupt label  interrupted
 ✔ Skill            task-auto
 ✔ Milestone        v0.3.0 · due 2030-01-15 · 12 open tasks
 ✔ Model            opus
 ✔ Effort           high

 What to do
 ────────────────────────────────────────────────────────────
 ❯ Run          5 tasks
   Assistant    opus · high
   Milestone    v0.3.0
   Settings
   Skip labels  none
   Exit         esc
 ────────────────────────────────────────────────────────────
 ↑↓ move · enter choose · ctrl+c exit
```

| Choice | Opens |
|---|---|
| `Run` | the run, on its tape; when it is over the menu is back by itself, with how the run went said above it. It is not opened while a run goes on in the working copy: the menu says `A run is going on here already:` above itself, with the task of that run as [`toobusy status`](#toobusy-status) names it. The checks of [`doctor`](#toobusy-doctor) are made first, as `toobusy run` makes them, and the run is not opened while one fails |
| `Assistant` | the model and the effort, described below; Escape goes back to the menu |
| `Milestone` | the list of the milestones; Escape goes back to the menu with the milestone as it was |
| `Settings` | the settings of the project with their values, described below: each opens its questions, and a change is saved when the user says so; Escape goes back to the menu |
| `Skip labels` | the labels whose tasks the runs of the menu leave out, described below; Escape goes back to the menu with the choice as it was |
| `Exit` | nothing: it leaves the page, as Escape twice and Ctrl+C twice do |

After `Run` stands the number of tasks a run would take as things are: those that are ready and those that open after them. It is counted when the menu opens, and again after a run, after a change of the milestone, after the settings and after a choice of the labels to skip. While the tracker is read, the dots of a wait run in its place, the row is muted, and Enter does nothing on it. A run that would take no task is not opened either: the row says `no tasks`, and Enter counts again, for what has changed meanwhile. When the tasks cannot be read the row says so, `the tasks cannot be read`, and the run is opened all the same: it says why it stops.

Before `Run` opens the run it makes every check of [`doctor`](#toobusy-doctor). The menu stays while they are made: the check that is running stands at `Run`, as `checking: GitHub login`, before the dots of a wait, the row is muted and Enter does nothing on it. The other rows are chosen as ever, and choosing one stops the checks. When every check has passed the run opens by itself. When one has failed no run is opened: the menu stays, and above it stand the failures with their fixes, as `toobusy run` prints them, a line that does not fit the window going on to the next one, and under them ``The run did not start. `toobusy doctor` checks all of it again.`` So where the `origin` remote is not a GitHub repository there is no run to open: the check of the repository says so. A demo has it all and checks nothing.

When the task that a run would take first is one that an earlier run had to stop, a task with the label of interrupted tasks, the row says so instead of the number, in the colour of a warning: `Run  interrupted: #71 Paint the windows yellow`. It is the task the next run goes on with, and Enter opens that run as it does on a count; the number of the tasks comes back when no task is interrupted any more. A title that does not fit is cut, and the number is not. A task that is interrupted and held, or that waits for another one, is not taken first, so the row does not name it. The row names in the same way the task that an earlier run left with a session, the task of [the record](#going-on-with-a-session): a run goes on with it before any other, whatever its status.

After `Assistant` stand the model and the effort that are chosen; the assistant's own model is `own model` there. It opens a list of the two choices of the user, each with what is chosen after its name:

```
 Assistant
 ────────────────────────────────────────────────────────────
 ❯ Model   opus
   Effort  high
   Back    esc
 ────────────────────────────────────────────────────────────
 ↑↓ move · enter choose · ctrl+c exit
```

| Choice | Opens |
|---|---|
| `Model` | the list of the models; Escape goes back with the model as it was |
| `Effort` | the list of the levels; Escape goes back with the effort as it was |

These lists end with `Back`, as every list does that there is somewhere to go back from.

After `Settings` stands nothing. It opens the settings of the project one under another, each with its value after its name, all of them at once: the list does not scroll.

```
 Settings
 Choose a setting to change it: a change is saved when you say so.
 ────────────────────────────────────────────────────────────
 ❯ Project          https://github.com/orgs/bitpatch/projects/4
   Statuses         Todo: Todo · In Progress: In Progress · Done: Done
   Blocking labels  manual, draft
   Labels to take   any task
   Manual label     manual
   Interrupt label  interrupted
   Skill            task-auto
   Before a task    none
   After a task     none
   Branch           develop
   Permission mode  auto by default
   Back             esc
 ────────────────────────────────────────────────────────────
 ↑↓ move · enter choose · ctrl+c exit
```

Under the project, in a project with a board, stand its statuses: which status of the board is which of the three a run knows, each of them by the name GitHub gives it and after it what the board calls it, as `Todo: Ready · In Progress: Doing · Done: Shipped`. A value stands in the colour of success, as what is chosen does in every list; the blocking labels stand in the colour of an error, as they keep tasks out of a run; and a value that says nothing is set, `none`, is muted. A board is named by its address, and by its title too once the boards are read. Over the list stand what the setup says of the machine, as `init` says it, and how the last change went. The bar says `Changing the settings`, and there are no steps at the foot. The pointer stays on the setting that was opened last.

Enter on a setting opens its questions with what the settings say proposed. The project, with the statuses of its board, the blocking labels, the labels to take, the manual label, the interrupt label and the skill are asked as the steps of `init` ask them; the boards are read the first time the project is opened, and the labels the first time a label is. `Statuses` asks the three questions about the statuses of the board, under the board, whatever the board calls them, and the board is not asked for again: a board that has `Todo`, `In Progress` and `Done` and a `Ready` beside them can take its tasks from there. A board with fewer than three statuses, or whose statuses cannot be read, is asked nothing, and that is said above the list. The four that `init` asks no question for are asked here:

| Setting | Question |
|---|---|
| `Before a task` | a text: `A line for the shell that a run runs before it takes a task. Leave empty to run none.` |
| `After a task` | a text: `A line for the shell that a run runs after the session of a task, however it went. Leave empty to run none.` |
| `Branch` | a text: `The branch the tasks are done on. Leave empty to do them on whatever is checked out.` A name that git does not take is refused as the settings refuse it |
| `Permission mode` | a choice between `auto` and `dontAsk`, each with what it checks. `auto` over settings that name no mode leaves them naming none |

A setting is left when its last question is answered, and when Escape or `Back` goes back from its first question: then what is chosen or typed there counts as the answer, as Enter would give it, but for a text that its question refuses, which is left as it was. Escape from a later question, as one about the statuses of a board or the name of a new label, goes back to the question before it. A setting that is left as the settings have it comes back to the list and asks nothing. One that is left changed shows what saving will do, as the last page of `init` does, and asks:

```
 This will change in .toobusy/settings.toml:
   → Blocking labels: manual → manual, draft

 Save the change?
 ────────────────────────────────────────────────────────────
 ❯ Save
   Don't save
   Back  esc
```

`Save` does what `Save and exit` of `init` does, for this setting alone: a new board is made and linked, a board that is chosen is linked when it is not yet, a label of a run that is named here and that the repository lacks is made, and the file is written. Then the list is back with how it went above it: ``The settings are written to .toobusy/settings.toml. Commit the file.``, or what GitHub refused and that the settings were not written. `Don't save` comes back to the list with `The change was not saved.`, and `Back` to the questions of the setting, with what was answered proposed; leaving them then, with Escape or `Back`, leaves them with that answer, and the change is asked about again. Leaving the list goes back to the menu.

What one setting holds, another cannot have, by the rules of the settings, and the list of the other says so. A label stands with its roles after it, `to take`, `blocking`, `manual label` and `interrupt label`; one that a role keeps out is muted all through and cannot be chosen, and the pointer stops on it without blinking:

| List | Muted, what holds it | Chosen as well, with its role |
|---|---|---|
| Blocking labels | `to take`, `interrupt label`, `manual label`: a task with the manual label is never taken anyway | — |
| Labels to take | `blocking`, `manual label`, `interrupt label` | — |
| Manual label | `to take`, `interrupt label` | `blocking` |
| Interrupt label | `to take`, `blocking`, `manual label` | — |

```
 Labels to take
 ────────────────────────────────────────────────────────────
   ◉ bug
   ◯ draft
 ❯ ◯ help wanted    blocking · manual label
   ◯ interrupted    interrupt label
   ◉ feature
```

The marks of the labels the settings have, `◉` and `◯` alike, are in the colour of success, also where the pointer blinks, so that what is saved is told from what is changed: a `◯` in that colour is a label that leaves the setting when it is saved, and a `◉` in the colour of any text one that comes into it. The manual label is not kept among the blocking labels, where it changes nothing: the list of the blocking labels does not offer it, and a label that is chosen as the manual label leaves them. Settings that name it among them, as an older setup may, lose it there when the blocking labels or the manual label are saved, and the change says so: `Blocking labels: help wanted, invalid → invalid`. Where nothing is verified and the labels are typed, such a label is refused with the reason.

Settings that do not validate have no list: `Settings` opens the steps of `init` then, which propose what a first setup does, and the end of the setup comes back to the menu with its last line above it.

After `Skip labels` stand the labels that are chosen, separated by commas, or `none`, muted. It opens a multiple choice over the labels of the repository, read as `init` reads them for its questions, under the dots of a wait with `Back` under them: those the repository has, and after them those of the settings that it lacks. Nothing is chosen at first, and a label that is chosen is chosen again when the list is opened once more; one that the repository has lost since stays among the choices, so that it can be left. Space chooses a label or leaves it, Enter on a label takes the choice, and the list ends with `Back`; Escape goes back with the choice as it was, and says so above the menu: `The labels to skip are left as they were.`

```
 Skip labels
 A run of this menu leaves their tasks out, until toobusy is closed.
 ────────────────────────────────────────────────────────────
 ❯ ◉ design
   ◯ feature
   ◯ manual
   Back  esc
 ────────────────────────────────────────────────────────────
 ↑↓ move · space select · enter confirm · ctrl+c exit
```

Where the labels cannot be read the list says why, in the colour of an error, and offers `Back` alone: `` The `origin` remote is not a GitHub repository, so there are no labels to choose from. ``, ``The GitHub command-line tool `gh` is not installed, so the labels cannot be read.`` or `` `gh` is not logged in to github.com, so the labels cannot be read. ``

While at least one label is chosen, `Skipped labels  design, sketch` stands above the menu after the effort, the tasks at `Run` are counted without what such a run leaves out, and `Run` opens a run that skips them, as [`run --skip-label`](#toobusy-run) does. The choice lasts while this `toobusy` is open and is kept nowhere, neither on the machine nor in the settings: the next `toobusy` starts with none, and the terminal is told nothing of it when the page is closed. A demo has the row too, over its made-up labels.

The answers of `Settings` are written to the settings of the project and are to be committed; the milestone, the model and the effort are kept on the machine of the user.

When the page is closed, the terminal gets the name of the tool and the folder, the report of a setup that was gone through or of the settings that were saved from the menu, each as it is now, and the milestone, the model and the effort when they were chosen. A run has left its tape in the terminal already, with its tasks and how they went; after a run with nothing else to report, nothing more is printed. A demo adds that what was chosen is not remembered and, after a run, that the tasks and the sessions were made up. The exit code is 0, and 1 when a setup failed on GitHub; after a run it is the exit code of the last one.

Without a terminal there is nobody to ask: `toobusy` prints its help in a project that is set up and the "not set up" message in one that is not.

### `toobusy milestone`

`milestone` chooses the milestone to work on, or shows the one that is chosen. It needs no settings: only a project.

| Command | Does |
|---|---|
| `toobusy milestone <title>` | chooses the open milestone with that title and prints it with its due date and its open tasks. The title is taken whatever the case of its letters, unless two milestones differ only in it |
| `toobusy milestone --none` | chooses to work without a milestone |
| `toobusy milestone` in a terminal | opens the list of the milestones on a page; Escape leaves the choice as it was |
| `toobusy milestone` without a terminal | prints the choice: `Milestone: v0.3.0`, `Milestone: no milestone` or `Milestone: not chosen`, and after a milestone that is not open any more, `(not open any more)`. Under a choice that nothing can be run with stands how to make one: ``Choose it with `toobusy milestone <title>` or `toobusy milestone --none`.`` |

A title that no open milestone has fails with exit code 1 and names the open ones, or says `The repository has no open milestones.` in their place:

```
toobusy: there is no open milestone “v0.1.0”.
The open ones: v0.2.0, v0.3.0
```

When the milestones cannot be read it fails with exit code 1 as well, and says that in one line: `toobusy: the milestones of acme/rocket cannot be read`, or, in a project whose `origin` remote is not a GitHub repository, that there are no milestones to choose from. A title together with `--none` is a wrong command line.

### `toobusy model` and `toobusy effort`

`model` and `effort` choose the model and the effort the tasks are done with by default, or show what is chosen. Like `milestone`, they need no settings: only a project.

| Command | Does |
|---|---|
| `toobusy model <name>` | chooses the model with that name, as it is written but for the spaces around it, and prints `Model: <name>`. A name that is empty or all spaces fails with exit code 1 |
| `toobusy model --default` | chooses the assistant's own model |
| `toobusy model` in a terminal | opens the list of the models on a page; Escape leaves the choice as it was |
| `toobusy model` without a terminal | prints the choice: `Model: opus`, `Model: the assistant's own` or `Model: not chosen` |
| `toobusy effort <level>` | chooses the level, whatever the case of its letters, and prints `Effort: <level>` |
| `toobusy effort` in a terminal | opens the list of the levels on a page; Escape leaves the choice as it was |
| `toobusy effort` without a terminal | prints the choice: `Effort: high` or `Effort: not chosen` |

Without a terminal a choice that is not made is followed by a line that says how to make it: ``Choose it with `toobusy model <name>` or `toobusy model --default`.`` and ``Choose it with `toobusy effort <level>`: low, medium, high, xhigh, max.``

A name together with `--default` is a wrong command line. A level that the assistant does not have fails with exit code 1 and names the levels:

```
toobusy: there is no effort “ultra”.
The levels: low, medium, high, xhigh, max
```

### `toobusy run`

`run` takes the tasks of the queue one after another, as [Running the tasks](#running-the-tasks) describes.

`run` does not start over settings that do not validate: it prints `toobusy: the settings of this project are not valid.` and the errors, a line each, and exits with code 1.

Nor does it start while a run goes on in the same working copy: a second one would work in the middle of the task of the first. It says so with the task of that run, as [`toobusy status`](#toobusy-status) names it, and exits with code 3, before anything is checked or asked of the tracker:

```
toobusy: a run is going on in this working copy already: #12 Export the orders as a file · claude attach 1a2b3c4d
The run did not start. `toobusy status` says whether that one still goes on.
```

Two runs that are started at the same moment may both come past this. The run itself looks once more when it puts up [its sign](#local-state): the one that finds the sign of the other says `✖ Did not start: a run is going on in this working copy already: …`, starts nothing and ends as a run that stopped because something was wrong.

Then come the checks of [`doctor`](#toobusy-doctor), and after them the choices of the user.

`run` does not start without a milestone to work on. When the user has not chosen one, or the chosen one is not open any more, it exits with code 1:

```
toobusy: the milestone to work on is not chosen.
Run `toobusy milestone` to choose it.
```

For a milestone that is not open any more the first line names it instead: `toobusy: the milestone “v0.2.0” is not open any more.`

Nor does it start before the model and the effort are chosen. The first that is missing is named, in the same way and with the same exit code:

```
toobusy: the model is not chosen.
Run `toobusy model` to choose it.
```

A project whose `origin` remote is not a GitHub repository has no tasks to take: `run` says so and exits with code 1.

In a terminal `run` shows the run on its tape, and leaves the tool when the run is over: no menu is opened, and no key is waited for. Without a terminal it prints the log of the run line by line, takes no commands, and stopping the tool kills the run.

Two options make it a run of less than the whole queue, as [One task, or a number of them](#one-task-or-a-number-of-them) describes:

| Option | Does |
|---|---|
| `--task <number>` | does the task with that number and no other. A task that a run would not take as things are is not started: the run says why and exits with code 1 |
| `--max <count>` | takes so many tasks at most, and then stops as a run that its owner stopped |

The two together are a wrong command line, and so is a number that is not 1 or more, or not a number: exit code 2, and nothing is read or started. ``toobusy: give `--task` or `--max`, not both``, ``toobusy: `--max` takes a number of tasks, which is 1 or more``. `run --demo` takes both options. The menu of `toobusy` has neither: its `Run` opens a run of the whole queue.

A third option leaves the tasks with a label out of one run, for an assistant that cannot do them this time, such as tasks of design for one that cannot reach the tool of design:

| Option | Does |
|---|---|
| `--skip-label <name>` | holds the tasks with that label for this run, and the tasks that wait for them, as [the queue](#the-queue) describes. Repeatable, and several names can be given at once, separated by commas, as `--blocking-label` of `init` takes them |

A label is compared whatever its case, as labels are, and any name is taken, whether the repository has the label or not: a label that no task has skips nothing. The skip holds for the whole run, as the queue is read anew before every task, and goes with `--task` and with `--max`. The task an earlier run left with a session is gone on with whatever its labels, as [Going on with a session](#going-on-with-a-session) describes. The first line of the run names the labels after the count of the queue: `✻ v0.2.0 · 5 tasks in the queue · skips design, sketch · opus · high effort · auto mode`. A session is told nothing of it. The menu of `toobusy` chooses the same with `Skip labels`, for the runs it opens.

The exit code says how the run ended: 0 for a run that ran out of tasks, that its owner stopped with `stop` or `abort`, that did the task or the number of tasks it was started for, or that a usage limit paused; 1 for one that stopped because something was wrong, and for one whose only task was not taken; 130 for one that was killed.

### `toobusy queue`

`queue` shows the queue without running it. It reads the open tasks as a run does before a task, with the record an earlier run left on the machine, and prints three lists. It changes nothing: not in the tracker, not on the machine. `queue --skip-label <name>` shows the queue as a run with [the same option](#toobusy-run) reads it, as text and as `--json`: a task with such a label is held with `it has the design label, which this run skips`, and so are the tasks that wait for it.

```text
Ready
  #71 Paint the windows yellow         interrupted
  #12 Export the orders as a file
  #15 Fix the rounding of a discount

Later
  #20 Deploy                           waits for #12

Held
  #7  Choose the format of the export  it waits for the owner: it has the manual label
  #9  Draft of the new page            it has the draft label
  #14 Rename the settings page         its status is In Progress
```

- **Ready** are the tasks a run takes now, in the order it takes them. The task an earlier run left with a session stands first, with `left with a session` after its title; a task with the label of interrupted tasks has `interrupted` there. A task of a record that a run would not go on with, because it was closed since or got a label that keeps it out, is not put first: it stands where the rules of [the queue](#the-queue) put it.
- **Later** are the tasks that open when what they wait for is closed, in the order they open, each with the tasks it waits for: `waits for #12, #15`.
- **Held** are the tasks no run takes as things are, by their numbers, each with the reason in the words a run says it in.

A list without tasks is not left out: it says `no tasks` under its name. The numbers and the titles stand in the same columns in all three lists, and a title of more than sixty characters is cut to sixty with `…`. The names of the lists are strong, the numbers, what a task waits for and why it is held are muted, and `interrupted` and `left with a session` have the colour of a warning, as [Colours](#colours) describes; without a terminal the lines are plain. The command has no screen of its own, in a terminal and without one.

`queue --json` prints the same for an assistant or a script, as one JSON object with the three lists, `ready`, `later` and `held`, each an array that is there when it is empty too. A task has its `number`, its `title`, whole however long it is, its `url`, the address of the task in the tracker, and its `labels`. A task of `ready` that a run takes before the others has `first` as well: `session` for the task an earlier run left with a session, `interrupted` for one with the label of interrupted tasks. A task of `later` has `waitsFor`, the numbers of the tasks it waits for, and a task of `held` has `reason`, the same words as in the list.

```json
{
  "ready": [
    {
      "number": 71,
      "title": "Paint the windows yellow",
      "url": "https://github.com/acme/rocket/issues/71",
      "labels": ["feature", "interrupted"],
      "first": "interrupted"
    }
  ],
  "later": [
    {
      "number": 20,
      "title": "Deploy",
      "url": "https://github.com/acme/rocket/issues/20",
      "labels": ["feature"],
      "waitsFor": [12]
    }
  ],
  "held": [
    {
      "number": 9,
      "title": "Draft of the new page",
      "url": "https://github.com/acme/rocket/issues/9",
      "labels": ["feature", "draft"],
      "reason": "it has the draft label"
    }
  ]
}
```

`queue` needs what `run` needs to read the queue, and says what is missing as `run` does, on the error stream, with `--json` too, where nothing is printed besides:

| What is missing | It says | Exit code |
|---|---|---|
| a git repository | `toobusy: not inside a git repository` | 2 |
| the settings | the message of [a project that is not set up](#not-set-up) | 2 |
| settings that validate | `toobusy: the settings of this project are not valid.` and the errors, a line each | 1 |
| a milestone to work on | `toobusy: the milestone to work on is not chosen.`, or that the chosen one is not open any more, and `` Run `toobusy milestone` to choose it. `` | 1 |
| a GitHub `origin` remote | ``toobusy: the `origin` remote is not a GitHub repository, so there are no tasks to take`` | 1 |
| tasks that can be read | `toobusy:` and what the tracker says, such as `toobusy: acme/rocket has 101 open tasks, and the queue reads only the first 100.` | 1 |

It does not need the model and the effort, and makes no checks of [`doctor`](#toobusy-doctor): what is wrong with `gh` is said when the tasks cannot be read. When the queue is shown the exit code is 0, whatever is in the lists.

`queue --demo` shows the made-up tasks of [a demo of a run](#a-demo-of-a-run), as its first run finds them: the task a run was killed over stands first as one left with a session. It needs no settings, and its last line says `Demo: the tasks are made up.`; with `--json` that line is not printed.

### `toobusy status`

`status` says whether a run is going on in the working copy, and changes nothing. A run works in the working copy itself, so a commit that is made beside it lands in the middle of its task: an assistant that works with the owner in the project looks here before it commits. The record of a session does not say it, as it stays after a run that was killed.

A run that goes on is told in one line, with the task it works on and the command that opens its session:

```
A run is going on: #12 Export the orders as a file · claude attach 1a2b3c4d
```

What the run waits for stands between the two, and a run that has no task at the moment says so:

| The run | The line |
|---|---|
| does a task | `A run is going on: #12 Export the orders as a file · claude attach 1a2b3c4d` |
| has taken a task whose session is not started yet, brings its branch up to date, or runs a command of the project around it | `A run is going on: #12 Export the orders as a file` |
| has a session that waits for the owner | `A run is going on: #12 Export the orders as a file · its session waits for the owner · claude attach 1a2b3c4d` |
| waits for a usage limit | `A run is going on: #12 Export the orders as a file · waits for a usage limit until 2030-01-01 14:05 UTC · claude attach 1a2b3c4d` |
| is between tasks: it checks the working tree or reads the queue | `A run is going on: between tasks` |

Without a run it says so, and names the task an earlier run left with a session, when there is one, by its number: the title of a task is the tracker's, which `status` does not ask.

```
No run is going on.
An earlier run left #12 with a session: the next run goes on with it.
```

It reads what a run wrote down in [`.toobusy/local/`](#local-state), the sign of the run and the record of a session, and nothing else: it needs a git working copy, and no settings, no choices of the user, no network, no `gh` and no terminal. It works from any folder of the working copy. Outside one it says `toobusy: not inside a git repository` and exits with code 2. The sign of a run that died does not count, as [Local state](#local-state) describes, and is left where it is.

**The exit code** tells a run that goes on from none, so that a script does not read the text: 0 when no run is going on, so that `toobusy status && git commit` commits only then, and 3 while one is. Neither is a failure, and 1 and 2 stay what they are for every command.

`status --json` prints the same as one JSON object, with the same exit code. `running` is always there. Of a run that goes on there are `process`, the number of its process, `since`, when the run started, and `phase`: `between-tasks`, `preparing` for a task that is taken and whose session does not work yet, `working`, `waiting-for-owner` or `waiting-for-limit`. `task` is the task it works on, with its `number`, its `title` and its `url`, and `session` the command that opens its session; neither is there between tasks, and `session` not before the session is started. `until` is when a wait for a usage limit ends. The times are in UTC.

```json
{
  "running": true,
  "process": 4242,
  "since": "2030-01-01T09:00:00Z",
  "phase": "working",
  "task": {
    "number": 12,
    "title": "Export the orders as a file",
    "url": "https://github.com/acme/rocket/issues/12"
  },
  "session": "claude attach 1a2b3c4d"
}
```

Without a run, `left` is the task an earlier run left with a session, when there is one: its number as `task`, and how the session was left as `state`, which is `working`, `paused` for a session that a usage limit stopped, or `wrapped-up` for one that wrapped the task up after `/abort`.

```json
{
  "running": false,
  "left": {
    "task": 12,
    "state": "working"
  }
}
```

`status --demo` shows a made-up run that goes on, whatever the working copy has, and exits with code 3 as for a real one. Its last line says `Demo: the run is made up.`; with `--json` that line is not printed.

### `toobusy about`

`about` tells how a run works, for the owner of a project and for the assistant that helps them to fit the instructions of the project to it and to write or correct the skill a session starts with. It describes and asks for nothing: what is done with it is theirs to decide. At its head it names [`toobusy init --assist`](#with-an-assistant), the walk through the setup, for the assistant that reads `about` first. A run needs none of it, as a session is told all it needs in its first message.

It prints, as Markdown and in this order: which tasks a run takes and how it does them, with the commands that show the queue and say whether a run is going on, the permission mode a session works in and what it checks, and that what a session is allowed and refused in particular stays in the settings of Claude Code in the project; that toobusy keeps the task of a session, and what it does to it before the session; that a session makes new tasks itself, which of them toobusy asks for, and what of them is the project's to say; that a session starts with the skill the settings name, what the first message says already, so that the skill does not say it again or against it, and what is the skill's to say: how the work of the project is done and checked without its owner, what is done before and after it, the language of the report, and which tasks a session makes besides the one of what is left; the branch of the tasks: that a run checks the working copy is on it and brings it up to date with its upstream before a task, and that a session brings it up to date before its push as the instructions of the project say, so that they do not tell a session to do what the run does and do tell it how the branch is brought up to date; the commands of the project a run runs around a task, when each runs, what it learns of the task and what a failure does, so that the instructions of a project do not tell a session to do the same; the messages a session gets, word for word, and when each of them comes — the first one, the one for a session that waits for the owner, the one for a session that has reported and is marked as waiting all the same, the one after a usage limit, those for a session that a later run goes on with, and the one of `/abort`; the lines a reply ends with, what is checked after each and what is done to the tracker, and that a commit may close its task, which the run settles all the same; and what holds over the instructions of a project. The messages, the times and the counts are those a run uses, so the text cannot say another thing than a run does.

In a project that is set up the labels of its settings are named, as in ``the `manual` label``, and it is said whether the project has a board. The skill is named too, and the first message is shown as a session of the project gets it, with the command of the skill at its head; a project that names none is told so. The branch and the commands around a task are named as the settings have them, and a project that has none is told so. So is the permission mode a session is started in, where `about` says that nobody answers the prompts of a session: the one the settings name, or `auto` of a project that names none, which is told how to name one; where no settings are read, it is `auto` unless the settings of a project name another. Where there are no settings, or they do not validate, the labels are spoken of without their names, the first message is shown without a skill, and the text says that no settings are read.

`about` reads nothing but the settings: it needs no network, no terminal and no `gh`, works outside a git working copy, as `--help` does, and exits with code 0.

### `toobusy doctor`

`doctor` checks everything a run needs and prints one line per check: passed, failed or skipped, and for a failed one what is wrong and, on a line under it, the command that fixes it. It exits with 0 when nothing failed and with 1 otherwise. It changes nothing. Outside a git working copy it fails as the other commands do, with exit code 2.

```
✔ git
✔ GitHub CLI
✘ GitHub login       `gh` is not logged in to github.com.
  fix: gh auth login
✔ Claude Code
✔ Claude Code login
✔ settings           .toobusy/settings.toml
○ repository         waits for the GitHub login
○ board              waits for the GitHub login
○ statuses           waits for the board
○ labels             waits for the repository
✔ skill              .claude/skills/task-auto/SKILL.md
```

A line is the mark, the name of the check and what the check found: `✔` in the success colour, `✘` and what is wrong in the error colour, `○` and what a skipped check waits for in the muted colour, as is the line of the fix. Settings that do not validate have their errors under the check, a line each, as `run` prints them.

In a terminal the check that is running has a line with a sign that turns, and the line is replaced by the result when the check finishes; without colours the sign stands still. Without a terminal each line is printed when its check finishes.

| Check | Fails when | Needs |
|---|---|---|
| git | `git` is not on the path, or does not run | — |
| GitHub CLI | `gh` is not on the path | — |
| GitHub login | `gh auth status` reports no login for github.com | GitHub CLI |
| Claude Code | `claude` is not on the path | — |
| Claude Code login | Claude Code is not logged in | Claude Code |
| settings | the file is missing or does not validate | — |
| repository | the `origin` remote is not a GitHub repository, or that repository cannot be read | git, GitHub login |
| board | the board cannot be read, or the token lacks the `project` scope | settings with a board, GitHub login |
| statuses | the board lacks one of the three statuses the settings name, `Todo`, `In Progress` and `Done` where they name none, or its statuses cannot be read | a board that passed its check |
| labels | a label of the settings does not exist in the repository, or the labels cannot be read | settings, repository |
| skill | the skill the settings name is not there | settings that name a skill |

A check whose need is not met is skipped and says what it waits for; the board of a project that has none is skipped as well, and says so, as are its statuses, and so is the skill of a project that names none. Without settings the first five checks run, the settings check fails with the "not set up" message and `toobusy init` as its fix, and the repository is checked all the same.

A tool is on the path when it tells its version: `git --version`, `gh --version`, `claude --version`. The `git` that macOS has before its developer tools are installed is on the path and only says how to install them, so it does not count. Claude Code is logged in when `claude auth status` exits with 0 and its answer, which is JSON, does not have `"loggedIn": false`; nothing of a session is started for it. Labels are compared whatever their case, as GitHub does.

The statuses of a board are the options of its `Status` field. The check goes by the names of the settings, each the name GitHub gives where the settings do not name it, and they are compared as a run compares them, whatever the case of their letters and whatever stands between them, as [The project and its settings](#the-project-and-its-settings) says. A check that passes says what the board calls its statuses, and one that fails says which are missing and which the board has: `The board has no status “In Progress”: it has Backlog, Ready, Done.` A board without a `Status` field fails in the same way, with `it has no statuses`. The check matters because nothing else tells it: a run takes a task only from the status the settings name, so over a board that calls its statuses otherwise every task is held, and the queue is empty without a word about the board.

A skill is there when Claude Code would load it by its command: a file `.claude/skills/<name>/SKILL.md` or `.claude/commands/<name>.md` in the root of the project, or the same two in Claude Code's own folder of the user, `$CLAUDE_CONFIG_DIR` or `~/.claude`. The check says where it found the skill. A skill that a plugin brings, named with the plugin before a colon as in `toolbox:task-auto`, is not looked for, and its check passes and says so. The check matters because of what Claude Code does with a command it does not know: it says so and asks the model nothing, so a session that starts with a skill that is not there does no work and never ends.

One thing a run needs is not checked: that Claude Code trusts the folder of the project. A run finds that out when it starts its first session, and says to run `claude` there once.

**Fixes.** Each failure names a fix for the platform the tool runs on:

| | macOS | Linux | Windows |
|---|---|---|---|
| git | `xcode-select --install` | link to the official instructions | `winget install Git.Git` |
| GitHub CLI | `brew install gh` | link to the official instructions | `winget install GitHub.cli` |
| Claude Code | `curl -fsSL https://claude.ai/install.sh \| bash` | the same | `irm https://claude.ai/install.ps1 \| iex` |
| GitHub login | `gh auth login` | | |
| `project` scope | `gh auth refresh -s project` | | |
| Claude Code login | `claude`, then `/login` | | |
| a missing label | `gh label create "<name>" --repo <owner>/<name>`, one for each | | |
| a board that lacks a status | `say which status of the board is which: toobusy init, or work without a board: toobusy init --no-board` | | |
| a missing skill | ``write .claude/skills/<name>/SKILL.md, as `toobusy about` tells, or name no skill: toobusy init --no-skill`` | | |

A command of a package manager is proposed only when that manager is on the path (`brew`, `winget`); otherwise the fix is the link to the tool's official installation page. The fix of the statuses is the setup, which asks which status of the board is which; a board with fewer than three statuses gets more on GitHub first. A repository or a board that cannot be read, statuses that cannot be read, and an `origin` remote that is not on GitHub, have no fix to name. Only macOS is verified by hand in the first version.

**A demo.** `doctor --demo` runs the same checks over a made-up machine that has it all: every tool, both logins, a repository and a board that can be read, the three statuses the settings name on that board, and every label and the skill the settings name; where the project has no settings they are made up. Each check takes a moment, so that the line of a running check can be looked at, and the last line says that the checks were made up.

**Use by other commands.** `init` runs the checks that need no settings and goes on whatever they say. `toobusy run` runs all of them before anything else, after it has found the settings and before it asks for the milestone, the model and the effort. When one fails it prints the failures with their fixes, as errors, says that the run did not start, and exits with code 1. In a terminal the check that is running is shown while it runs and leaves nothing behind. `Run` of the menu of `toobusy` runs all of them too before it opens the run: the check that is running is shown at `Run`, and when one fails the run is not opened and the failures stand above the menu with their fixes. A demo of a run checks nothing, from the command line and from the menu.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | done; a run that ran out of tasks, that its owner stopped, that did the task or the number of tasks it was started for, or that a usage limit paused; a queue that is shown, whatever is in it; `status` when no run is going on; the steps of `init --assist`, whatever they find |
| 1 | failed: a check did not pass, a value was refused, `init` was declined or left, the settings do not validate, the milestone to work on, the model or the effort is not chosen, the tasks cannot be read, a run stopped because something was wrong, the task of `run --task` is not one a run takes |
| 2 | there is no project, the project is not set up, there is no home folder to keep the choices in, or the command line is wrong |
| 3 | a run is going on in the working copy: `status` says so, and `run` did not start beside it |
| 130 | a run was killed |

### Tests

- The settings: reading, every validation rule, writing, and that a rewrite keeps unknown comments, the commands around a task and the branch. The branch: read, left out, refused where git does not take its name, and written into its section. The permission mode: read, left out, each mode of Claude Code that is refused with its reason, a mode of an assistant toobusy does not know, and written into the section of the assistant. The statuses of a board: read, each with the name of GitHub where it is not named, refused when one is empty, when two are one status and without a board, and written only where they differ from the names of GitHub.
- The choice of the milestone against made-up lists of open milestones, the order of the milestones, and the file the choices are kept in: the milestone, the model and the effort, each apart.
- What is asked of `gh`, argument by argument, and what is made of its answers and its failures, with the commands of the machine faked.
- The setup steps, with the questions answered from a script and the tracker and the environment faked: a first setup, an existing setup, going back, a new board, the statuses of a board, asked for, typed, proposed, named by the options and not asked for, a board with fewer than three of them, a missing `gh`, the skill, named, left out and not written yet, the commands around a task, set, changed, taken away, kept and empty, the branch, named, changed, taken away, kept and refused, the permission mode, named whatever its case, changed, kept and refused, every option, `--yes`, no terminal.
- The screens, through a terminal of a test: scripted keys, a size, and the frames that were drawn. A tape is read as the rows the terminal's own screen has after it, sequence by sequence, so that a tape that has lost count of its lines fails. A key that a test holds back for something that never happens fails the test instead of letting the page go on for ever.
- `doctor`: each check passed, failed and skipped, the statuses of a board with each one missing, without the field and not read, and by the names of the settings, the skill looked for in the folders of a test, the fix for each platform with and without its package manager, the lines of the checks with and without a terminal, and a run that does not start while a check fails, from the command line and from the menu, where one that starts after the checks is covered too.
- `status` through the command line: no run, the task of a record, a run that goes on, with its task, between tasks and waiting for a usage limit, what a run that died left behind, `--json`, a demo, outside a git working copy, and that it asks nothing of the machine and leaves nothing on it.
- The steps of `init --assist`, as a function of what is read: every step over what a project may have: no settings, settings that are valid and settings that are not; a board, no board, a board with other statuses and settings that name them; a skill that is named and written, which is read against `about`, named and not written, and none, which keep the step as it was; what becomes of an old way of running the tasks in step 5 and of what was taken out in step 9; that no step names a repository it was not given, a script or the line another tool reads; commands around a task and none; a branch and none; a permission mode and none; choices that are made and not made; a tracker that cannot be read. Through the command line: the introduction with its list; a number after the last step; a number that is not one; `--assist` with another option; outside a git working copy; without a terminal and in one; a run that goes on; that nothing is written anywhere, and nothing is asked of the tracker but what is read; and that every command line a step names is parsed by the real command line of the tool, so that no step names a command or an option that is not there.
- `--version`: the version, and the commit of a build in development after it; the informational version of a build split into the two, with the commit shortened, whole and missing.
- The bar of the menu: the version that `--version` prints at its right, without the commit, in a demo too, and the folder alone in a window too narrow for both.
- The help of `toobusy` and of `init`, and the two messages that name `init --assist` where there is no terminal.
- The "not set up" message and the exit codes, through the command line; `about` outside a project, in one that is set up, with a skill and without one, with the commands around a task, with a branch and without one, and in one whose settings do not validate.

## Running the tasks

`toobusy run` takes the tasks of the project one after another and has the assistant do each of them in the working copy, in a session of its own. It goes on until no task is left, until the owner stops it, or until something happens that it does not go on past. A run that is started for one task, or for a number of them, ends when it has done as much.

### Who does what

- **The run keeps the task of a session.** It moves the task before its session starts and after it ends, assigns it to the user who runs toobusy, writes the report of the session into the task, puts and takes off its labels, and closes it. The assistant changes nothing of its own task, and it is told so.
- **The assistant makes new tasks**, and the run makes none. toobusy says what a run depends on: what cannot be done without the owner becomes a new task with the manual label, so that no run takes it, and the first message names what the session needs for that. The project says the rest in its instructions: which other tasks a session makes and when, such as the findings of an audit or a proposal, and their labels, their wording and their relations.
- **The assistant does the work** as the project's own instructions say (`CLAUDE.md`, `AGENTS.md` and the like): it changes the working copy, checks the change, commits it and pushes it, and ends its last reply with one line that says how the task went.
- **Nothing is taught to the assistant beforehand.** What a session has to know of the run it is told in its first message, and another assistant is told the same. No skill is needed for a run. A project may name one for a session to start with, which says what belongs to the project: how a task is done there without its owner. A project that wants its instructions and its skill to fit a run reads how a run works from `toobusy about`.

### The queue

The tasks are the open issues of the repository of the `origin` remote that belong to the milestone of the user, or all of them when the user works without a milestone. A run reads them anew before every task. More than a hundred open tasks are an error that says how many there are.

A task is **held**, and no run takes it as things are, when one of these is so, in this order:

| Reason | The run says |
|---|---|
| it has a blocking label | `it has the draft label` |
| it has the manual label | `it waits for the owner: it has the manual label` |
| there are labels to take and it has none of them | `it has none of the labels to take: feature, bug` |
| the run was told to skip a label of the task, by [`run --skip-label`](#toobusy-run) or `Skip labels` of the menu | `it has the design label, which this run skips` |
| the project has a board, and the task does not stand there as one to do | `its status is In Progress`, `it is not on the board` |

In a project with a board a task is taken only from the status `Todo`: the one the settings name so, as [The project and its settings](#the-project-and-its-settings) tells, and the one GitHub gives a new board where they name none. The three statuses a run knows are known whatever the case of their letters and whatever stands between them: `To do` and `in-progress` are those statuses too. The reason a task is held for names its status as the board calls it, whatever the settings say: over settings that take tasks from `Ready`, a task that stands in a status the board calls `Todo` is held with `its status is Todo`. A board that lacks one of the three gives a run nothing to take or nowhere to move a task, so [`doctor`](#toobusy-doctor) checks that the board has them, and a run does not start over one that does not. A project without a board has no statuses: no task is held by one, and none is moved. Nothing of the statuses is asked of GitHub there: the request for the tasks names no board, so it is answered to a token without the `project` scope too.

A label to skip holds a task for one run and no longer: a label that keeps a task out for good, the manual label and a missing label to take are said before it. It names the label as the task has it, whatever the case it was given in.

A task **waits** for the open tasks that block it and for its own open sub-issues. It opens when they are closed, and is held while one of them is held or is not among the tasks that were read: `it waits for #7 (it has the draft label)`, `it waits for #7 (it has the design label, which this run skips)`.

The tasks that can be taken now are taken in this order: those with the label of interrupted tasks first, then by their numbers. Before any of them comes the task an earlier run left with a session, as [Going on with a session](#going-on-with-a-session) describes.

### One task, or a number of them

**`run --task 12`** does task #12 and no other. Its first line says so: `✻ v0.2.0 · #12 and no other task · opus · high effort · auto mode`. The task is done as any task of a run is, and when it has come to an outcome, done, done in part or left to the owner, the run ends as one that its owner stopped: `‖ Stopped after #12, as you asked · 1 done`. The queue is not read again, so a task that the session made is not taken by this run, whatever its labels.

The task is taken only when a run of the queue would take it now, whatever its place among the tasks that can be taken. Otherwise nothing is started and nothing is changed: the run says why, in the words of [the queue](#the-queue), and exits with code 1.

| The task | The run says |
|---|---|
| is held | `✖ Stopped: #12 is not taken: it has the draft label · no task was started` |
| has a label the run skips | `… #12 is not taken: it has the design label, which this run skips · …` |
| waits for tasks that a run would close before it | `… #12 is not taken: it waits for #7, #9 · …` |
| waits for a task that is held | `… #12 is not taken: it waits for #7 (it has the draft label) · …` |
| is not an open task of the milestone | `… #12 is not taken: it is not among the open tasks of the milestone v0.2.0 · …`, and without a milestone `it is not among the open tasks` |
| is another one than the task an earlier run left with a session | `… #12 is not taken: an earlier run left #7 with a session, and a run goes on with that task before any other · …` |

The last of them is the task of [the record](#going-on-with-a-session). A run takes it before any other, its changes may be in the working tree, and only one task has a record: another task is not started over it, and its session and its record are left as they are. `run --task 7` goes on with it, as a run of the queue does. The same holds for a task of the record that is closed and is to be settled: `… #12 is not taken: an earlier run left #7 with a session, and a run settles that task before any other · …`, and `run --task 7` settles it. A record whose task is not gone on with any more, one that got a blocking label for one, is forgotten as in any run, and holds nothing back.

**`run --max 3`** takes three tasks at most. Its first line says `✻ v0.2.0 · 8 tasks in the queue, 3 at most · opus · high effort · auto mode`. A task counts when it has come to an outcome in this run: done, done in part or left to the owner. A task that is gone on with counts as one, however it was stopped before: the task of a record, an interrupted one, one whose session asked and was told to go on alone, and one that waited for a usage limit. After the last of them the run ends as one that its owner stopped, `‖ Stopped after 3 tasks, as you asked · 3 done`, and the queue is not read again. The queue is read before every task, as ever: a task that a session made and that can be taken is taken in its turn, and counts. A run that runs out of tasks first says `No task is left`, as any other.

Everything else that ends a run ends these too, with its own words and its own exit code: `/stop`, `/abort`, a kill, a usage limit that pauses a task, and a task that the run stops over. Such a task does not count.

### A task

1. **The working tree** must be clean: no changed file and none that git does not know. Otherwise the run stops with `the working tree is not clean: commit or stash the changes first`. Only the changes of the task an earlier run left with a session are started over. In a project that names its branch, the working copy must be on that branch first, as [The branch of the tasks](#the-branch-of-the-tasks) describes.
2. **The queue** is read, and the task of the record, or else the first task that can be taken, is the one.
3. **The limits** of usage are read for the page of the run. They hold no task back, as described below.
4. **The branch is brought up to date** with its upstream, in a project that names one, for a task that is taken anew. When it cannot be, the run stops, and the task is not touched.
5. **The command before a task** runs, in a project that has one, as [The commands of the project](#the-commands-of-the-project) describes. When it fails, the run stops, and the task is not touched.
6. **The task is moved to `In Progress`** on the board, the status the settings name so. Nothing of the task is read for the session, which reads it on its own.
7. **A session is started** with the model and the effort of the user, in the permission mode of the settings and, in a project that names one, with its skill, and the task and the session are written down on the machine. A task that was interrupted loses its label then. A tracker that refuses that stops the session as it stops the run, as [Going on with a session](#going-on-with-a-session) describes.
8. **The task is assigned** to the user who runs toobusy, the one `gh` is logged in as, beside the assignees it has: everyone who looks at the task or at the board sees who is doing it. It is done in a project with a board and in one without, where it is the only sign that a task is being worked on. The log says `#12 is In Progress on the board and assigned to the current user; the session is told that toobusy keeps its task`, and without a board `#12 is assigned to the current user; …`. A tracker that refuses the assignment does not stop the run: the log says what was refused, `▲ #12 could not be assigned to the current user. … · the run goes on`, and the task is done as any other.
9. **The session is watched** until its turn ends with a line for toobusy.
10. **The outcome is checked** against the working copy, and the tracker is told.
11. **The command after a task** runs, in a project that has one, however the task went.

A session of Claude Code is a background one, started with `claude --bg`: it goes on by itself, and the owner can open it with `claude attach <id>`. The run stops it when its task is over, and never removes it: it stays among the sessions of Claude Code, with its conversation. The lines of a run that call for a look at a session name that command: a task that is taken, a session that waits or is told to go on, a kill, and a run that stops because something is wrong with a task. The session is started in the permission mode of the settings, `auto` where they name none, as [The permission mode of a session](#the-permission-mode-of-a-session) describes, with nobody to answer its permission prompts, and works in the working copy itself, without a worktree. A session that is told to go on, one of an earlier run among them, goes on in the mode of the settings as they are then: `claude --bg --resume <conversation> --permission-mode <mode> <message>`. Claude Code must trust the folder: where it does not, the run stops and says to run `claude` there once. A task whose session did not start, for this or for another reason, goes back to `Todo`; it was not assigned yet, as a task is assigned once its session has started.

### The branch of the tasks

A project does its work on one branch, and a task that is started on another one, or on a branch that lacks what was pushed since, is built on the wrong ground. `workspace.branch` of the settings names the branch the tasks are done on, and the run looks after it around every task. A project that names none has its tasks done on whatever is checked out, as it stands, and nothing of this is done.

**The branch.** Before every task, where the working tree is checked and before the queue is read, the working copy must be on the branch of the settings. On another one, or on none, as on a detached HEAD, the run stops with exit code 1, before the working tree is looked at for changes:

```
✖ Stopped: the working copy is on feature/export, and the tasks are done on develop · the next task did not start · 3 done
```

The run does not change the branch itself: what is checked out is the owner's. The branch is checked for every task, the task of [the record](#going-on-with-a-session) among them.

**The sync.** Before a task that is taken anew, once the queue is read and before the command before the task, the branch is brought up to date with its upstream: what the remote has is fetched, with `git fetch`, and the branch is moved forward to it, with a merge that can only be a fast-forward, so that the task starts from what the remote has. The log says so when the branch moved, `  develop is moved forward by 3 commits to origin/develop`, and nothing when it had what its upstream has. A branch that cannot simply be moved forward stops the run with exit code 1, and the task is not touched, as when the command before it fails:

| The branch | The run says |
|---|---|
| has commits its upstream lacks | `✖ Stopped: develop has 2 commits that origin/develop lacks: push them first, or take them back · #12 is not taken · the next task did not start · 3 done` |
| has parted from its upstream: each has commits the other lacks | `… develop and origin/develop have parted: develop has 1 commit that origin/develop lacks, and lacks 3 commits of it: rebase or merge it first · …` |
| cannot be fetched: the remote refuses, or does not answer three tries in a row, as [A tracker that does not answer](#a-tracker-that-does-not-answer) describes | `… origin/develop cannot be fetched: fatal: unable to access 'https://github.com/acme/rocket/': Could not resolve host: github.com · …`, with the first line git said at the last try |
| cannot be moved otherwise: its upstream is not on the remote any more, or git refuses | `… develop cannot be brought up to date with origin/develop: origin/develop is not on the remote any more · …` |

A branch without an upstream has nothing to be brought up to date with: the log says so once in a run, `  develop has no upstream, so there is nothing to bring it up to date with`, and the tasks go on. There is no setting of its own for the sync: a project that names its branch wants it level with its remote before every task, and a run always does that. While the branch is brought up to date, the page of the run shows the task with `bringing the branch up to date` as what is being done.

**A task that is gone on with.** The task of the record may start over the changes its session left in the working tree, and its session may have commits that are not pushed yet: nothing is moved under them. Its branch is checked as for any task, and it is not brought up to date: its session does that before its push, as below. A new session for such a task, whose conversation is gone, is not brought up to date either, and a task that is only settled is not.

**The push of a session.** The branch may move while a session works: someone pushes to it meanwhile. Bringing the branch up to date before the push is the session's business, and the project's to say how, by a rebase or by a merge, in its instructions: the first message of a session says that the branch may have moved on the remote by the time it pushes, and that it brings the branch up to date then as the instructions of the project say, and pushes again. toobusy checks the result only, as [How a task ends](#how-a-task-ends) describes: a session that leaves commits its upstream lacks stops the run. This holds in a project that names no branch too.

**[`doctor`](#toobusy-doctor)** does not look at the branch: which branch is checked out, and whether it is level with its remote, is how the working copy stands at one moment and not what the project has. A run checks it before every task.

**A demo of a run** checks nothing of it, whatever the settings say.

### The commands of the project

A project may need something done around every task: that the ports of its development server are free before one, that the server is stopped after it. As a rule in the instructions of the project it is something a session may forget, so the run does it itself: `task.before` and `task.after` of the settings are two commands, each optional, that the run runs around a task.

**A command** is a line for the shell of the machine, `sh -c` and on Windows `cmd /c`, run at the root of the working copy with its input closed. The run knows of it only whether it exited with 0. It has the environment of toobusy and, of the task:

| Variable | What it is |
|---|---|
| `TOOBUSY_TASK` | the number of the task |
| `TOOBUSY_TASK_TITLE` | its title |
| `TOOBUSY_TASK_URL` | its address |

A command may run for ten minutes; one that has not ended by then is stopped, with everything it started, and counts as failed, as does one whose shell cannot be run. A command that leaves something running, as a server it started, is over when it exits itself: the run does not wait for what it left.

**The log** says that a command runs and how it ended, and on the page of a run the task it runs for stands as the one that is worked on, with `the command before the task` or `the command after the task` as what is being done. What a command printed, to its output and to where its errors go, is shown only when it fails: its last twelve lines.

```
  Running the command before #12: scripts/dev.sh ports free
  the command before #12 passed in 2 s
→ #12 Export the data — started · a new session · claude attach 1a2b3c4d
```

**`before`** runs when the working tree and the queue are read, the task is chosen and, in a project that names its branch, the branch is brought up to date, before the task is moved to `In Progress` and before its session is started. When it fails the run stops, with exit code 1, and nothing of the task is touched: not its status, not its assignee, and no session is started for it.

```
  Running the command before #12: scripts/dev.sh ports free
✖ the command before #12 exited with 3: scripts/dev.sh ports free
  │ port 3000 is taken by pid 71
✖ Stopped: the command before #12 exited with 3 · #12 is not taken · the next task did not start · 3 done
```

**`after`** runs when the run has done with the task, however it went: done, done in part, left to the owner, interrupted, failed or left to the owner by a check that did not pass, paused by a usage limit, and when the tracker refused something or the session did not start. It runs after the checks of the working copy and after the tracker is told, so that the outcome of the task does not wait for it and does not depend on it; what the command leaves in the working tree is found by the check before the next task, which stops the run. When `after` fails, the task stays as it ended and the run stops: `✖ Stopped: the command after #12 exited with 1 · #12 stays as it ended · the next task did not start · 4 done`. The next task is not started over what the command was to put right. In a run that ends over the task anyway, the failure is said and changes nothing of why the run ended: `▲ the command after #12 exited with 1 · see to it by hand before the next run`. `after` does not run when `before` failed.

**A task that is gone on with** has the commands around it as any other, in the run that goes on with it: `before` runs before its session is told to go on, or before a new one is started for it, and when it fails the session is not told, the record and the task stay as they are, and the next run tries again. Two cases of [Going on with a session](#going-on-with-a-session) differ: a session that still works when the run finds it is not disturbed by `before`, and one that ended while nobody watched is only settled; `after` runs for both.

**A wait for a usage limit** inside a task has no command around it: the task is still the one of the run, and what `before` made ready stays. When the wait ends in a pause, `after` runs as after any task, and `before` runs again in the run that goes on with the session.

**A kill** of the run cuts a command that is running, with everything it started. A kill during `before` leaves the task untouched, and `after` does not run. A kill while the session works stops the session and then runs `after`, so that what the task started does not go on with nobody watching; the kill does not cut it, and it has thirty seconds then, as the owner waits for the run to end. A kill during `after` cuts it, and the run ends as killed.

**A demo of a run** runs no command of the project, whatever the settings say.

### What a session is told

The first message of a session names the task and gives the rules. It starts with `You are doing task #12 of this project without its owner.` and with what toobusy is, the same for every session but for the number, so that a session of a run can be told from any other.

**The skill of the project.** The message is the same in every project: it says how a run works and nothing of the project. What a session without its owner does in this project, such as which checks it runs, what it stops at the end and what language it reports in, is said by the project, and a project that names a skill in `session.skill` has its sessions start with it. The first message then begins with the command of the skill, the number and the title of the task, and goes on as it does in any project:

```text
/task-auto #12 Export the orders as a file

You are doing task #12 of this project without its owner. …
```

Claude Code loads the skill by that line itself, in a background session as in an interactive one, so nothing is left to the session; the whole message reaches the session with the skill, as its arguments. A project that names no skill has the first message begin with the words of toobusy. The skill is loaded once, at the start: the messages for a session that goes on do not name it, and a new session for a task whose conversation is gone starts with it again. The log says of a session that is started so `a new session with the skill task-auto`. The skill says only what belongs to the project; how a run works stays in the message of toobusy, and [`toobusy about`](#toobusy-about) tells what each of them says. A run does not start while the skill of the settings is not there: the check of [`doctor`](#toobusy-doctor) fails.

After those words come, in this order:

- the number, the title and the address of the task, and that the session reads the task and its comments before it starts: nothing of them is passed on, so nothing is cut and nothing is stale;
- nobody will answer: no questions, no waiting for an approval, no plan mode. Where the instructions of the project, its skills among them, say to ask the owner, the session does not: the owner started the run to have the task done;
- the instructions of the project hold in everything else, committing and pushing among it. Where they differ from the rules of the session, the rules of the session hold;
- the task of the session is kept by toobusy, which has already moved it to `In Progress` and assigns it to the user who runs it: its status, its assignee, its labels, closing it and commenting on it are not the session's;
- the session may make new tasks in the tracker: the one of what cannot be done without the owner, and those the instructions of the project call for, with the labels, the wording and the relations they say;
- the work is done in this working copy, which was clean and must be clean at the end: everything committed and pushed, or undone. The branch may have moved on the remote by the time the session pushes: then the session brings it up to date as the instructions of the project say, and pushes again;
- the session keeps a plan of the work in its task list: the steps are laid out before it starts, each is marked when it is begun and when it is done, and the list changes when the plan does. The page of a run shows it;
- a task that was interrupted before is told how by the last comment that starts with `**Interrupted.**` or with `**Stopped by a usage limit**`. The first is the report of the session before: the session goes on with what it says is left and does again what it says was undone. The second has no report: the session before worked elsewhere, its uncommitted changes are not here, and the task is done from its description;
- a task that a session worked on before and left no report of, its conversation being gone, may have the changes of that session in the working copy: the session looks at them, keeps what is right and undoes what is not;
- what cannot be done without the owner, as described below;
- the last reply is the report of the task, and its last line is one of the first four of these. The fifth is not in the first message: only the request of [`/abort`](#stopping) asks for it:

| Line | Means |
|---|---|
| `TOOBUSY: done` | the task is done, committed and pushed |
| `TOOBUSY: partial` | a part is done, committed and pushed, and the session has made the task of what is left, which the report names by its number |
| `TOOBUSY: owner` | nothing could be done without the owner; the reply says what is needed, and the changes are undone |
| `TOOBUSY: failed <reason>` | the work stopped on something the session cannot fix; the working copy is left as it is |
| `TOOBUSY: interrupted` | the session wrapped the task up because it was asked to |

The line is read whatever its case and whatever marks of emphasis stand around it; the last such line of the reply counts.

### How a task ends

A task ends when the turn of its session ends with a line for toobusy. It ends so too when Claude Code marks the session as waiting for an answer though its reply ends with the line, once the session was told that no answer will come, and while the run aborts, as [A session that waits for the owner](#a-session-that-waits-for-the-owner) describes. Either way the outcome is read from that reply.

What the session says is checked first: after `done`, `partial`, `owner` and `interrupted` the working tree must be clean, and after `done` and `partial` the branch must have no commit that its upstream lacks; a branch without an upstream has nothing to lack, and passes. After `partial` the report must name a task other than its own, as `#34` or by the address of an issue: the task of what is left, which the session made. It is the report that is read, not the tracker: what the named task is, the run does not look at. Then the tracker is told:

| Outcome | The tracker |
|---|---|
| done | the report as a comment; the task is closed and moved to `Done`, the status the settings name so, as `Todo` below is |
| partial | the report as a comment, which says that what is left is a task that the session made; the task is closed and moved to `Done`. The run makes no task |
| owner | the task gets the manual label and the report as a comment, and goes back to `Todo` |
| interrupted | the task gets the label of interrupted tasks and the report as a comment, and goes back to `Todo` |

**The command after a task** runs then, in a project that has one, whatever the outcome was and whatever the tracker answered, as [The commands of the project](#the-commands-of-the-project) describes.

**The assignee stays**, however the task ends. On a task that is done or done in part it says who did it. On one that goes back to `Todo`, after `owner`, after `interrupted`, after a pause by a usage limit and when the run stopped over it, it says whose task it is: the one who is to answer in it, and the one on whose machine its session and its changes are. A run never takes an assignee off, its own or one that was put by hand, and another user whose run takes the task later is assigned beside the first.

Every comment starts with what became of the task, `**Done.**` for one, and ends with the session. The report between them is put in as the session wrote it, in the language the instructions of the project call for; the words of toobusy around it are in English.

**The run stops**, with exit code 1, when the session failed, when it ended without saying how the task went, when a check did not pass, when the tracker refused something or did not answer it [three times](#a-tracker-that-does-not-answer), when [a command of the project](#the-commands-of-the-project) failed, and when the same task came up again right after its session. `Stopped: #12 left changes in the working tree · claude attach 1a2b3c4d · the next task did not start · 3 done`. The next task is never built on a doubt. Such a task is for the owner: what was written down of it is forgotten, and no run goes on with its session by itself. A tracker that refuses while a task is being taken is another matter: its session is stopped, and the next run [goes on with it](#going-on-with-a-session).

**A task that a run stopped over is left to the owner.** When the session failed, did not say how the task went, or a check did not pass, that of the working copy or that of the report after `partial`, the task gets the manual label and a comment, and goes back to `Todo`; the working copy is left as it is. The comment says what went wrong, then what the session said last, then the session:

> **Failed.** The run stopped over this task: its session left changes in the working tree. Look at what went wrong, take the label `manual` off, and a run takes the task again.

No run takes the task while it has the label, in a project with a board as in one without. The owner looks at it and takes the label off, and a run takes it as any other task, with a new session, which reads the comment with the task. The log says `#12 has the manual label and a comment that says what went wrong; no run takes it while it has the label`. The same is done, as far as the tracker lets it, when the tracker refused something while it was told how the task went, as a task that could not be closed: its record is forgotten by then, and the label is what keeps the next run from it. A task that was wrapped up is not left so: it has its record, and the next run goes on with its session.

A tracker that refuses the label or the comment does not hide why the run stopped: the run stops with the same words, and the log says what was refused, `▲ #12 is not left to the owner as it should be: … · set it right by hand before the next run`. A task that did not get the label stays in `In Progress`, which in a project with a board keeps it out of the next run all the same.

**A task that its commit has closed.** A project may have its commits close their tasks: with `Closes #12` in the message, the push closes the task before the run does, while its session still works. The run settles the task all the same, and what its session said is checked as ever. After `done` and `partial` the report is written into the closed task as a comment, the task is closed once more, which GitHub does not refuse, and it is moved to `Done`.

A closed task is never opened again by a run, and it is not put back among those to do. Where the table and the paragraphs above put a label on a task and move it back to `Todo`, after `owner`, after `interrupted`, when the session failed or said nothing, and when a check did not pass, the run asks the tracker whether the task is closed. One that is gets the comment only, with neither a label nor a status, and the comment says so: `The task was closed before its session ended, and it is left closed: look at what went wrong, open it again, and a run takes it.` The log says `#12 was closed before its session ended, and is left closed, with a comment that says what went wrong; no run takes it until it is opened again`. The run goes on or stops as it does over an open task. A session that says `interrupted` over a closed task leaves no record: no run goes on with a closed task. A tracker that does not say whether the task is closed leaves it as one that is open. The run does not look at who closed the task: one that its owner closed by hand while its session worked is settled in the same way.

A run that was killed between the push and the end of the task settles it later, as [Going on with a session](#going-on-with-a-session) describes.

Three things differ from that:

- A board that does not take a closed task to `Done` does not stop the run: the task is closed, the log says what was refused, and the run goes on.
- A session that says `interrupted` when nobody asked it to wrap up is taken at its word, as the table says, and then the run stops.
- A session that does not start, as in a folder that Claude Code does not trust, sends its task back to `Todo` before the run stops.

### A tracker that does not answer

A run asks the tracker many times over a night: the open tasks before every task, and the status, the assignee, the labels and the closing of each. Now and then such a call gets no answer, because the network or GitHub was away for a moment. That is not a doubt yet: the run makes the call again before it stops over it.

- **Which calls are made again.** Those that are safe to make twice, as making one once more changes nothing a first one did not: reading the open tasks and a task by its number, moving a task on the board, assigning it, putting a label on it and taking one off, and closing it. Not a comment: one whose answer was lost may be written already, and a second would stand twice. Nothing of the assistant is: a session is started once, telling a session something has tries of its own, and the list of the sessions is read again every few seconds as it is.
- **Which failures.** A failure that may pass: `gh` did not answer in its time; it did not reach GitHub, as when the name of the host is not found or the connection is refused or cut; or GitHub answered with a status of the 500s, a 429, or a 403 that names a rate limit, as an error of a query that names one does. A failure that stays is told at once and not tried again: a task that does not exist, a label the repository does not have, a status the board lacks, a token without the scope, no login, `gh` that is not installed. GitHub says so with another status of the 400s or an error of the query. The adapter of the tracker tells the two apart from what `gh` answered: the status of the process, and the HTTP status or the error in its words.
- **How many times.** Three tries in all, with pauses of 10 and 30 seconds between them. After the third failure the run goes on as it does over a tracker that refuses, with the words of the last failure: in most places it stops, and where a refusal does not stop the run, as of the assignment, of the move of a closed task to `Done` or of what leaves a task to the owner, the log says it and the run goes on. A kill ends a pause at once. What is done after a kill, as the pause of a task in the wait for a usage limit, is asked once: the owner waits for it.
- **The log** has a line for every try that comes after a failure, in the words of what the call does:

  ```
  ▲ GitHub did not answer: the open tasks are read again in 10 s
  ▲ GitHub did not answer: #12 is moved to In Progress again in 30 s
  ```

  The others are `#12 is read`, `#12 is assigned to the current user`, `the label manual is put on #12`, `the label interrupted is taken off #12` and `#12 is closed`, with the labels and the statuses as the settings name them. The page shows the task as the one that is worked on meanwhile, as it does while a command of the project runs; between tasks it says what the run is doing, and the sign of the run says `between tasks`.
- **The sync of the branch**, in a project that names its branch, is tried again by the same rule: a fetch that did not end in its time, that did not reach the remote, or that the remote answered with a status of the 500s or a 429, is made again, `▲ The remote did not answer: origin/develop is fetched again in 10 s`. A remote that refuses, as over a login or a repository that is not there, stops the run at once, as [The branch of the tasks](#the-branch-of-the-tasks) describes.
- **The other commands** ask once and say what the tracker says: `toobusy queue`, `doctor`, `init`, `milestone` and the count of the menu are run by a person at the keyboard, who runs them again. A demo imitates no failure and shows nothing of it.

### What cannot be done without the owner

The session is told to do everything that does not depend on the owner, to leave nothing broken, and to commit and push that part. What is left becomes a new task, which the session makes itself. The first message says what the task needs, so that a run deals with it as it should:

- its description is written for a session that has never seen this one: what is left, what is done already and where, and the questions for the owner with the answers the session sees. It names the first task;
- it has the manual label, which the message names as the settings do. A task that has labels to take is told to put them on the new task too, by their names, so that a run takes it once the owner has answered;
- it goes into the milestone the user works on, which the message names. For a user who works without a milestone it is the milestone of the first task, or none when that task has none;
- in a project with a board it stands on the board, whose address the message gives, with the status `Todo`, by the name the settings give it. A project without a board is told nothing of one;
- the session decides whether it blocks what the first task blocks: the run closes the first task, and what waited for it opens.

The session names the new task in its report, and ends with `TOOBUSY: partial`. A report that names no other task does not pass the check, as [How a task ends](#how-a-task-ends) describes: the run does not close a task whose rest it cannot find. The run does nothing to the new task and reads nothing of it.

No run takes the new task while it has the manual label. The owner answers in it, takes the label off, and a run takes it as any other task, and assigns it then.

When nothing at all could be done, there is nothing to close and nothing to make: the task itself gets the manual label and the questions as a comment.

### A session that waits for the owner

A session waits for the owner when Claude Code says that it is blocked on an answer and its reply does not end with a line for toobusy, and when its turn ended without such a line. The run says so, with the first lines of what the session said, and notifies the owner:

```
‖ #12 waits for the owner: input needed · it is told to go on alone in 90 s: /nudge does it now, /hold never · claude attach 1a2b3c4d
  │ The task does not say what kind of file the export is. Should it be CSV or JSON?
```

After ninety seconds the turn of the session is cut, and it goes on with this as the next message of its conversation:

> The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #12 from where you stopped. What cannot be decided without the owner, make a new task of it yourself: describe there what is left and your questions for the owner, and put the label `manual` on it, so that no run takes it before the owner answers. End your reply with a `TOOBUSY:` line.

The label is the one of the settings. The message says it in full, and does not point at the rules of the session: it may come long after them. The log shows the message as it was sent. The command the log calls `/nudge` sends it at once, and `/hold` never: the session waits for the owner, who answers it with `claude attach` or sends the message later. On the page of a run the two are `send now` and `hold`, and they are offered as soon as a session waits. A session that goes back to work by itself is told nothing.

A task is told so ten times at most, and it is left to the owner sooner when two messages in a row brought no step of its own. A session that is left to the owner after its turn ended stops the run, as one that did not say how the task went.

**A session that has reported.** Claude Code marks a turn as waiting for an answer by its words, not by a question: a reply that leaves something to the owner, as the report of a task done in part does, is marked so even when it asks nothing and the session has nothing more to do. Such a session is not left for the owner. A reply **ends with the line** when the last of its lines that is not empty is a line for toobusy, read as [the line](#what-a-session-is-told) always is: whatever its case and the marks of emphasis around it. A question after the line is not that: such a reply does not end with the line, and neither does one after which the session asks with a tool.

1. **A session marked as waiting whose reply ends with the line is told at once that no answer will come.** There is no countdown and no `/nudge` or `/hold`: there is no question the owner could answer, the session has reported. The message counts as one of the ten messages a task gets. The log says so, quotes the reply as it does when a session waits, and shows the message as it was sent; the owner gets no notification, as nothing is asked of them:

   ```
   ▲ #12 ended its turn with a line for toobusy, but Claude Code marks it as waiting for an answer · it is told that none will come · claude attach 1a2b3c4d
     │ Half of the export is written; the dates are left for #34.
     “No answer will come: the owner is away, …”
   ```

   > No answer will come: the owner is away, and nobody reads your questions. Go on with task #12 from where you stopped. What still needs the owner, make a new task of it yourself: describe there what is left and your questions for the owner, and put the label `manual` on it, so that no run takes it before the owner answers. When everything that can be done is done, say so: end your reply with a `TOOBUSY:` line, and ask nothing after it.

   The label is the one of the settings, as in the message after the countdown.
2. **Once a session was told that no answer will come, a reply that ends with the line ends the task**, whatever Claude Code says of its state. The outcome is read from that reply and settled as [How a task ends](#how-a-task-ends) says: the checks of the working copy, the comment, the board. This holds for the message above and for the one a waiting session gets after the countdown alike: a session that asked a question, was told that the owner is away, went on and later ended with the line is over at that reply, and no second message is sent. Told is counted within the run: a session that a later run goes on with may be told once more.
3. **A reply after the message that does not end with the line** is handled as any other: a question, or a turn that ended without the line, waits for the owner with the countdown, `/nudge` and `/hold`, the limit of ten messages and the rule of two that brought no step of its own, and rule 2 holds for what comes after it. The rule of two such messages does not leave a session that ends with the line to the owner: by rule 2 the task is over before that rule is looked at.
4. **While the run aborts**, a session marked as waiting whose reply ends with the line is over at once, with no message: it was asked to wrap up and did, with `TOOBUSY: interrupted`, or it had reported before the request reached it, and its reply is settled as it says.
5. **A usage limit** in the reply comes first: a session that ran into a limit is handled by [the limit](#usage-limits), whatever its state and its last line.
6. **A session an earlier run left** that is found marked as waiting is watched as the run's own, as [Going on with a session](#going-on-with-a-session) describes, and these rules hold for it as for any session.

### Stopping

A run is stopped with the commands of the menu of its page, `stop`, `continue` and `abort`; the log calls them with a slash:

| Command | Does |
|---|---|
| `/stop` | the current task is finished, and no other is taken: `Stopped by /stop · 3 done · 1 done in part`. Between tasks the queue stops at once |
| `/continue` | takes `/stop` back |
| `/abort` | stops as soon as possible, with nothing committed and a report in the task. It cannot be taken back |
| Ctrl+C twice | kills the run: the session is stopped at once, and the task stays in `In Progress` as it is; the next run goes on with its session. A kill during the wait for a usage limit pauses the task instead, as described below. The exit code is 130 |

**`/abort`.** The session is asked to wrap up: to let the command that runs finish and start no new step, to commit and push nothing, to undo its uncommitted changes, and to reply with a report for the session that will go on with the task — what was found out and decided, what was undone, file by file, and what is left — ended with `TOOBUSY: interrupted`. The request reaches a session that works after its next step, without cutting its turn: a hook of the session prints it. A session that has not wrapped up in ten minutes, and one that waits for the owner, has its turn cut and gets the request as a message. A session whose reply ends with a line for toobusy is over then, even where Claude Code marks it as waiting for an answer, and is told nothing more. When the session ends, the task gets the label of interrupted tasks and the report, goes back to `Todo`, and the run stops; the next run takes the task first and goes on with the same session, which is told to do again what it undid. Where the conversation is gone, as on another machine, a new session is told that the report of the one before is among the comments.

### Usage limits

Claude Code has two limits of usage, a five-hour one and a weekly one; a run knows how much of them is used from the status line of the session that works, read with every look at it, and between tasks from that of the last session; that a session ran into one it knows from its conversation. A window whose reset has passed has started anew since the status line told of it, so how much of it is used is not known: a run counts it as not spent, and the page shows nothing for it. A limit counts as spent at 99%.

- **Before a task.** No limit holds a task back: a task is started however much of either is used, and a session that runs into a limit is handled as one inside a task.
- **Inside a task.** A session that runs into a limit is stopped. When the five-hour limit is spent and the time of its reset is known, the run waits for the reset and a minute more; otherwise it waits for half an hour. Then the same session goes on with a message that says so. A task waits so ten times at most.
- **A pause.** When the weekly limit is spent, when the limit did not reset, when the session did not go on, and when the owner stops, aborts or kills the run during the wait, the task is paused: the session is stopped, the task gets the label of interrupted tasks and a comment, goes back to `Todo`, and its uncommitted changes stay in the working tree. The run ends. The next run takes the paused task first, over the changes, and goes on with the same session.

### Going on with a session

Whatever stops a run over a task, the next run goes on with the same session. A run writes down the task and its session in `.toobusy/local/session.json` as soon as the session is started, and forgets them when the task comes to an outcome: done, done in part, left to the owner, or failed.

```json
{"task":12,"session":"1a2b3c4d","conversation":"1a2b3c4d-0c7f-4efb-a0a0-211c72424c97","since":"2030-01-01T09:00:00.0000000Z","state":"working"}
```

| Key | Meaning |
|---|---|
| `task` | the number of the task |
| `session` | what Claude Code calls the session while it lives, as in `claude attach` |
| `conversation` | what the session is gone on with when nothing has it any more; it is learnt from the list of the sessions a moment after the start, and written when it is known |
| `since` | when the session was started or told something last: what it said before does not count |
| `state` | `working`, for a session that was started and of which nothing more is known; `paused`, for one that a usage limit stopped; `wrapped-up`, for one that wrapped its task up after `/abort` |

The record is local state and not a setting: a conversation lives on one machine. What every machine sees is the tracker, with the label of interrupted tasks and the report in a comment. The record is also the proof that it was a run that moved the task to `In Progress`, so the task it names is taken whatever its status on the board is and whatever it waits for.

**The order of the checks.** Before every task a run does this:

1. It reads the record and the working tree. In a project that names its branch, the working copy must be on it, with a record and without one. Changes in the working tree are those of the task of the record, and with no record, or with one of a session that wrapped up, which left none, they stop the run.
2. It reads the queue.
3. A task of the record that is not among the open tasks of the milestone any more, or that got the manual label or a blocking label since, or lost its label to take, is not taken; a label that the run skips does not count, as the skip is about the tasks a run takes anew, and a session that was started is finished: its session is stopped, the record is forgotten, the log says so, and the checks start again, without a record. `▲ #12 was left with a session by an earlier run, but it is not gone on with: it has the draft label · its session is stopped and forgotten`. A task that is not among the open ones is first read by its number, as the queue has only the open ones: one that is closed may be settled instead, as described under “A task of the record that is closed” below.
4. Otherwise the task of the record is the one that is taken, before those that were interrupted and before the rest.
5. The session is looked for, and the task goes on in the way the session was left, as the cases below say. The task is moved to `In Progress`, is assigned to the user who runs toobusy and loses the label of interrupted tasks only when its session goes on. The record says from then on that the session works, whatever it said before: the session was told to go on, and what it changes is its own. The branch of a task of the record is not brought up to date, as [The branch of the tasks](#the-branch-of-the-tasks) describes.

**The cases.** The log says how the task was taken: `→ #12 Export the data — taken again · its session was stopped, and goes on · claude attach 1a2b3c4d`.

| What the run finds | It does | The log says |
|---|---|---|
| the session still works, or waits for the owner: toobusy died, or its terminal was closed | takes the session over and watches it as its own: one that waits is told to go on alone after the same countdown, and one that is marked as waiting though its reply ends with a line for toobusy is told at once that no answer will come | `its session still works, and is watched again` |
| the session ended with a line for toobusy while nobody watched | settles the task as usual, the checks of the working copy among it | `its session ended while nobody watched` |
| the session was stopped: a kill, or a tracker that refused while the task was taken | tells it to go on, over the changes left in the working tree | `its session was stopped, and goes on` |
| the session was stopped by a usage limit: a pause, or a limit it ran into while nobody watched | tells it that the limit has reset | `its session goes on after the usage limit` |
| the session wrapped up after `/abort`: the record says so | tells it to go on: to do again what it undid, and what is left | `its session goes on after /abort` |
| nothing is left of the session: no process has it, and its conversation is not known or is not on this machine | starts a new session with the first message, with the skill of the project at its head again. After a wrap-up it is told that the task was interrupted and has a report; otherwise that a session worked on the task before, left no report and may have left changes | `a new session: the conversation of the one before is gone` |
| the sessions cannot be read | stops, with the record and the task as they are; the next run looks again | |
| the session does not go on when it is told to | stops in the same way; the next run tries again | |

A session that is stopped and goes on is told this as the next message of its conversation:

> toobusy was stopped while you worked, and it runs again: go on with task #12 from where you stopped. The uncommitted changes in the working copy are yours. The rules of this session hold as before: nobody will answer, and your last reply ends with a `TOOBUSY:` line.

One that wrapped up is told:

> toobusy runs again: go on with task #12. You wrapped it up when the owner stopped the run, and your report is the last comment of the task that starts with **Interrupted.**: do again what you undid, and do what is left. The rules of this session hold as before: commit and push the finished work as the instructions of the project say, and end your last reply with a `TOOBUSY:` line.

**A task of the record that is closed.** A commit of a session may close its task, as [How a task ends](#how-a-task-ends) describes, and a run may be killed after that push and before it settles the task. The next run finds the record, does not find the task among the open ones, and reads it by its number. What it does with a task that is closed depends on its session:

| The session | The run does | The log says |
|---|---|---|
| ended with a line for toobusy while nobody watched | settles the task as any other: the checks of the working copy, the report as a comment, the board. The task is not taken again: it is not moved to `In Progress`, nobody is assigned, and it is not closed once more. After another line than `done` and `partial`, and when a check does not pass, the task gets the comment only and is left closed | `→ #12 Export the data — taken again · it was closed since, and its session ended while nobody watched · claude attach 1a2b3c4d`, and for a task that is done `#12 was closed already, and has the report of the session as its comment` |
| did not come to such a line: it still works, waits for the owner, was stopped in the middle, or ran into a usage limit | stops the session and forgets the record, as before any task that is not gone on with. The uncommitted changes of the session stay in the working tree and stop the run at the next check | `▲ #12 was left with a session by an earlier run, but it is not gone on with: it is closed, and its session did not say how the task went · its session is stopped and forgotten` |
| is gone, with its conversation | forgets the record | `… it is closed, and nothing is left of its session · …` |
| is that of a record that says `paused` or `wrapped-up` | stops the session and forgets the record: such a session did not end with a line that is still to be settled, the report of a wrap-up being in the task already | `… it is closed · …` |
| cannot be read | stops, with the record and the task as they are; the next run looks again | |

A session that did not come to its line is not waited for and not told to go on over a closed task: a task that is closed in the middle of the work may have been closed by its owner, who does not want it done, and the run does not go on past that doubt. A task that is settled so has the command after a task run for it, counts as a task of `run --max`, and is what `run --task` with its number does. A tracker that does not say whether the task is closed stops the run, and the record is kept. A task of the record that is open and stands in another milestone now, and a number the tracker does not know as a task, are forgotten as before: `it is not among the open tasks`.

**What a run does not go on with.** A failure stays for the owner: when the session failed, did not say how the task went, or the check of the working copy did not pass, the record is forgotten as the run stops, and the task gets the manual label and a comment, as [How a task ends](#how-a-task-ends) describes. The label keeps it out of the next run, with a board or without one. A task that came to an outcome is forgotten before the tracker is told, so that nothing is told twice: when the tracker refuses then, the task is left to the owner in the same way. Only a task that was wrapped up keeps its record through that.

**A tracker that refuses while a task is taken.** A session works from the moment it is started or told to go on, and the tracker is told more after that: a task whose session goes on is moved to `In Progress`, the task is assigned, and a task that was interrupted loses its label. When the tracker refuses the status or the label then, the run stops, and it stops the session first, as a kill does, so that nothing works on with nobody watching: `✖ Stopped: … · the session of #12 is stopped, and the next run goes on with it · claude attach 1a2b3c4d · the next task did not start · 0 done`. The record is kept, the task stays as the tracker has it, and the next run goes on with that session as with one that a kill stopped. A kill that comes at that moment stops the session too: `■ Killed the session of #12 while the task was taken: the next run goes on with its session · claude attach 1a2b3c4d`.

On another machine there is no record: a task that was interrupted or paused is taken first by its label, with a new session. Its first message has the rule of a task that was interrupted, which names both comments such a task may have: the report of a wrap-up, to go on from, and the comment of a pause, after which the task is done from its description, the changes of the session before being on the machine it worked on. One that a kill left in `In Progress` is not taken, in a project with a board. A project without a board has no status to hold such a task: there it is taken as any other, with a new session.

### Local state

What a run leaves on the machine lies in `.toobusy/local/`: the record of the task a run has a session for, the sign of a run that goes on, the request that waits for a session, and what the status line of the last session told. The folder is made by the run and has a `.gitignore` of its own that leaves everything in it out, so that nothing of it is committed, the working tree stays clean, and the `.gitignore` of the project is not touched.

**The sign of a run.** A run writes down that it goes on, as `run.json`, when it starts, before it reads anything, and takes that away when it ends, however it ends: out of tasks, stopped, over a problem or killed. The sign has the number of the process of the run and when that process started, when the run started, and what the run is doing: where it is, the task it works on, with its title and its address, the command that opens the session of the task, and until when it waits for a usage limit. It is written anew when one of these changes, not with every step of a session, and put in place whole, as the record is.

A sign counts while its process lives. A run whose process was killed outright leaves its sign behind, and that sign says nothing: [`toobusy status`](#toobusy-status) says that no run is going on, and the next run puts its own sign over it. The number of a process is given to another one after a while, so a process counts as the run only when it started when the sign says its process did; a process whose start cannot be read counts. A sign that cannot be read says nothing either.

One run goes on in a working copy: [`toobusy run`](#toobusy-run) and `Run` of the menu do not start a second one. The sign is that of the working copy, so runs in two working copies of a project do not hold each other. A run of the menu that comes after another one of the same `toobusy` puts the sign up again.

### The machine

On macOS the machine is kept awake while a run lasts and no longer, so that back in the menu it may sleep, and the owner gets a notification when a session waits for an answer, when a task is paused, and when the run ends, unless it was killed. On other systems neither is done yet.

### The page of a run

A run is a tape on the terminal's own screen, under the bar of the page, which is printed once:

```
  toobusy · ~/Projects/rocket                                             Running the queue

 ✔ #101 Show the total of an order in its header  12:04
 ◐ #103 Fix the rounding of a discount  7:31

 ⣾ #102 Export the orders as a file
   1:24 · Edit src/Export/CsvExport.cs
   ▰▰▰▱▱
 ────────────────────────────────────────────────────────────
 ❯ press / to show the menu
 ────────────────────────────────────────────────────────────
 ctrl+c stop the session and exit                5-hour 41% · weekly 12%
```

- **What stays.** A task that is over is one line: the mark of how it went in its colour, `✔` done, `◐` done in part, `◇` waits for the owner, `■` interrupted or killed, `✖` failed, `‖` paused by a usage limit; its number and its title, `#71 Paint the windows yellow`, the number being what finds the task in the tracker; and, muted, the time it took. A title that does not fit gives way to the time, and the number is never the part that is cut. Nothing is said of a task that starts. What a run warns of, `▲`, stays as a line too. These lines are written for good: as they grow in number the bar scrolls up with them into the history of the terminal, and the terminal scrolls back to them.
- **The task that is worked on** stands under them, redrawn in its place. Before its title a mark turns in the accent: a cell full of dots with a gap that runs around it clockwise, `⣾ ⣷ ⣯ ⣟ ⡿ ⢿ ⣻ ⣽`, a turn in 0.64 seconds; without colours a `●` stands there still. After it stand the number and the title of the task, as they do on its line when it is over. Under the title, muted, stand the time the task has been worked on and what its session is doing; keeping its plan is not said there. A session that waits says so and when it is told to go on alone, a wait for a limit when it ends, a run that was told to stop that this is its last task, and one that was told to abort that the session is wrapping up. Between tasks the first line says what the run is doing.
- **The plan of the session** is a third line under these two, a bar with a cell for each step of the plan, `▰▰▰▱▱▱▱`: a step that is done is a full cell in the accent, a step the session is at blinks between the accent and the muted colour, and a step that is not begun is an empty cell, muted. The bar is the list as it is: when the session adds steps, takes some out or marks them, the bar is built anew, with as many cells as the list has. A session that keeps no plan has no such line. Without colours nothing blinks, and a step the session is at is an empty cell. The plan of a Claude Code session is the list of tasks it keeps with `TaskCreate` and `TaskUpdate`, read from its conversation.
- **A session that waits for the owner** shows, under these lines, what it said last, seven lines of it at most, as the width breaks them, and under them how its session is opened to answer it. When it goes on, there is the title, the time and the step again, and nothing else.
- **The commands** have their place between two rules. Nothing is typed there: the line says `press / to show the menu` after the pointer of a menu, `❯`, which blinks there as it does on the chosen line of the menu that opens in its place. `/` opens the menu of the commands that mean something at the moment, each with what it does; Up and Down move, Enter runs one, Escape closes the menu. They are `hold` and `send now` while a session waits for the owner, then `stop`, `continue` and `abort`. A session that waits for the owner brings `hold` and `send now` without being asked; Escape puts them away, and `/` opens the whole menu.
- **Under the second rule** stand the keys and, at the right edge, how much of the two limits is used, when it is known: the figures follow the session that works, and between tasks they are those it left. A window that is not known has nothing there, and with neither the edge is empty. After the first Ctrl+C the line says `press ctrl+c again to stop the session and exit`.
- **The end.** When the run is over, what was redrawn is erased, and under the tasks the tape says how the run went: `5 tasks in 1 h 12 min · 4 done · 1 done in part`, and under that, for a run that did not simply run out of tasks, what ended it, on as many lines as it takes: `‖ Stopped, as you asked`, `‖ Stopped after 3 tasks, as you asked` or `‖ Stopped after #12, as you asked` for a run that was started for so much, `✖ Stopped: #12 left changes in the working tree · claude attach 1a2b3c4d`, or `‖ Stopped: #71 is paused: the weekly limit is spent. Its changes stay in the working tree, and the next run goes on with its session`. The page does not wait to be left: a run that was opened from the menu goes back to it, with this said above the menu, and `toobusy run` leaves the tool.

No line of a tape wraps: lines are cut a column short of the window, and the terminal is told not to wrap while the tape is unrolled. What is redrawn is never taller than the window: what a session said gives way first. When the window changes its size the whole tape is drawn anew to the new one: the terminal is erased, its history too, and the bar, every line that stays and what is redrawn under them are written again, a title or a warning laid out to the new width. What the terminal had in its history before the run is gone with that. A terminal that jumps to its last line whenever something is written does not let the history be read while a task is worked on: what is redrawn is drawn anew once in a second, and in a terminal with colours the mark that turns and what blinks are written with every beat as well, eighty milliseconds apart.

Without a terminal the lines of the log are printed as they come: the run, each task that starts and how it ends, what is done to the tracker, every wait and every command, each with its mark: `✻` the run, `→` a task that is started, `▶` goes on, and the marks above. The first line, of the run, names the milestone, the tasks in the queue, the labels it skips, when it skips any, the model, the effort and the permission mode its sessions work in: `✻ v0.2.0 · 8 tasks in the queue · opus · high effort · auto mode`. A line that ends a run or pauses a task counts the tasks the run closed, and so does the notification of a run that ran out of tasks, was stopped by `/stop` or did what it was started for: those that are done, `3 done`, and apart from them those that are done in part, when there are any, `✔ No task is left · 5 done · 1 done in part`.

### A demo of a run

`run --demo` is the real run over a made-up tracker and made-up sessions; nothing is changed anywhere, no command of the project is run, no branch is checked, and the waits are seconds long. It puts up no sign and looks at none: a demo works on nothing, so it goes on beside a run in the same working copy, a run starts beside it, and `toobusy status` does not tell it. Where the settings are made up they name the skill `task-auto`, and the log says of every new session that it starts with it. The tasks fit the rules of the project, and one after another they show: a task that a run was killed over, which stands in `In Progress` with a made-up record, and whose session goes on; a task that is done; a session that stops with a question, the countdown, the message toobusy sends and the session deciding alone; a task done in part, whose session makes a task of what is left, which is held for the owner when the run ends, and which Claude Code marks as waiting after its report, so that the session is told that no answer will come and the task ends at its next reply; a session that runs into the five-hour limit, the wait and the same session going on; a task that opens after another; and tasks that are held by a label and by their status. The commands of the page work as they do in a run. The menu of a demo opens with that task named at `Run`, `interrupted: #100 Rename the settings page`, as the task of a record is. A demo that is aborted remembers it for as long as the demo goes on, and for no longer, and nowhere: the menu it comes back to says `interrupted: #101 Show the total of an order in its header`, and the next run takes that task first, goes on with its session, and then takes the rest of the same tasks. After that run the made-up tasks start over, as they do after any run that leaves no task interrupted and no record; the task a run was killed over is among the first of them only. Without a terminal the log of a demo starts with `Demo: nothing is changed.`

`run --demo` takes `--task`, `--max` and `--skip-label` as a run does, and the menu of a demo has `Skip labels` over its made-up labels. The tasks of a demo are #100 to #107. A demo of one task has no task that a run was killed over, whose record would keep the run from any other, unless that task, #100, is the one asked for: `run --demo --task 101` does #101, `--task 105` says that it waits for #102, and `--task 100` goes on with the session of #100.

### Tests

- The queue: every reason a task is held for, the order, tasks that wait for others, the interrupted task a run takes first, and the task of a record, which comes before it. The labels a run skips: a task with one, in any case, and the tasks that wait for it, a reason that comes before the skip, a label that no task has, and the task of a record, which is gone on with whatever its labels. The statuses of a board by the names of the settings: which status a name of the board is, what a status is called, and what a board lacks; a queue over a board that calls them otherwise, where a task in a status the board calls `Todo` is held. `toobusy queue` through the command line: each list and each reason, the task of a record, `--json`, an empty queue, the colours, a demo, what is missing, and that nothing but the tasks is asked for and nothing is left on the machine.
- The line for toobusy in a reply, which is read without a block of what is left, whether a reply ends with it, and the tasks a report names; the messages a session gets, the first one with what a new task needs and with the sentence of the push, as the project and the task are, with the command of a skill at its head and without one; and what `about` tells: the same messages word for word, the labels, the skill and the permission mode of the project, what is the skill's to say, and the times and the counts of the policy.
- The run, with hand-written fakes of the tracker, the assistant, the working copy, the machine and the clock: a task after a task, every outcome, a task done in part, for which the run makes nothing, and one whose report names no other task; a session that asks and is told, the limits of that, every command; a session marked as waiting whose reply ends with the line, which is told once that no answer will come and is over at its next reply, one that asks after the line, which waits as any other, and one that was told after a question and ends with the line later, which is over at once, while the run aborts, with a usage limit in the reply, and a session of an earlier run that is found so; a kill, the limits of usage, read with every look at a session and on the page as they change, a window whose reset has passed, which is not on the page and does not pause a task, a pause and the run after it; the record, written at the start of a session, when its conversation is known and at an outcome, and each case of the run that finds one: a session that still works, one that ended, one that was stopped, one that wrapped up, one whose conversation is gone, a task that is not taken any more, and a failure; a task that a commit of its session closed, which is settled all the same, and one that is not done, which gets its report and neither a label nor a status; a closed task of the record: one whose session ended with each line, which is settled without being taken again, one whose session did not come to a line, is gone or was paused or wrapped up, which is forgotten, and the sessions and the tracker that cannot be read; a tracker that refuses, and a kill, while a task is taken, after its session was started or told to go on, with the session stopped and the next run going on with it; a task that a run stopped over, which gets the manual label and a comment and is not taken by the next run in a project without a board, and a tracker that refuses either; the assignment of a task that is taken, with a board and without one, and a tracker that refuses it, which the run goes on past; a run of one task, with each outcome, a task that is held, one that waits, a number that is not a task, the record of another task and its own, and a task that its session makes; a run of a number of tasks, what counts, a task that is gone on with, a task that a session makes, a queue that runs out first and a `/stop` before the count; a run that skips labels: its first line, the tasks it holds, one with such a label that a session makes, a run of one task over a task it skips, and the task of a record with such a label; the commands of the project, with a fake of the shell: a run without them, the two around a task and what they are told, a `before` that fails, is given up or does not start, `after` after every outcome and after a session that did not start, an `after` that fails, a kill before and inside a task, a task that is gone on with, and a wait for a usage limit; the branch of the tasks, with a fake of the working copy: the right branch, another one and none, a branch that is moved forward, one that is level, one with commits its upstream lacks, one that parted from it, a remote that does not answer, a branch without an upstream, said once, a task that is gone on with and a new session for it, which are checked and not brought up to date, a project that names no branch, and the order with the command before a task; the sign of the run, with a fake of it: put up, changed with what the run does and not with every look, taken down at the end and after a kill, and a run that finds the sign of another and starts nothing; a tracker that does not answer: each call that is made again, after a failure once, with the line of the log in the words of the call, and after three, when the run stops with the words of the last failure, a failure that stays, told at once, a comment that is not written again, a kill during a pause, while a task is taken too, nothing made again after a kill, and a remote that does not answer once and three times, with the page in the pauses.
- Which answers of `gh` are failures that may pass and which stay, by the status of the process and the words of the answer: the HTTP status, a rate limit, a connection that is not made; and the same of `git fetch`, by what git and ssh say.
- What is asked of `gh` to move a task to a status the settings name, and what the board is told when it has no status of that name; the first message, the log of a run and `about` with the statuses named as the settings name them.
- What is asked of `gh` and of `claude`, argument by argument, a message that begins with the command of a skill among it, a session started and gone on with in the permission mode of the settings and in `auto` where they name none, and what is made of their answers; a closed task that is asked for by its number, with a board and without one, and a number that is open or is no task; a conversation of Claude Code read from a file of the test, with a reply that ends with the line for toobusy, one with a question after it and one after which the session asks with a tool; a session of an earlier run that is found, lost or gone; the file of the record; the file of the sign, with the processes of the machine faked and real: a run that goes on, one that ended, one that died, a process that got its number, a second run, and a file that cannot be read; a command of the project through the real shell: its folder, its environment, its exit code and what it printed, one that does not end in its time, one that is cut, and one that leaves something running; what is asked of `git` to read the working copy and to bring its branch up to date, and the same through the real `git`, in repositories of the test with a remote and another clone that pushes to it: a branch that is level, one that is moved forward, one with commits of its own, one that parted from its upstream, one without an upstream, a remote that is not there, and a detached HEAD.
- The tape of a run over a run that the test writes: what stays and what is redrawn, the menu, the commands a waiting session brings, the limits at the right edge as they change, with a window that is not known and with none, a kill, a short and a narrow window. A demo from its first line to its last through the command line, with a clock that makes nobody wait: as a log without a terminal, and as a tape in one; and a demo that is aborted, whose menu names the interrupted task and whose next run takes it first; the menu over a record. `run --task` and `run --max` through the command line: the two together and a number that is not positive, a demo of each, a task that is not taken, the tape of a counted run, and a number that is not a task of a project, for which no session is started; and a demo in a project whose settings have commands, which runs none. `run --skip-label` with the option given again and with several names, with the task of a record that is gone on with, and with `--task` over a task it skips; `queue --skip-label` as text and as JSON; the help of `run` and of `queue`. `Skip labels` of the menu: the row, the labels it offers, the choice, the line above the menu, the count at `Run` and the run it opens, Escape, the labels that cannot be read, and a demo. A second run that does not start, from the command line and from the menu; a run that `status` tells while it goes on and not after it; and a demo that a run does not hold.
