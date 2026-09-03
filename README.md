# Markdown Viewer

A small, good-looking Markdown viewer and editor. Open a `.md` file as **raw
source**, as a **rendered document**, or **both side by side** with a divider you
can drag — and edit the source with the rendered side updating as you type.

It runs two ways: as a **Windows desktop app** in its own window, or as a plain
web page you open in a browser. Same app either way — no build step, no
framework, no runtime dependencies to fetch.

---

## The desktop app

```powershell
powershell -ExecutionPolicy Bypass -File desktop\build.ps1
```

That produces `MarkdownViewer.exe` (~1.2 MB) in the repo root. It is
self-contained: the whole web app is embedded in it, so the exe alone can be
copied anywhere and run.

Building needs **nothing installed** — no .NET SDK, no Visual Studio, no package
manager. It uses the C# compiler that already ships with Windows, and downloads
the WebView2 SDK from nuget.org on the first build.

Requires Windows 10/11 with the Edge WebView2 runtime, which is preinstalled on
Windows 11 and on any machine with a current Edge.

**Open a file directly:**

```powershell
MarkdownViewer.exe notes.md
```

Because it accepts a file argument, you can make it the default app for `.md`
files (right-click a `.md` → *Open with* → *Choose another app*) and just
double-click your way in.

Opening several files that way fills **one** window rather than spawning a
window each: later launches hand their arguments to the instance already
running and exit, so the files pile up in the sidebar. Pass `--new-window` when
you actually want a second window.

### Why the desktop app is the better way to run it

| | Desktop app | Browser (`file://`) |
| --- | :-: | :-: |
| Its own window, no browser chrome | ✓ | — |
| Settings remembered between runs | ✓ | — |
| Auto-reload when a file changes on disk | ✓ | — |
| Native file and folder pickers | ✓ | partial |
| Remembers window size and position | ✓ | — |

Browsers deliberately withhold persistent storage and file-system access from
`file://` pages, which is why the plain-HTML route loses those. The desktop
shell sidesteps it by serving the app from a real origin
(`https://mdviewer.local/`) inside its own window — a normal secure context, so
settings persist — and by watching files natively with `FileSystemWatcher`
instead of polling. No local web server is involved at any point.

### In a browser instead

Open `index.html`. Everything in *Markdown support* below works identically; you
just lose the row of ticks above. Serving the folder over `http://`
(`python -m http.server 8000`) restores saved settings and auto-reload in Chrome
and Edge.

---

## The three views

| View | What you get |
| --- | --- |
| **Raw** | An editor for the Markdown source, syntax-highlighted, with line numbers |
| **Rendered** | The formatted document |
| **Split** | Both, with a draggable divider and synchronised scrolling |

Drag the divider to give either side more room. Double-click it to snap back to
50/50, or focus it and use the arrow keys. The position is remembered.

---

## Editing

The raw pane is a real editor, in both Raw and Split view. Type in it and the
rendered side follows a moment later.

- **Unsaved changes** show as an asterisk after the file name — in the toolbar,
  the sidebar, and the window title. It clears itself if you undo back to what
  is on disk.
- **Save** writes in place; **Save as…** picks a new file and retargets the
  document to it.
- **Undo and redo** are the browser's own, so they behave exactly as you expect.
- <kbd>Tab</kbd> indents, <kbd>Shift</kbd>+<kbd>Tab</kbd> outdents, and both work
  across a multi-line selection.
- <kbd>Enter</kbd> continues what you are in the middle of: bullets, numbered
  items (incrementing as it goes), task list items, blockquotes, and plain
  indentation. Pressing it on an empty item ends the list instead.
- The status bar shows **line and column**, and the selected character count.

### Find and replace

<kbd>Ctrl</kbd>+<kbd>F</kbd> opens find, <kbd>Ctrl</kbd>+<kbd>H</kbd> opens it with
replace. Every match is highlighted in the source at once, with the current one
picked out, and the count reads `3 of 17`.

- <kbd>Enter</kbd> / <kbd>Shift</kbd>+<kbd>Enter</kbd> (or <kbd>F3</kbd>) step
  through matches, wrapping around at the ends. <kbd>Esc</kbd> closes.
- **Match case** (<kbd>Alt</kbd>+<kbd>C</kbd>), **whole word**
  (<kbd>Alt</kbd>+<kbd>W</kbd>) and **regular expression**
  (<kbd>Alt</kbd>+<kbd>R</kbd>) — in regex mode `^` and `$` anchor to lines.
- **Replace** changes the current match; **All** does the lot as a single undo
  step, so one <kbd>Ctrl</kbd>+<kbd>Z</kbd> takes it all back.
- Opening find with text selected searches for that text.

Nothing is written until you ask for it, and nothing overwrites your work:

- Closing with unsaved changes asks first.
- If a file changes **on disk** while you have unsaved edits, auto-reload backs
  off and keeps your version, telling you it did.
- Re-opening a file that has unsaved edits keeps the edits rather than
  reloading over them.
- A file's existing line endings (CRLF or LF) are preserved on save, so editing
  one line does not rewrite every line in your diff.

In a browser rather than the desktop app, saving uses the File System Access API
where it exists (Chrome, Edge) and otherwise falls back to downloading the file,
which is the only write a browser will permit.

---

## Markdown support

Rendering follows **GitHub Flavored Markdown**, so files written for GitHub — or
written for you by Claude — look the way they were meant to look.

- Headings with hover anchors, and a document outline panel
- **Bold**, _italic_, ~~strikethrough~~, `inline code`, links, images
- Tables, with horizontal scrolling when they are wide
- Task lists, including checked boxes
- Fenced code blocks with syntax highlighting for ~40 languages, plus a copy button
- Blockquotes and [GitHub alerts](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax#alerts) — `> [!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]`, `[!CAUTION]`
- Footnotes, collected into a section at the end
- YAML front matter, shown as a collapsible table instead of leaking into the text
- **Mermaid** diagrams in ` ```mermaid ` blocks
- **LaTeX** math, inline with `$…$` and display with `$$…$$`

> [!NOTE]
> Mermaid and KaTeX are the only pieces not bundled. They load from a CDN, and
> only when a document actually uses them — so everything else works with no
> network at all. If you are offline the first time you open a file containing a
> diagram or an equation, the source is shown as a code block instead.

---

## Opening files

- **Drag and drop** a file — or a whole folder — anywhere in the window
- **Files** / **Folder** buttons in the sidebar
- **Paste** Markdown straight from the clipboard with <kbd>Ctrl</kbd>+<kbd>V</kbd>
- Pass a path on the command line (desktop app)

Opening a folder loads every Markdown file in it, grouped by directory in the
sidebar, with a filter box for finding one quickly. Relative image links and
relative links between documents both resolve, so a folder of notes browses like
a small wiki.

### Auto-reload

In the desktop app this is on by default: edit a file in your editor and the
viewer re-reads it the moment you save, holding your scroll position. Useful
when something else is writing the file while you watch it. Toggle it from the
status bar.

---

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> | Save |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> | Save as… |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> / <kbd>Ctrl</kbd>+<kbd>H</kbd> | Find / find and replace |
| <kbd>F3</kbd> / <kbd>Shift</kbd>+<kbd>F3</kbd> | Next / previous match |
| <kbd>Tab</kbd> / <kbd>Shift</kbd>+<kbd>Tab</kbd> | Indent / outdent (in the editor) |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> / <kbd>Ctrl</kbd>+<kbd>Y</kbd> | Undo / redo (in the editor) |
| <kbd>1</kbd> / <kbd>2</kbd> / <kbd>3</kbd> | Raw / Split / Rendered |
| <kbd>4</kbd> | Toggle the outline |
| <kbd>T</kbd> | Cycle theme: auto → light → dark |
| <kbd>[</kbd> / <kbd>]</kbd> | Previous / next document |
| <kbd>Ctrl</kbd>+<kbd>O</kbd> | Open files |
| <kbd>Ctrl</kbd>+<kbd>B</kbd> | Toggle the sidebar |
| <kbd>Ctrl</kbd>+<kbd>K</kbd> | Jump to the file filter |
| <kbd>Ctrl</kbd>+<kbd>P</kbd> | Print — or save the rendered document as PDF |

The single-letter shortcuts only fire outside the editor, so typing `t` in your
document does not change the theme.

Printing outputs the rendered document alone: no sidebar, no toolbar, no raw pane.

---

## Themes

Light and dark, following your system by default. Click the icon in the toolbar
(or press <kbd>T</kbd>) to pin one. Code highlighting, diagrams and equations all
follow the active theme.

---

## Project layout

```
index.html                   markup and the pre-paint theme switch
assets/app.css               application shell
assets/markdown.css          rendered-document styling
assets/highlight.css         syntax colours, shared by both themes
assets/app.js                everything else
vendor/                      marked, DOMPurify, highlight.js  (~180 KB)

desktop/MarkdownViewer.cs    WebView2 host: window, virtual origin, file watching
desktop/MakeIcon.cs          draws the .ico at build time, no image tooling needed
desktop/app.manifest         per-monitor DPI awareness
desktop/build.ps1            the whole build
```

Rendered HTML is sanitised with DOMPurify before it reaches the page, so opening
an untrusted Markdown file cannot run scripts. The desktop shell serves only its
own `index.html`, `assets/` and `vendor/` from disk, and hands external links to
your real browser rather than opening them inside the app window.

Set `MDVIEWER_DEBUG=1` before launching to get a trace at
`%LOCALAPPDATA%\MarkdownViewer\debug.log`.

## Browser support

Any current Chrome, Edge, Firefox or Safari. Folder picking and browser-side
auto-reload use the File System Access API and are Chromium-only; everywhere
else the viewer falls back to a normal file picker.

## License

[MIT](LICENSE)
