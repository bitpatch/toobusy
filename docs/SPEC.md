# Specification

This is the detailed description of what toobusy does. It grows one part at a time; [IDEA.md](IDEA.md) says what the tool is meant to be, and where the two disagree, this file wins. It has two parts. [Setting a project up](#setting-a-project-up) covers the settings file, `toobusy init`, the milestone of the user, the model and the effort the tasks are done with, the page `toobusy` opens with, `toobusy doctor`, and what the tool says in a project that is not set up. [Running the tasks](#running-the-tasks) covers `toobusy run`: the queue, the sessions of the assistant and what they are told, what a run does to the tracker, the limits of usage, and the page of a run. `doctor` and the options of `init` that answer without questions are described but not built.

## Setting a project up

### Scope

In scope:

- the settings file in `.toobusy/`: its format, its first sections, reading and validating it;
- `toobusy init`: an interactive setup, and the same setup driven by options alone;
- the milestone to work on: the choice of the user, kept on their machine, and `toobusy milestone`;
- the model and the effort the tasks are done with by default: choices of the user too, `toobusy model` and `toobusy effort`;
- `toobusy` without a command: the page with the menu;
- `toobusy doctor`: the check that everything a run needs is installed and logged in;
- the message of `toobusy` and `toobusy run` in a project that is not set up.

Out of scope, each a later part of this specification:

- the names of the board's statuses: a run knows the three GitHub gives a new board;
- the messages a session gets as templates of the project: they are texts of the tool;
- the assistant's permissions;
- choosing an assistant per task, for example by a label;
- the branch of the working copy and its sync with the remote before a task, and commands of the project around a task.

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
owner = "manual"                            # put on a task that waits for the owner
interrupted = "interrupted"                 # put on a task that a run had to stop

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
| `queue.labels.owner` | the label a run puts on a task that waits for the owner; a task that has it is never taken | a label name; not one of `take`; may be one of `blocking` |
| `queue.labels.interrupted` | the label a run puts on a task it had to stop; such a task is taken first | a label name; not one of `take` or `blocking`, and not the label of `owner` |
| `assistant.type` | the assistant adapter | `claude-code` only |

The settings do not name the repository. The tasks are the issues of the repository the project is cloned from: the GitHub repository of the `origin` remote. A project whose `origin` is not a GitHub repository has no tasks to take.

An unknown key, a missing required key and a value of the wrong type are errors that name the file, the key and the line. Validation of the file never calls the tracker: whether the board and the labels exist is `doctor`'s business.

The file that `init` writes carries a short comment over each section. `init` on an existing file changes the values it asks about and keeps every other key and comment as it is.

#### The milestone to work on

Only the tasks of one milestone are taken, and the user says which: one of the open milestones of the repository, or none, and then tasks are taken whatever their milestone. toobusy never chooses a milestone on its own.

The choice is personal. It is not a setting of the project and is not committed: it is kept on the machine of the user, for each project apart, in `projects.toml` of the user's own toobusy folder. That folder is `$XDG_CONFIG_HOME/toobusy` when the variable is set, `%APPDATA%\toobusy` on Windows, and `~/.config/toobusy` otherwise.

```toml
["/Users/ann/Projects/rocket"]
milestone = "v0.3.0"    # an empty title means working without a milestone
model = "opus"          # an empty name means the assistant's own model
effort = "high"
```

A project is known by the path of its root, so a second clone and a worktree have choices of their own. The milestone is kept by its title. The file holds every personal choice of a project, the model and the effort among them, each under its own key; a key that is not there is a choice that is not made. toobusy writes the file whole; a file it cannot read counts as one that says nothing.

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

- **The weekly limit** is how much of the weekly limit of usage a run may use, in percent: at that much of it a run takes no next task. The list offers 50%, 60%, 70%, 80%, 90% and 96%. Nothing has to be chosen: a user who has not chosen has 96%. It is kept in the same file, as a number, `limit = 80`; a number that is not a share of anything counts as one that is not chosen.

A session of the assistant is started with the model and the effort.

### Not set up

In a git repository without `.toobusy/settings.toml`, `toobusy run` (with any options) and, without a terminal, `toobusy` print to the error stream and exit with code 2:

```
toobusy: this project is not set up yet.
Run `toobusy init` to set it up.
```

The message is coloured: `toobusy:` in the muted colour, the command between backticks in the accent, the rest in the terminal's own colour. The text is the same without colour.

In a terminal `toobusy` without a command does not stop there: it opens its page and sets the project up, as described below. `--help` and `--version` work everywhere.

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

 ●──◉──○                                                  the steps, on grey
```

- The bar names the tool and the folder of the project, shown from `~` when it is under the home folder, and says at its right what the command is doing. It is three lines on a grey ground: an empty one, the text, an empty one.
- The question, the choice and the keys are three parts with a rule between them. The question is its name and a line that says what to do. The choice is the only part the user acts in, and what waits for the user there blinks, smoothly, once in a second and a half. The rule is the same on every screen:
  - On the line that is chosen, the one the pointer `❯` stands on, the pointer and the name blink: they glow in the accent and fade to the colour of any text, never to a grey. What explains the name at its right does not blink. The pointer stays where it is, and the line is read as well at every moment.
  - Where a text is typed there is a cursor of the classic shape, a block in the accent with the character it stands on drawn over it, that fades away to nothing and comes back. A line where a text is typed has no pointer.
  - A screen may have both, as the list of the boards has under its filter.
  - The blink keeps its time: a key or a change of the chosen line does not start it again.
  - The cursor of the terminal is hidden, because whether it blinks is up to the terminal. A terminal of sixteen colours has the two ends of the blink without the fade, and without colours nothing blinks: the terminal's own cursor stands in the text or on the pointer.
- While toobusy reads something from the tracker, the choice says what it reads and, under it, shows seven dots that a light runs along, from the first to the last and back, a second each way: the dot it is on is in the accent, and those it has left fade behind it to the muted colour of the rest. A terminal of sixteen colours has the lit dot without the fade, and without colours the dots stand still. Nobody has to wait: the keys work while toobusy reads. Where there is somewhere to go back to, `Back` stands under the dots from the first moment, and Enter or Escape goes back at once; where Escape leaves the page it leaves it from here too, asked twice, and so does Ctrl+C twice. What was being read is told to stop and is not waited for, so a tracker that never answers holds nobody. Where what is read belongs to one row of a list, the dots stand on that row, after its name, and the rest of the list is used meanwhile.
- The keys are hints of the form `enter choose`: the name of each key is a little lighter than what it does, so that the eye finds it.
- Choices stand one under another, never side by side.
- The body between the bar and the question gives way when the window is short, its oldest lines first.
- The last line of the setup, after an empty one, shows its steps on the same grey as the bar, as marks joined by lines and without their names: `●` for those that are done, `◉` for the one that is asked, `○` for those to come. The confirmation is not a step: when it is asked, every mark is done. Marks that do not fit go on to the next line.
- No line is longer than the window is wide: what does not fit is cut with `…`. The screen is drawn again when the size of the window changes.

**Leaving.** Ctrl+C leaves the screen, but only when it is pressed twice in a row: after the first one the line of the keys says `press ctrl+c again to exit`, and any other key takes the question back.

Without a terminal there is no screen: `init` fails, as described below, and the other commands print plain lines.

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

`init` asks what it needs, shows what it is about to do, and does it when the user saves: it writes `.toobusy/settings.toml` and, when the user asked for that, makes a GitHub project and links the project of the tasks to the repository. Until then it changes nothing, and it never touches the working copy or git.

The steps, in order:

1. **Environment.** Whether `gh` is installed and logged in to github.com, and whether the `origin` remote is a GitHub repository; the other checks of `doctor` come with `doctor`. Failed checks are shown with their fixes and do not stop the setup; when all pass, nothing is shown and the setup starts with the first question. Without a working `gh`, or when the `origin` remote is not a GitHub repository, the steps below cannot read the tracker: they accept typed values and say that nothing was verified.
2. **Tracker.** GitHub is the only one; it is shown, not asked.
3. **Project.** The GitHub Projects board of the tasks, optional. A project that has one already, the board of the settings or one that is linked to the repository, shows it as the first of two choices, as it stands in the list of the boards, with `Choose another project` under it; one Enter keeps it. That choice, and a project without a board, open four ways to answer, as tabs that Tab goes through: one of the boards the user can reach, the address of a board, a new board with its owner and title, or no board. The address of any page of a project is taken as its board. A chosen board is checked for access and for the token scope it needs.
4. **Blocking labels.** A multiple choice over the repository's labels; nothing is chosen at first.
5. **Labels to take.** A multiple choice over the remaining labels; nothing chosen means any task.
6. **Owner's label.** The label a run puts on a task that cannot go on without the owner: one of the labels of the repository that are not labels to take, or `New label…`, the last of the list, where a name is typed. A first setup proposes `needs-owner`: the label of that name when the repository has it, a new one otherwise.
7. **Interrupt label.** The label a run puts on a task it had to stop, chosen in the same way among the labels that neither block nor let a task in and are not the owner's. A first setup proposes `interrupted`.
8. **Assistant.** Claude Code is the only one; it is shown, not asked.
9. **The last page.** What saving will do, in the words of the questions and not as the text of the file, and under it the question with three choices: `Save and exit`, `Exit without saving` and `Back`. A first setup shows the answers and says that they will be written. A setup that exists says what changes in it, each setting as it was and as it will be: `Project: Rocket → none`. Under it stand the things to be done on GitHub: a new board to make, a board to link to the repository, a label to make. A board that is not linked to the repository yet is linked; one that is linked is left alone, and no link of another board is ever taken away. A label of the two that the repository does not have is made: `The label “needs-owner” will be made in acme/rocket.` `Exit without saving` does nothing and exits with code 1. On `Save and exit` the board is made, the board is linked, the labels are made and the file is written, in that order. When GitHub refuses any of it, the file is not written, and `init` says what was refused, naming the board when it was made already, and exits with code 1. When there is nothing to write and nothing to do on GitHub, `init` has no last page, says `Nothing to change: the settings already say this.` and exits with code 0.

**Going back.** Escape goes back from every question to the one before, which proposes what was answered there. Escape from the first question leaves the setup with nothing changed and exit code 1, as leaving with Ctrl+C does, and it is asked about in the same way: the keys say `esc exit` there, the first Escape gives `press esc again to exit`, and the second one leaves.

After writing, `init` says that the file is to be committed.

**An existing setup.** When the settings exist, `init` runs the same steps with the current values proposed, so pressing Enter through it changes nothing. A settings file that does not validate is reported with its errors, and `init` proposes whatever it could read.

**Without questions.** This part is not built yet. Every answer has an option, and `--yes` accepts the proposed value of every question the options leave open, the confirmation included:

| Option | Answer |
|---|---|
| `--board <URL>`, `--no-board` | the board, or none |
| `--blocking-label <name>` | a blocking label; repeatable |
| `--take-label <name>` | a label to take; repeatable |
| `--yes` | ask nothing |

Without `--yes` the options are the proposed answers of the interactive setup. With `--yes` a value that fails its check — a board that cannot be reached, a label the repository does not have — is an error with exit code 1, and nothing is written. Without a terminal and without `--yes`, `init` fails and names the option.

**A demo.** `--demo` is an option of every command: it shows the tool over made-up data and changes nothing. What it reads is real where reading changes nothing: the `origin` remote and, through `gh`, the boards of the user and the open milestones of the repository. When there are fewer than ten boards, made-up ones follow the real ones, so that there is a list to scroll; the owners a new project can be made for and the milestones are filled up to ten in the same way. The rest is imitated: the labels are made up, the settings that exist are read, nothing is made, linked or written, and the settings, the milestone, the model and the effort that are chosen are remembered only until the demo ends. `toobusy --demo` and `run --demo` show a project that is ready, whatever the project is: where there are no settings they are made up, the first of the milestones is the chosen one, the model is the assistant's own and the effort is `high`, so that the menu opens at once; the setup and the choices are tried from the menu, or with `init --demo`, `milestone --demo`, `model --demo` and `effort --demo`. A run of a demo is the real run over made-up tasks and made-up sessions, described with [the page of a run](#a-demo-of-a-run). The bar of the screen says `demo`. It is there to try the tool and to see what it looks like.

**The questions.** Every question has the shape the screen gives it: its name and a line that says what it is about and what to do, then the choices or the text, then the keys it understands. A refused answer stays in its question, and the reason takes the place of the line under the name.

- **`Back`.** Wherever Escape goes back and does not leave the page, a list ends with a row `Back` that does what Escape does: in the lists the menu opens and in the steps of a setup opened from it, in a selection, a multiple choice, a confirmation and the lists of the boards. Where Escape would leave the page there is no `Back`: in the menu, which has `Exit`, in the first question of `init`, on the first run and in `toobusy milestone`, `model` and `effort`. A page that waits for the tracker has it under its dots. A question that is answered with a text has no list to end with it, and a list of the boards that a filter has emptied has none either; Escape goes back from them. The row names its key at its right, `Back  esc`, and the line of the keys does not say `esc back` then: it says so only where there is no such row. Where Escape leaves the page, the keys go on saying `esc exit`.
- A selection moves with Up and Down, a multiple choice marks with the space bar, and the last question of a setup is `Save and exit` over `Exit without saving`. A list that does not fit shows eight rows, five for the boards, and says above and below how many rows are beyond them: `↑ 2 more`, `↓ 4 more`.
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

Every tab stands on the grey of the bar, its name a little quieter than a plain text, and the chosen one is white on the deeper accent; without colours the chosen one is in brackets. The place under the tabs is as tall as the list of the boards, whatever tab is open, so that nothing moves when the tab changes: a list starts at its top, and the line where a text is typed, with what is said about it, stands at its bottom, with empty lines between.

- **Your projects** lists the open boards of the user and of their organisations, those linked to the repository first, each by its title with its address beside it. Typing narrows the list, the best fit first: the board of that address, a title that starts with the text, a title that has it inside, an address that has it, as the name of the owner is. The boards come from one request through `gh` with a timeout of three seconds; when `gh` is missing, not logged in, lacks the scope to read projects or is too slow, this tab is not there.
- **By URL** takes the address of a board or of any page of it.
- **New project** asks whose the board will be and for its title. The owners, the user and their organisations, are a list at the top of the tab, as the boards are in theirs: Up and Down move the pointer, which starts on the owner of the repository. The title is typed in the line at the bottom. The board is made only when the setup is confirmed. The tab is not there when `gh` gave no owners.
- **No project** leaves the setting out.

Escape from these tabs comes back to those two choices when the project has a board, and goes back a step when it has none.

**The report.** When the screen is closed, the terminal gets the name of the command and the folder, the answers as `✔ Blocking labels  manual, draft` lines, and one line that says how the setup ended.

The setup is written against an interface for asking questions, so that tests answer them from a script and the terminal implementation can change without touching the steps.

### `toobusy`

In a terminal `toobusy` without a command opens its page and goes on from wherever the project is:

1. **A project that is not set up** is set up: the steps of `init`, on the same page. Leaving them, or declining at the end, leaves the page.
2. **A user without a milestone to work on**, one who has not chosen or whose milestone is not open any more, chooses one from the list. Leaving the list leaves the page.
3. **A user who has not chosen the model or the effort** chooses them, the model first. Leaving either list leaves the page.
4. **The menu.** Above it stand the settings, the milestone, the model, the effort and the weekly limit as they are; the milestone stands again, in the colour of success, after the choice that changes it.

```
 ✔ Project          https://github.com/orgs/bitpatch/projects/4
 ✔ Blocking labels  manual, draft
 ✔ Labels to take   any task
 ✔ Owner's label    manual
 ✔ Interrupt label  interrupted
 ✔ Milestone        v0.3.0 · due 2030-01-15 · 12 open tasks
 ✔ Model            opus
 ✔ Effort           high
 ✔ Weekly limit     96%

 What to do
 ────────────────────────────────────────────────────────────
 ❯ Run        5 tasks
   Assistant  opus · high
   Milestone  v0.3.0
   Settings
   Exit
 ────────────────────────────────────────────────────────────
 ↑↓ move · enter choose · esc exit · ctrl+c exit
```

| Choice | Opens |
|---|---|
| `Run` | the run, on its tape; when it is over the menu is back by itself, with how the run went said above it |
| `Assistant` | the model, the effort and the weekly limit, described below; Escape goes back to the menu |
| `Milestone` | the list of the milestones; Escape goes back to the menu with the milestone as it was |
| `Settings` | the steps of `init` with the current values proposed; Escape from the first of them goes back to the menu, and so does the end of the setup, whose last line is shown above the menu |
| `Exit` | nothing: it leaves the page, as Escape twice and Ctrl+C twice do |

After `Run` stands the number of tasks a run would take as things are: those that are ready and those that open after them. It is counted when the menu opens, and again after a run, after a change of the milestone and after the settings. While the tracker is read, the dots of a wait run in its place, the row is muted, and Enter does nothing on it. A run that would take no task is not opened either: the row says `no tasks`, and Enter counts again, for what has changed meanwhile. When the tasks cannot be read the row says so, and the run is opened all the same: it says why it stops.

After `Assistant` stand the model and the effort that are chosen; the assistant's own model is `own model` there. It opens a list of the three choices of the user, each with what is chosen after its name:

```
 Assistant
 ────────────────────────────────────────────────────────────
 ❯ Model         opus
   Effort        high
   Weekly limit  96%
   Back          esc
 ────────────────────────────────────────────────────────────
 ↑↓ move · enter choose · ctrl+c exit
```

| Choice | Opens |
|---|---|
| `Model` | the list of the models; Escape goes back with the model as it was |
| `Effort` | the list of the levels; Escape goes back with the effort as it was |
| `Weekly limit` | the list of the shares, the pointer on the one in force; Escape goes back with the limit as it was |

The three lists end with `Back`, as every list does that there is somewhere to go back from.

The answers of `Settings` are written to the settings of the project and are to be committed; the milestone, the model, the effort and the weekly limit are kept on the machine of the user.

When the page is closed, the terminal gets the name of the tool and the folder, the report of a setup that was gone through, and the milestone, the model, the effort and the weekly limit when they were chosen. A run has left its tape in the terminal already, with its tasks and how they went; after a run with nothing else to report, nothing more is printed. The exit code is 0, and 1 when a setup failed on GitHub; after a run it is the exit code of the last one.

Without a terminal there is nobody to ask: `toobusy` prints its help in a project that is set up and the "not set up" message in one that is not.

### `toobusy milestone`

`milestone` chooses the milestone to work on, or shows the one that is chosen. It needs no settings: only a project.

| Command | Does |
|---|---|
| `toobusy milestone <title>` | chooses the open milestone with that title and prints it with its due date and its open tasks. The title is taken whatever the case of its letters, unless two milestones differ only in it |
| `toobusy milestone --none` | chooses to work without a milestone |
| `toobusy milestone` in a terminal | opens the list of the milestones on a page; Escape leaves the choice as it was |
| `toobusy milestone` without a terminal | prints the choice: `Milestone: v0.3.0`, `Milestone: no milestone` or `Milestone: not chosen`, and after a milestone that is not open any more, `(not open any more)` |

A title that no open milestone has fails with exit code 1 and names the open ones:

```
toobusy: there is no open milestone “v0.1.0”.
The open ones: v0.2.0, v0.3.0
```

It fails in the same way when the milestones cannot be read. A title together with `--none` is a wrong command line.

### `toobusy model` and `toobusy effort`

`model` and `effort` choose the model and the effort the tasks are done with by default, or show what is chosen. Like `milestone`, they need no settings: only a project.

| Command | Does |
|---|---|
| `toobusy model <name>` | chooses the model with that name, as it is written, and prints `Model: <name>`. An empty name fails with exit code 1 |
| `toobusy model --default` | chooses the assistant's own model |
| `toobusy model` in a terminal | opens the list of the models on a page; Escape leaves the choice as it was |
| `toobusy model` without a terminal | prints the choice: `Model: opus`, `Model: the assistant's own` or `Model: not chosen` |
| `toobusy effort <level>` | chooses the level, whatever the case of its letters, and prints `Effort: <level>` |
| `toobusy effort` in a terminal | opens the list of the levels on a page; Escape leaves the choice as it was |
| `toobusy effort` without a terminal | prints the choice: `Effort: high` or `Effort: not chosen` |

A name together with `--default` is a wrong command line. A level that the assistant does not have fails with exit code 1 and names the levels:

```
toobusy: there is no effort “ultra”.
The levels: low, medium, high, xhigh, max
```

### `toobusy run`

`run` takes the tasks of the queue one after another, as [Running the tasks](#running-the-tasks) describes.

`run` does not start without a milestone to work on. When the user has not chosen one, or the chosen one is not open any more, it exits with code 1:

```
toobusy: the milestone to work on is not chosen.
Run `toobusy milestone` to choose it.
```

Nor does it start before the model and the effort are chosen. The first that is missing is named, in the same way and with the same exit code:

```
toobusy: the model is not chosen.
Run `toobusy model` to choose it.
```

A project whose `origin` remote is not a GitHub repository has no tasks to take: `run` says so and exits with code 1.

In a terminal `run` shows the run on its tape, and leaves the tool when the run is over: no menu is opened, and no key is waited for. Without a terminal it prints the log of the run line by line, takes no commands, and stopping the tool kills the run.

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

**Use by other commands.** `init` runs the checks that need no settings and goes on whatever they say. `run` will run all of them before anything else and, when one fails, print the failures and exit with code 1 without starting; until `doctor` is built it starts without them, and what is missing stops it where it is needed.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | done |
| 1 | failed: a check did not pass, a value was refused, the setup was declined or left, the milestone to work on, the model or the effort is not chosen, a run stopped because something was wrong |
| 2 | there is no project, the project is not set up, or the command line is wrong |
| 130 | a run was killed while a session worked |

### Tests

- The settings: reading, every validation rule, writing, and that a rewrite keeps unknown comments.
- The choice of the milestone against made-up lists of open milestones, the order of the milestones, and the file the choices are kept in: the milestone, the model, the effort and the weekly limit, each apart.
- What is asked of `gh`, argument by argument, and what is made of its answers and its failures, with the commands of the machine faked.
- The setup steps, with the questions answered from a script and the tracker and the environment faked: a first setup, an existing setup, going back, a new board, a missing `gh`, every option, `--yes`, no terminal.
- The screens, through a terminal of a test: scripted keys, a size, and the frames that were drawn. A tape is read as the rows the terminal's own screen has after it, sequence by sequence, so that a tape that has lost count of its lines fails. A key that a test holds back for something that never happens fails the test instead of letting the page go on for ever.
- `doctor`: each check passed, failed and skipped, and the fix for each platform with and without its package manager.
- The "not set up" message and the exit codes, through the command line.

### To be settled while building

- **How to tell that Claude Code is logged in** without starting a session.

## Running the tasks

`toobusy run` takes the tasks of the project one after another and has the assistant do each of them in the working copy, in a session of its own. It goes on until no task is left, until the owner stops it, or until something happens that it does not go on past.

### Who does what

- **The run keeps the tracker.** It moves a task before its session starts and after it ends, writes the report of the session into the task, puts and takes off labels, and makes new tasks. The assistant changes nothing in the tracker, and it is told so.
- **The assistant does the work** as the project's own instructions say (`CLAUDE.md`, `AGENTS.md` and the like): it changes the working copy, checks the change, commits it and pushes it, and ends its last reply with one line that says how the task went.
- **Nothing is taught to the assistant beforehand.** What a session has to know of the run it is told in its first message. No skills of Claude Code are needed, and another assistant is told the same.

### The queue

The tasks are the open issues of the repository of the `origin` remote that belong to the milestone of the user, or all of them when the user works without a milestone. A run reads them anew before every task. More than a hundred open tasks are an error that says how many there are.

A task is **held**, and no run takes it as things are, when one of these is so, in this order:

| Reason | The run says |
|---|---|
| it has a blocking label | `it has the manual label` |
| it has the label of the owner | `it waits for the owner: it has the needs-owner label` |
| there are labels to take and it has none of them | `it has none of the labels to take: feature, bug` |
| the project has a board, and the task does not stand there as one to do | `its status is In Progress`, `it is not on the board` |

In a project with a board a task is taken only from the status `Todo`. The three statuses a run knows are those GitHub gives a new board, `Todo`, `In Progress` and `Done`, whatever the case of their letters. A project without a board has no statuses, and nothing of them is asked.

A task **waits** for the open tasks that block it and for its own open sub-issues. It opens when they are closed, and is held while one of them is held or is not among the tasks that were read: `it waits for #7 (it has the manual label)`.

The tasks that can be taken now are taken in this order: those with the label of interrupted tasks first, then by their numbers.

### A task

1. **The working tree** must be clean: no changed file and none that git does not know. Otherwise the run stops with `the working tree is not clean: commit or stash the changes first`.
2. **The queue** is read, and the first task that can be taken is the one.
3. **The limits** of usage are looked at, as described below.
4. **The task is moved to `In Progress`** on the board, and its description and its comments are read.
5. **A session is started** with the model and the effort of the user. A task that was interrupted loses its label then.
6. **The session is watched** until its turn ends with a line for toobusy.
7. **The outcome is checked** against the working copy, and the tracker is told.

A session of Claude Code is a background one, started with `claude --bg`: it goes on by itself, the owner can open it with `claude attach <id>`, and it is kept when the run ends. Every line of the run that speaks of a session names that command. The session is started in the permission mode `auto`, with nobody to answer its permission prompts, and works in the working copy itself, without a worktree. Claude Code must trust the folder: where it does not, the run stops and says to run `claude` there once.

### What a session is told

The first message of a session gives the task and the rules, in this order:

- the number, the title and the address of the task, its description, and its comments, the last thirty of them;
- nobody will answer: no questions, no waiting for an approval, no plan mode. Where the instructions of the project say to ask the owner, the session does not: the owner started the run to have the task done;
- the instructions of the project hold in everything else, committing and pushing among it;
- the tracker is kept by toobusy, which has already moved the task to `In Progress`: the status, the labels, closing and comments are not the session's;
- the work is done in this working copy, which was clean and must be clean at the end: everything committed and pushed, or undone;
- a task that was interrupted before has the report of that session among its comments;
- what cannot be done without the owner, as described below;
- the last reply is the report of the task, and its last line is one of:

| Line | Means |
|---|---|
| `TOOBUSY: done` | the task is done, committed and pushed |
| `TOOBUSY: partial` | a part is done, committed and pushed; what is left stands before this line, after the report: a line `TOOBUSY-REST: <title of the new task>` and under it the description of that task |
| `TOOBUSY: owner` | nothing could be done without the owner; the reply says what is needed, and the changes are undone |
| `TOOBUSY: failed <reason>` | the work stopped on something the session cannot fix; the working copy is left as it is |
| `TOOBUSY: interrupted` | the session wrapped the task up because it was asked to |

The line is read whatever its case and whatever marks of emphasis stand around it; the last such line of the reply counts.

### How a task ends

What the session says is checked first: after `done`, `partial`, `owner` and `interrupted` the working tree must be clean, and after `done` and `partial` the branch must have no commit that its upstream lacks. Then the tracker is told:

| Outcome | The tracker |
|---|---|
| done | the report as a comment; the task is closed and moved to `Done` |
| partial | a new task is made of what is left, as described below; the report as a comment that names it; the task is closed and moved to `Done` |
| owner | the task gets the label of the owner and the report as a comment, and goes back to `Todo` |
| interrupted | the task gets the label of interrupted tasks and the report as a comment, and goes back to `Todo` |

Every comment starts with what became of the task, `**Done.**` for one, and ends with the session.

**The run stops**, with exit code 1 and the task left as it is, in `In Progress`, when the session failed, when it ended without saying how the task went, when the check of the working copy did not pass, when the tracker refused something, and when the same task came up again right after its session. `Stopped: #12 left changes in the working tree · claude attach 1a2b3c4d · the next task did not start · 3 done`. The next task is never built on a doubt.

### What cannot be done without the owner

The session is told to do everything that does not depend on the owner, to leave nothing broken, and to commit and push that part. What is left becomes a new task, which the run makes:

- its title and its description are those the session wrote after `TOOBUSY-REST:`: what is left, what is done already and where, and the questions for the owner with the answers the session sees. They are written for a session that has never seen this one. A session that named nothing gives a task called `What is left of “<title>”` with its report as the description;
- under the description stand the task it comes from and, folded, the description of that task;
- it has the label of the owner, the labels of the first task that are labels to take, and the same milestone, and stands on the board as one to do.

No run takes the new task while it has the label of the owner. The owner answers in it, takes the label off, and a run takes it as any other task.

When nothing at all could be done, there is nothing to close and nothing to make: the task itself gets the label of the owner and the questions as a comment.

### A session that waits for the owner

A session waits for the owner when Claude Code says that it is blocked on an answer, and when its turn ended without a line for toobusy. The run says so, with the first lines of what the session said, and notifies the owner:

```
‖ #12 waits for the owner: input needed · it is told to go on alone in 90 s: /nudge does it now, /hold never · claude attach 1a2b3c4d
  │ The task does not say what kind of file the export is. Should it be CSV or JSON?
```

After ninety seconds the turn of the session is cut, and it goes on with this as the next message of its conversation:

> The owner is away: no answer to your question and no approval will come. Decide yourself and go on with task #12 from where you stopped. What cannot be decided without the owner goes into the new task for the owner, as the rules of this session say. End your reply with a `TOOBUSY:` line.

The log shows the message as it was sent. The command the log calls `/nudge` sends it at once, and `/hold` never: the session waits for the owner, who answers it with `claude attach` or sends the message later. On the page of a run the two are `send now` and `hold`, and they are offered as soon as a session waits. A session that goes back to work by itself is told nothing.

A task is told so ten times at most, and it is left to the owner sooner when two messages in a row brought no step of its own. A session that is left to the owner after its turn ended stops the run, as one that did not say how the task went.

### Stopping

A run is stopped with the commands of the menu of its page, `stop`, `continue` and `abort`; the log calls them with a slash:

| Command | Does |
|---|---|
| `/stop` | the current task is finished, and no other is taken: `Stopped by /stop · 3 done`. Between tasks the queue stops at once |
| `/continue` | takes `/stop` back |
| `/abort` | stops as soon as possible, with nothing committed and a report in the task. It cannot be taken back |
| Ctrl+C twice | kills the run: the session is stopped at once, and the task stays in `In Progress` as it is. The exit code is 130 |

**`/abort`.** The session is asked to wrap up: to let the command that runs finish and start no new step, to commit and push nothing, to undo its uncommitted changes, and to reply with a report for the session that will go on with the task — what was found out and decided, what was undone, file by file, and what is left — ended with `TOOBUSY: interrupted`. The request reaches a session that works after its next step, without cutting its turn: a hook of the session prints it. A session that has not wrapped up in ten minutes, and one that waits for the owner, has its turn cut and gets the request as a message. When the session ends, the task gets the label of interrupted tasks and the report, goes back to `Todo`, and the run stops; the next run takes the task first, and its session is told that the report of the one before is among the comments.

### Usage limits

Claude Code has two limits of usage, a five-hour one and a weekly one; a run knows them from the status line of its last session.

- **Before a task.** At the share of the weekly limit that the user chose, 96% until they choose, the run stops. At 96% of the five-hour limit it waits for the reset and a minute more, and reads the queue again; `/stop` ends the wait.
- **Inside a task.** A session that runs into a limit is stopped. The run waits for the reset of the five-hour limit, or for half an hour when it does not know the time, and the same session goes on with a message that says so. A task waits so ten times at most.
- **A pause.** When the weekly limit is spent, when the limit did not reset, when the session did not go on, and when the owner stops the run during the wait, the task is paused: the session is stopped, the task gets the label of interrupted tasks and a comment, goes back to `Todo`, and its uncommitted changes stay in the working tree. The run ends. The next run takes the paused task first, over the changes, and goes on with the same session. A pause whose changes are gone is forgotten; one whose task the queue does not take stops the run while the changes wait.

### Local state

What a run leaves on the machine lies in `.toobusy/local/`: the task that is paused, the request that waits for a session, and what the status line of the last session told. The folder is made by the run and has a `.gitignore` of its own that leaves everything in it out, so that nothing of it is committed, the working tree stays clean, and the `.gitignore` of the project is not touched.

### The machine

On macOS the machine is kept awake while a run lasts and no longer, so that back in the menu it may sleep, and the owner gets a notification when a session waits for an answer, when a task is paused, and when the run ends. On other systems neither is done yet.

### The page of a run

A run is a tape on the terminal's own screen, under the bar of the page, which is printed once:

```
  toobusy · ~/Projects/rocket                                             Running the queue

 ✔ Show the total of an order in its header  12:04
 ◐ Fix the rounding of a discount  7:31

 ⣾ Export the orders as a file
   1:24 · Edit src/Export/CsvExport.cs
 ────────────────────────────────────────────────────────────
 ❯ press / to show the menu
 ────────────────────────────────────────────────────────────
 ctrl+c stop the session and exit                5-hour 41% · weekly 12%
```

- **What stays.** A task that is over is one line: the mark of how it went in its colour, `✔` done, `◐` done in part, `◇` waits for the owner, `■` interrupted or killed, `✖` failed, `‖` paused by a usage limit; its title; and, muted, the time it took. A title that does not fit gives way to the time. Nothing is said of a task that starts. What a run warns of, `▲`, stays as a line too. These lines are written for good: as they grow in number the bar scrolls up with them into the history of the terminal, and the terminal scrolls back to them.
- **The task that is worked on** stands under them, redrawn in its place. Before its title a mark turns in the accent: a cell full of dots with a gap that runs around it clockwise, `⣾ ⣷ ⣯ ⣟ ⡿ ⢿ ⣻ ⣽`, a turn in two thirds of a second; without colours a `●` stands there still. Under the title, muted, stand the time the task has been worked on and what its session is doing. A session that waits says so and when it is told to go on alone, a wait for a limit when it ends, and a run that was told to stop that this is its last task. Between tasks the first line says what the run is doing.
- **A session that waits for the owner** shows, under these two lines, what it said last, eight lines of it at most, and how its session is opened to answer it. When it goes on, there is the title, the time and the step again, and nothing else.
- **The commands** have their place between two rules. Nothing is typed there: the line says `press / to show the menu` after the pointer of a menu, `❯`, which blinks there as it does on the chosen line of the menu that opens in its place. `/` opens the menu of the commands that mean something at the moment, each with what it does; Up and Down move, Enter runs one, Escape closes the menu. They are `hold` and `send now` while a session waits for the owner, then `stop`, `continue` and `abort`. A session that waits for the owner brings `hold` and `send now` without being asked; Escape puts them away, and `/` opens the whole menu.
- **Under the second rule** stand the keys and, at the right edge, how much of the two limits is used, when it is known. After the first Ctrl+C the line says `press ctrl+c again to stop the session and exit`.
- **The end.** When the run is over, what was redrawn is erased, and under the tasks the tape says how the run went: `5 tasks in 1 h 12 min · 4 done · 1 done in part`, and under that, for a run that did not simply run out of tasks, what ended it: `‖ Stopped: the weekly limit is at 80%`. The page does not wait to be left: a run that was opened from the menu goes back to it, with this said above the menu, and `toobusy run` leaves the tool.

No line of a tape wraps: lines are cut a column short of the window, and the terminal is told not to wrap while the tape is unrolled. What is redrawn is never taller than the window: what a session said gives way first. When the window changes its size the whole tape is drawn anew to the new one: the terminal is erased, its history too, and the bar, every line that stays and what is redrawn under them are written again, a title or a warning laid out to the new width. What the terminal had in its history before the run is gone with that. A terminal that jumps to its last line whenever something is written lets the history be read only between two drawings, which come once in a second while a task is worked on.

Without a terminal the lines of the log are printed as they come: the run, each task that starts and how it ends, what is done to the tracker, every wait and every command, each with its mark: `✻` the run, `→` a task that is started, `▶` goes on, and the marks above.

### A demo of a run

`run --demo` is the real run over a made-up tracker and made-up sessions; nothing is changed anywhere, and the waits are seconds long. The tasks fit the rules of the project, and one after another they show: a task that is done; a session that stops with a question, the countdown, the message toobusy sends and the session deciding alone; a task done in part, whose rest becomes a task for the owner; a session that runs into the five-hour limit, the wait and the same session going on; a task that opens after another; and tasks that are held by a label and by their status. The commands of the page work as they do in a run.

### Tests

- The queue: every reason a task is held for, the order, tasks that wait for others.
- The line for toobusy in a reply, and the messages a session gets.
- The run, with hand-written fakes of the tracker, the assistant, the working copy, the machine and the clock: a task after a task, every outcome, a session that asks and is told, the limits of that, every command, a kill, the limits of usage, a pause and the run after it.
- What is asked of `gh` and of `claude`, argument by argument, and what is made of their answers; a conversation of Claude Code read from a file of the test.
- The tape of a run over a run that the test writes: what stays and what is redrawn, the menu, the commands a waiting session brings, a kill, a short and a narrow window. A demo from its first line to its last through the command line, with a clock that makes nobody wait: as a log without a terminal, and as a tape in one.
