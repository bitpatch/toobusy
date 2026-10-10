#!/usr/bin/env python3
"""Takes pictures of the screen of toobusy.

Runs the real binary in a pseudo-terminal, presses the keys of a scenario and draws what a terminal would show
to PNG files, each with the same screen as text next to it. It is there to check the look of a page: the colours,
the grounds and the blinking, which the tests see only as escape sequences.

    tools/screenshot.py init --demo -- shot:project tab tab type:Rocket shot:new-project blink

What stands before `--` are the arguments of toobusy, what stands after it is the scenario, a step per argument:

    tab shift+tab enter esc up down left right home end space backspace delete ctrl+<letter>
    type:<text>     types the text
    wait:<ms>       waits
    until:<text>    waits until the screen shows the text
    shot[:<name>]   takes a picture
    blink[:<n>]     takes n pictures (10 if not said) over one blink and stacks the rows that change

The pictures go to artifacts/screens. The emulator and the drawing need pyte and Pillow: at the first run they are
installed into artifacts/screens/.venv, and nothing is added to toobusy itself.
"""

import argparse
import fcntl
import os
import select
import shutil
import signal
import struct
import subprocess
import sys
import termios
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCREENS = ROOT / "artifacts" / "screens"
VENV = SCREENS / ".venv"
BINARY = ROOT / "src" / "TooBusy.Cli" / "bin" / "Debug" / "net10.0" / "toobusy"

# One blink of the screen: Screen.Beat times Screen.Beats.
BLINK = 1.6
SETTLE = 0.25

KEYS = {
    "tab": b"\t",
    "shift+tab": b"\x1b[Z",
    "enter": b"\r",
    "esc": b"\x1b",
    "up": b"\x1b[A",
    "down": b"\x1b[B",
    "right": b"\x1b[C",
    "left": b"\x1b[D",
    "home": b"\x1b[H",
    "end": b"\x1b[F",
    "space": b" ",
    "backspace": b"\x7f",
    "delete": b"\x1b[3~",
}

THEMES = {
    "dark": {"ground": (28, 28, 28), "ink": (226, 226, 226), "COLORFGBG": "15;0"},
    "light": {"ground": (255, 255, 255), "ink": (30, 30, 30), "COLORFGBG": "0;15"},
}

# The sixteen colours of xterm, for a palette that does not use the full range.
NAMED = {
    "black": (0, 0, 0), "red": (205, 0, 0), "green": (0, 205, 0), "brown": (205, 205, 0),
    "blue": (0, 0, 238), "magenta": (205, 0, 205), "cyan": (0, 205, 205), "white": (229, 229, 229),
    "brightblack": (127, 127, 127), "brightred": (255, 0, 0), "brightgreen": (0, 255, 0),
    "brightbrown": (255, 255, 0), "brightblue": (92, 92, 255), "brightmagenta": (255, 0, 255),
    "brightcyan": (0, 255, 255), "brightwhite": (255, 255, 255),
}

FONTS = ["/System/Library/Fonts/Menlo.ttc", "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"]
BOLD_FONTS = [("/System/Library/Fonts/Menlo.ttc", 1), ("/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf", 0)]
# Where a character is looked for when the main font does not have it.
SPARE_FONTS = [
    "/System/Library/Fonts/Apple Symbols.ttf",
    "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
]


def with_libraries():
    """Runs this script again in the environment that has pyte and Pillow, making it first when it is missing."""
    try:
        import PIL  # noqa: F401
        import pyte  # noqa: F401
        return
    except ImportError:
        pass

    python = VENV / "bin" / "python"
    if Path(sys.prefix).resolve() == VENV.resolve():
        sys.exit("screenshot: pyte and Pillow could not be loaded from " + str(VENV))
    if not python.exists():
        print("screenshot: installing pyte and Pillow into " + str(VENV.relative_to(ROOT)), file=sys.stderr)
        subprocess.run([sys.executable, "-m", "venv", str(VENV)], check=True)
        installed = subprocess.run([str(python), "-m", "pip", "install", "--quiet", "pyte", "Pillow"])
        if installed.returncode != 0:
            shutil.rmtree(VENV)
            sys.exit("screenshot: pyte and Pillow could not be installed")
    os.execv(str(python), [str(python), __file__, *sys.argv[1:]])


class Terminal:
    """The binary in a pseudo-terminal and the screen it has drawn so far."""

    def __init__(self, command, columns, rows, theme, directory):
        import pyte

        self.screen = pyte.Screen(columns, rows)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = os.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))

        environment = dict(os.environ, TERM="xterm-256color", COLORTERM="truecolor", COLORFGBG=theme["COLORFGBG"])
        environment.pop("NO_COLOR", None)

        def own_terminal():
            os.setsid()
            fcntl.ioctl(0, termios.TIOCSCTTY, 0)

        self.process = subprocess.Popen(
            command, stdin=slave, stdout=slave, stderr=slave, cwd=directory, env=environment, preexec_fn=own_terminal)
        os.close(slave)

    def read(self, seconds):
        """Takes in what the binary writes during the given time."""
        end = time.monotonic() + seconds
        while (left := end - time.monotonic()) > 0:
            ready, _, _ = select.select([self.master], [], [], left)
            if not ready:
                break
            try:
                data = os.read(self.master, 65536)
            except OSError:
                data = b""
            if not data:
                time.sleep(max(0.0, end - time.monotonic()))
                break
            self.stream.feed(data)

    def press(self, keys):
        try:
            os.write(self.master, keys)
        except OSError:
            raise SystemExit("screenshot: toobusy has ended and takes no keys:\n" + self.text()) from None
        self.read(SETTLE)

    def text(self):
        return "\n".join(line.rstrip() for line in self.screen.display).rstrip()

    def until(self, text, seconds=15.0):
        end = time.monotonic() + seconds
        while text not in self.text():
            if time.monotonic() > end:
                raise SystemExit(f"screenshot: the screen never showed “{text}”:\n{self.text()}")
            self.read(0.05)
        self.read(SETTLE)

    def cells(self):
        """The screen as rows of (character, ink, ground, bold); the colours are None where the terminal decides."""
        rows = []
        for y in range(self.screen.lines):
            line = self.screen.buffer[y]
            row = []
            for x in range(self.screen.columns):
                cell = line[x]
                ink, ground = colour(cell.fg), colour(cell.bg)
                row.append((cell.data or " ", ink, ground, cell.bold, cell.reverse))
            rows.append(row)
        return rows

    def close(self):
        if self.process.poll() is None:
            self.process.send_signal(signal.SIGKILL)
        # The terminal is closed first: a process does not end while what it wrote to its terminal is unread.
        os.close(self.master)
        self.process.wait()


def colour(name):
    if name == "default":
        return None
    if name in NAMED:
        return NAMED[name]
    try:
        return tuple(int(name[index:index + 2], 16) for index in (0, 2, 4))
    except ValueError:
        return None


class Painter:
    """Draws rows of cells with a fixed-width font."""

    def __init__(self, theme, scale):
        from PIL import ImageFont

        self.theme = theme
        size = 14 * scale
        regular = next(path for path in FONTS if os.path.exists(path))
        bold, index = next((path, index) for path, index in BOLD_FONTS if os.path.exists(path))
        self.regular = ImageFont.truetype(regular, size)
        self.bold = ImageFont.truetype(bold, size, index=index)
        self.spare = [ImageFont.truetype(path, size) for path in SPARE_FONTS if os.path.exists(path)]
        self.missing = bytes(self.regular.getmask("￿"))
        self.width = round(self.regular.getlength("M"))
        self.height = round(size * 1.3)
        self.rise = round(size * 0.12)

    def font(self, character, bold):
        font = self.bold if bold else self.regular
        if character.isascii() or bytes(font.getmask(character)) != self.missing:
            return font
        for spare in self.spare:
            if bytes(spare.getmask(character)) != bytes(spare.getmask("￿")):
                return spare
        return font

    def paint(self, rows):
        from PIL import Image, ImageDraw

        image = Image.new("RGB", (self.width * len(rows[0]), self.height * len(rows)), self.theme["ground"])
        draw = ImageDraw.Draw(image)
        for y, row in enumerate(rows):
            for x, (character, ink, ground, bold, reverse) in enumerate(row):
                ink, ground = ink or self.theme["ink"], ground or self.theme["ground"]
                if reverse:
                    ink, ground = ground, ink
                left, top = x * self.width, y * self.height
                if ground != self.theme["ground"]:
                    draw.rectangle([left, top, left + self.width - 1, top + self.height - 1], fill=ground)
                if character.strip():
                    draw.text((left, top + self.rise), character, font=self.font(character, bold), fill=ink)
        return image


def blink(terminal, painter, count):
    """Pictures of the rows that change during one blink, one under another in the order of time."""
    from PIL import Image

    frames = []
    for _ in range(count):
        frames.append(terminal.cells())
        terminal.read(BLINK / count)

    changing = [y for y in range(len(frames[0])) if any(frame[y] != frames[0][y] for frame in frames)]
    if not changing:
        return painter.paint(frames[0]), "nothing changes during a blink"

    first, last = max(0, changing[0] - 1), min(len(frames[0]), changing[-1] + 2)
    strips = [painter.paint(frame[first:last]) for frame in frames]
    gap = painter.height // 3
    image = Image.new("RGB", (strips[0].width, sum(strip.height + gap for strip in strips) - gap), (128, 128, 128))
    for index, strip in enumerate(strips):
        image.paste(strip, (0, index * (strip.height + gap)))
    return image, f"rows {first + 1}–{last} at {count} moments of a blink"


def main():
    arguments = sys.argv[1:]
    scenario = []
    if "--" in arguments:
        at = arguments.index("--")
        arguments, scenario = arguments[:at], arguments[at + 1:]

    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter,
        usage="screenshot.py [options] <arguments of toobusy> -- <steps>")
    parser.add_argument("--size", default="100x30", help="columns and rows of the terminal (100x30)")
    parser.add_argument("--light", action="store_true", help="a terminal with a light ground")
    parser.add_argument("--binary", default=None, help="the binary to run instead of the debug build")
    parser.add_argument("--no-build", action="store_true", help="do not build before running")
    parser.add_argument("--directory", default=str(ROOT), help="the folder toobusy is run in (the repository)")
    parser.add_argument("--scale", type=int, default=2, help="how many pixels a point takes (2)")
    options, command = parser.parse_known_args(arguments)

    with_libraries()

    if options.binary is None and not options.no_build:
        built = subprocess.run(
            ["dotnet", "build", str(ROOT / "src" / "TooBusy.Cli"), "--nologo", "-v", "quiet"],
            capture_output=True, text=True)
        if built.returncode != 0:
            sys.exit(built.stdout + built.stderr)

    columns, rows = (int(part) for part in options.size.lower().split("x"))
    theme = THEMES["light" if options.light else "dark"]
    painter = Painter(theme, options.scale)

    SCREENS.mkdir(parents=True, exist_ok=True)
    for old in [*SCREENS.glob("*.png"), *SCREENS.glob("*.txt")]:
        old.unlink()

    terminal = Terminal([options.binary or str(BINARY), *command], columns, rows, theme, options.directory)
    taken = 0

    def save(image, name, note):
        nonlocal taken
        taken += 1
        path = SCREENS / f"{taken:02}-{name}.png"
        image.save(path)
        path.with_suffix(".txt").write_text(terminal.text() + "\n")
        print(f"{path.relative_to(ROOT)}  {note}")

    try:
        terminal.read(1.0)
        for step in scenario or ["shot"]:
            name, _, value = step.partition(":")
            if name == "type":
                for character in value:
                    terminal.press(character.encode())
            elif name == "wait":
                terminal.read(int(value) / 1000)
            elif name == "until":
                terminal.until(value)
            elif name == "shot":
                save(painter.paint(terminal.cells()), value or "screen", f"{columns}x{rows}")
            elif name == "blink":
                image, note = blink(terminal, painter, int(value or 10))
                save(image, "blink", note)
            elif name.startswith("ctrl+") and len(name) == 6:
                terminal.press(bytes([ord(name[-1].lower()) & 0x1f]))
            elif step in KEYS:
                terminal.press(KEYS[step])
            else:
                sys.exit(f"screenshot: unknown step “{step}”")
    finally:
        terminal.close()


if __name__ == "__main__":
    main()
