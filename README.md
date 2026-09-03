# Markdown Viewer

A small, good-looking Markdown viewer that runs from a single HTML file. Open a
`.md` file as **raw source**, as a **rendered document**, or **both side by side**
with a divider you can drag.

No build step, no install, no server, no dependencies to fetch — clone the repo
and double-click `index.html`.

---

## Quick start

```bash
git clone https://github.com/jdsaphir/markdown-viewer.git
cd markdown-viewer
```

Then open `index.html` in your browser. That's the whole setup.

Prefer it served? Any static server works:

```bash
python -m http.server 8000
```

Serving it over `http://` unlocks two extras that browsers withhold from
`file://` pages: **auto-reload** when a file changes on disk, and persistent
settings.

---

## The three views

| View         | What you get                                                    |
| ------------ | --------------------------------------------------------------- |
| **Raw**      | The Markdown source, syntax-highlighted, with line numbers        |
| **Rendered** | The formatted document                                            |
| **Split**    | Both, with a draggable divider and synchronised scrolling         |

Drag the divider to give either side more room. Double-click it to snap back to
50/50, or focus it and use the arrow keys. The position is remembered.

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

Dropping a folder loads every Markdown file it contains, grouped by directory in
the sidebar, with a filter box for finding one quickly. Relative image links and
relative links between documents both resolve, so a folder of notes browses like
a small wiki.

### Auto-reload

When the browser grants file handles (Chrome and Edge over `http://`), an
**Auto-reload** toggle appears in the status bar. Turn it on and the viewer
re-reads the file whenever it changes on disk, keeping your scroll position —
useful when something else is writing the file while you watch.

---

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| <kbd>1</kbd> / <kbd>2</kbd> / <kbd>3</kbd> | Raw / Split / Rendered |
| <kbd>4</kbd> | Toggle the outline |
| <kbd>T</kbd> | Cycle theme: auto → light → dark |
| <kbd>[</kbd> / <kbd>]</kbd> | Previous / next document |
| <kbd>Ctrl</kbd>+<kbd>O</kbd> | Open files |
| <kbd>Ctrl</kbd>+<kbd>B</kbd> | Toggle the sidebar |
| <kbd>Ctrl</kbd>+<kbd>K</kbd> | Jump to the file filter |
| <kbd>Ctrl</kbd>+<kbd>P</kbd> | Print — or save the rendered document as PDF |

Printing outputs the rendered document alone: no sidebar, no toolbar, no raw pane.

---

## Themes

Light and dark, following your system by default. Click the icon in the toolbar
(or press <kbd>T</kbd>) to pin one. Code highlighting, diagrams and equations all
follow the active theme.

---

## Project layout

```
index.html            markup and the pre-paint theme switch
assets/app.css        application shell
assets/markdown.css   rendered-document styling
assets/highlight.css  syntax colours, shared by both themes
assets/app.js         everything else
vendor/               marked, DOMPurify, highlight.js  (~180 KB total)
```

Rendered HTML is sanitised with DOMPurify before it reaches the page, so opening
an untrusted Markdown file cannot run scripts.

## Browser support

Any current Chrome, Edge, Firefox or Safari. Folder picking and auto-reload use
the File System Access API and are Chromium-only; everywhere else the viewer
falls back to a normal file picker.

## License

[MIT](LICENSE)
