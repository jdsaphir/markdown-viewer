/* ==========================================================================
   Markdown Viewer
   Zero-build, single-page. marked + DOMPurify + highlight.js are vendored;
   KaTeX and Mermaid are fetched from a CDN only if a document actually uses
   them, so the core stays offline-capable.
   ========================================================================== */
(function () {
'use strict';

/* ---------------------------------------------------------------- helpers */

const $  = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

const MD_EXT = /\.(md|markdown|mdown|mkd|mkdn|mdx|qmd|rmd|txt)$/i;

function escapeHtml(s) {
  return String(s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function formatBytes(n) {
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(n < 10240 ? 1 : 0) + ' KB';
  return (n / 1048576).toFixed(1) + ' MB';
}

const store = {
  get(key, fallback) {
    try { const v = localStorage.getItem('mdv.' + key); return v === null ? fallback : JSON.parse(v); }
    catch (_) { return fallback; }
  },
  set(key, value) {
    try { localStorage.setItem('mdv.' + key, JSON.stringify(value)); } catch (_) { /* file:// or private mode */ }
  }
};

let toastTimer = null;
function toast(msg) {
  const el = $('#toast');
  el.textContent = msg;
  el.hidden = false;
  requestAnimationFrame(() => el.classList.add('is-visible'));
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    el.classList.remove('is-visible');
    setTimeout(() => { el.hidden = true; }, 200);
  }, 2200);
}

/** Load a classic script once; resolves when it has executed. */
const scriptCache = new Map();
function loadScript(src) {
  if (scriptCache.has(src)) return scriptCache.get(src);
  const p = new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = src;
    s.onload = () => resolve();
    s.onerror = () => reject(new Error('Could not load ' + src));
    document.head.appendChild(s);
  });
  scriptCache.set(src, p);
  return p;
}

function loadStyle(href) {
  if (scriptCache.has(href)) return scriptCache.get(href);
  const p = new Promise((resolve) => {
    const l = document.createElement('link');
    l.rel = 'stylesheet';
    l.href = href;
    l.onload = l.onerror = () => resolve();
    document.head.appendChild(l);
  });
  scriptCache.set(href, p);
  return p;
}

const CDN = {
  katexJs:  'https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.js',
  katexCss: 'https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.css',
  mermaid:  'https://cdn.jsdelivr.net/npm/mermaid@10.9.1/dist/mermaid.min.js'
};

/* ------------------------------------------------------------------ state */

const state = {
  docs: [],            // { id, name, dir, path, text, size, handle, lastModified }
  activeId: null,
  assets: new Map(),   // normalised path -> File | FileSystemFileHandle (for images)
  objectUrls: [],
  view: store.get('view', 'split'),
  split: store.get('split', 50),
  theme: store.get('theme', 'auto'),
  sync: store.get('sync', true),
  watching: false,
  watchTimer: null,
  mermaidTheme: null,
  seq: 0
};

const activeDoc = () => state.docs.find((d) => d.id === state.activeId) || null;

/* ------------------------------------------------------------------ theme */

const mq = window.matchMedia('(prefers-color-scheme: dark)');

function resolveTheme() {
  const resolved = state.theme === 'auto' ? (mq.matches ? 'dark' : 'light') : state.theme;
  document.documentElement.dataset.theme = state.theme;
  document.documentElement.dataset.resolved = resolved;
  $('#btn-theme').title = 'Theme: ' + state.theme + ' (click to change)';
  if (state.mermaidTheme && state.mermaidTheme !== resolved) renderMermaid(true);
  return resolved;
}

function cycleTheme() {
  const order = ['auto', 'light', 'dark'];
  state.theme = order[(order.indexOf(state.theme) + 1) % order.length];
  store.set('theme', state.theme);
  resolveTheme();
  toast('Theme: ' + state.theme);
}

mq.addEventListener('change', () => { if (state.theme === 'auto') resolveTheme(); });

/* ======================================================================
   Markdown pipeline
   ====================================================================== */

/* --- slugs ---------------------------------------------------------- */
let slugCounts = Object.create(null);

function slugify(text) {
  const base = String(text)
    .replace(/<[^>]*>/g, '')
    .trim().toLowerCase()
    .replace(/[^\p{L}\p{N}\s-]/gu, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '') || 'section';
  const n = slugCounts[base] = (slugCounts[base] || 0) + 1;
  return n === 1 ? base : base + '-' + (n - 1);
}

/* --- footnotes ------------------------------------------------------ */
/* GFM footnotes are not built into marked, so definitions are lifted out of
   the source before parsing and appended as a section afterwards.          */
function extractFootnotes(src) {
  const lines = src.split('\n');
  const defs = new Map();
  const kept = [];
  let fence = null;
  let current = null;

  for (const line of lines) {
    const fenceMatch = /^\s{0,3}(`{3,}|~{3,})/.exec(line);
    if (fenceMatch) {
      if (!fence) fence = fenceMatch[1][0];
      else if (fenceMatch[1][0] === fence) fence = null;
      if (current) { defs.set(current.id, current.lines.join('\n')); current = null; }
      kept.push(line);
      continue;
    }
    if (fence) { kept.push(line); continue; }

    const def = /^\[\^([^\]\s]+)\]:[ \t]*(.*)$/.exec(line);
    if (def) {
      if (current) defs.set(current.id, current.lines.join('\n'));
      current = { id: def[1], lines: [def[2]] };
      continue;
    }
    if (current) {
      if (/^(\s{4}|\t)/.test(line)) { current.lines.push(line.replace(/^(\s{4}|\t)/, '')); continue; }
      if (line.trim() === '') { current.lines.push(''); continue; }
      defs.set(current.id, current.lines.join('\n'));
      current = null;
    }
    kept.push(line);
  }
  if (current) defs.set(current.id, current.lines.join('\n'));
  return { text: kept.join('\n'), defs };
}

let footnoteDefs = new Map();
let footnoteOrder = [];

function footnoteHtml() {
  if (!footnoteOrder.length) return '';
  const items = footnoteOrder.map((id, i) => {
    const body = footnoteDefs.get(id);
    const back = `<a href="#fnref-${escapeHtml(id)}" class="footnote-back" title="Back to text">&#8617;</a>`;
    let inner = marked.parse(body == null ? '*Missing footnote.*' : body.trim());
    // Tuck the backlink inside the closing paragraph so it stays on the same line.
    inner = /<\/p>\s*$/.test(inner) ? inner.replace(/<\/p>\s*$/, back + '</p>') : inner + back;
    return `<li id="fn-${escapeHtml(id)}">${inner}</li>`;
  }).join('\n');
  return `\n<section class="footnotes"><h2>Footnotes</h2><ol>${items}</ol></section>`;
}

/* --- front matter --------------------------------------------------- */
function extractFrontMatter(src) {
  const m = /^---\r?\n([\s\S]*?)\r?\n---[ \t]*(?:\r?\n|$)/.exec(src);
  if (!m) return { text: src, fm: null };
  return { text: src.slice(m[0].length), fm: m[1] };
}

function frontMatterHtml(raw) {
  const rows = [];
  let ok = true;
  for (const line of raw.split('\n')) {
    if (!line.trim() || /^\s*#/.test(line)) continue;
    const m = /^([A-Za-z0-9_.-]+)\s*:\s*(.*)$/.exec(line);
    if (!m) { if (/^\s+/.test(line)) continue; ok = false; break; }
    rows.push([m[1], m[2].replace(/^["']|["']$/g, '')]);
  }
  const body = ok && rows.length
    ? `<table><tbody>${rows.map(([k, v]) =>
        `<tr><th>${escapeHtml(k)}</th><td>${escapeHtml(v) || '<span style="opacity:.5">&mdash;</span>'}</td></tr>`).join('')}</tbody></table>`
    : `<pre><code>${escapeHtml(raw)}</code></pre>`;
  return `<details class="front-matter"><summary>Front matter</summary>${body}</details>`;
}

/* --- marked configuration -------------------------------------------- */
const renderer = {
  heading(text, level) {
    const id = slugify(text);
    return `<h${level} id="${id}"><a class="heading-anchor" href="#${id}" aria-hidden="true" tabindex="-1">#</a>${text}</h${level}>\n`;
  },

  code(code, infostring) {
    const lang = (infostring || '').trim().split(/\s+/)[0].toLowerCase();

    if (lang === 'mermaid') {
      return `<div class="mermaid-block" data-state="pending">${escapeHtml(code)}</div>\n`;
    }
    if (lang === 'math' || lang === 'katex' || lang === 'latex') {
      return `<div class="math-block" data-display="1">${escapeHtml(code)}</div>\n`;
    }

    let body;
    if (lang && hljs.getLanguage(lang)) {
      try { body = hljs.highlight(code, { language: lang, ignoreIllegals: true }).value; }
      catch (_) { body = escapeHtml(code); }
    } else {
      body = escapeHtml(code);
    }
    const label = lang ? `<span class="code-lang">${escapeHtml(lang)}</span>` : '';
    const cls = lang ? ` class="language-${escapeHtml(lang)}"` : '';
    return `<div class="code-block">${label}<button class="copy-btn" type="button" aria-label="Copy code">`
      + `<svg viewBox="0 0 16 16" aria-hidden="true"><rect x="5.5" y="5.5" width="8" height="8" rx="1.5"/>`
      + `<path d="M10.5 5.5v-2a1.5 1.5 0 0 0-1.5-1.5H4a1.5 1.5 0 0 0-1.5 1.5v5A1.5 1.5 0 0 0 4 10.5h1.5"/></svg>`
      + `Copy</button><pre><code${cls}>${body}</code></pre></div>\n`;
  },

  link(href, title, text) {
    const safe = escapeHtml(href || '');
    const t = title ? ` title="${escapeHtml(title)}"` : '';
    const external = /^(https?:)?\/\//i.test(href || '');
    const target = external ? ' target="_blank" rel="noopener noreferrer"' : '';
    return `<a href="${safe}"${t}${target}>${text}</a>`;
  },

  table(header, body) {
    return `<div class="table-wrap"><table><thead>${header}</thead><tbody>${body}</tbody></table></div>\n`;
  }
};

const mathBlock = {
  name: 'mathBlock',
  level: 'block',
  start(src) { const i = src.indexOf('$$'); return i < 0 ? undefined : i; },
  tokenizer(src) {
    const m = /^ {0,3}\$\$([\s\S]+?)\$\$[ \t]*(?:\n+|$)/.exec(src);
    if (m) return { type: 'mathBlock', raw: m[0], text: m[1].trim() };
  },
  renderer(token) { return `<div class="math-block" data-display="1">${escapeHtml(token.text)}</div>\n`; }
};

const mathInline = {
  name: 'mathInline',
  level: 'inline',
  start(src) { const i = src.indexOf('$'); return i < 0 ? undefined : i; },
  tokenizer(src) {
    // The trailing-space check is done in JS rather than with a lookbehind so
    // the file still parses on older engines. "$5 and $10" stays plain text.
    const m = /^\$(?![\s$])((?:[^$\n]|\\\$)+?)\$/.exec(src);
    if (m && !/\s$/.test(m[1])) return { type: 'mathInline', raw: m[0], text: m[1] };
  },
  renderer(token) { return `<span class="math-inline">${escapeHtml(token.text)}</span>`; }
};

const footnoteRef = {
  name: 'footnoteRef',
  level: 'inline',
  start(src) { const i = src.indexOf('[^'); return i < 0 ? undefined : i; },
  tokenizer(src) {
    const m = /^\[\^([^\]\s]+)\]/.exec(src);
    if (m) return { type: 'footnoteRef', raw: m[0], text: m[1] };
  },
  renderer(token) {
    const id = token.text;
    if (!footnoteOrder.includes(id)) footnoteOrder.push(id);
    const n = footnoteOrder.indexOf(id) + 1;
    return `<sup><a id="fnref-${escapeHtml(id)}" href="#fn-${escapeHtml(id)}" class="footnote-ref">${n}</a></sup>`;
  }
};

marked.use({
  gfm: true,
  breaks: false,
  pedantic: false,
  renderer,
  extensions: [mathBlock, mathInline, footnoteRef]
});

/* --- sanitiser ------------------------------------------------------- */
DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A' && node.getAttribute('target') === '_blank') {
    node.setAttribute('rel', 'noopener noreferrer');
  }
});

function toSafeHtml(markdownHtml) {
  return DOMPurify.sanitize(markdownHtml, {
    ADD_ATTR: ['target', 'id', 'align', 'colspan', 'rowspan', 'start', 'checked', 'disabled'],
    ADD_TAGS: ['details', 'summary'],
    ALLOW_DATA_ATTR: true
  });
}

/* ======================================================================
   Rendering
   ====================================================================== */

const renderedEl = $('#rendered');
const rawEl = $('#raw-code');
const RAW_HIGHLIGHT_LIMIT = 400 * 1024;

/** Split highlight.js output into lines, re-opening spans that cross a break. */
function splitHighlightedLines(html) {
  const lines = [];
  const stack = [];
  let cur = '';
  const tagRe = /<\/?[a-zA-Z][^>]*>/g;
  let last = 0, m;

  const pushText = (txt) => {
    const parts = txt.split('\n');
    for (let i = 0; i < parts.length; i++) {
      if (i > 0) {
        cur += '</span>'.repeat(stack.length);
        lines.push(cur);
        cur = stack.join('');
      }
      cur += parts[i];
    }
  };

  while ((m = tagRe.exec(html)) !== null) {
    pushText(html.slice(last, m.index));
    const tag = m[0];
    if (tag[1] === '/') { stack.pop(); } else { stack.push(tag); }
    cur += tag;
    last = tagRe.lastIndex;
  }
  pushText(html.slice(last));
  lines.push(cur);
  return lines;
}

function renderRaw(src) {
  let html;
  if (src.length > RAW_HIGHLIGHT_LIMIT) {
    html = escapeHtml(src);
  } else {
    try { html = hljs.highlight(src, { language: 'markdown', ignoreIllegals: true }).value; }
    catch (_) { html = escapeHtml(src); }
  }
  const lines = splitHighlightedLines(html);
  const frag = [];
  for (let i = 0; i < lines.length; i++) {
    frag.push(`<div class="raw-line"><span class="n">${i + 1}</span><span class="t">${lines[i] || ''}</span></div>`);
  }
  rawEl.innerHTML = frag.join('');
}

function renderMarkdown(src) {
  slugCounts = Object.create(null);
  footnoteOrder = [];

  const fmResult = extractFrontMatter(src);
  const fnResult = extractFootnotes(fmResult.text);
  footnoteDefs = fnResult.defs;

  let html = marked.parse(fnResult.text);
  html += footnoteHtml();
  if (fmResult.fm !== null) html = frontMatterHtml(fmResult.fm) + html;

  renderedEl.innerHTML = toSafeHtml(html);

  upgradeAlerts();
  wireCopyButtons();
  resolveImages();
  renderMath();
  renderMermaid(false);
  buildOutline();
}

/* --- GitHub alerts: > [!NOTE] ---------------------------------------- */
const ALERT_ICONS = {
  note:      '<path d="M8 1.5a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13z"/><path d="M8 7.2v4M8 4.9h.01"/>',
  tip:       '<path d="M8 1.5a4.5 4.5 0 0 0-2.6 8.2V12h5.2V9.7A4.5 4.5 0 0 0 8 1.5z"/><path d="M6.6 14h2.8"/>',
  important: '<path d="M2 3.5h12v8H8.8L6 14v-2.5H2z"/><path d="M8 5.6v3M8 10.1h.01"/>',
  warning:   '<path d="M8 2 1.8 13h12.4z"/><path d="M8 6.4v3M8 11.3h.01"/>',
  caution:   '<path d="M5.6 1.8h4.8l3.8 3.8v4.8l-3.8 3.8H5.6L1.8 10.4V5.6z"/><path d="M8 5.2v3.4M8 11.1h.01"/>'
};

function upgradeAlerts() {
  renderedEl.querySelectorAll('blockquote').forEach((bq) => {
    const first = bq.firstElementChild;
    if (!first || first.tagName !== 'P') return;
    const m = /^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*/i.exec(first.textContent);
    if (!m) return;

    const kind = m[1].toLowerCase();
    // Drop the marker (and the line break that followed it) from the paragraph.
    first.innerHTML = first.innerHTML.replace(/^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(<br\s*\/?>)?\s*/i, '');

    const div = document.createElement('div');
    div.className = 'md-alert';
    div.dataset.kind = kind;
    const title = document.createElement('p');
    title.className = 'md-alert-title';
    title.innerHTML = `<svg viewBox="0 0 16 16" aria-hidden="true">${ALERT_ICONS[kind]}</svg>`
      + kind.charAt(0).toUpperCase() + kind.slice(1);
    div.appendChild(title);
    while (bq.firstChild) div.appendChild(bq.firstChild);
    if (!first.textContent.trim() && !first.querySelector('img, code')) first.remove();
    bq.replaceWith(div);
  });
}

/* --- copy buttons ----------------------------------------------------- */
function wireCopyButtons() {
  renderedEl.querySelectorAll('.copy-btn').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const code = btn.parentElement.querySelector('code');
      if (!code) return;
      try {
        await navigator.clipboard.writeText(code.textContent);
      } catch (_) {
        const ta = document.createElement('textarea');
        ta.value = code.textContent;
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand('copy'); } catch (__) { /* nothing else to try */ }
        ta.remove();
      }
      btn.classList.add('is-done');
      const label = btn.lastChild;
      label.textContent = 'Copied';
      setTimeout(() => { btn.classList.remove('is-done'); label.textContent = 'Copy'; }, 1400);
    });
  });
}

/* --- relative images -------------------------------------------------- */
function normalisePath(p) {
  const parts = [];
  for (const seg of p.split('/')) {
    if (!seg || seg === '.') continue;
    if (seg === '..') parts.pop();
    else parts.push(seg);
  }
  return parts.join('/');
}

async function resolveImages() {
  const doc = activeDoc();
  if (!doc || !state.assets.size) return;

  const imgs = Array.from(renderedEl.querySelectorAll('img[src]'));
  for (const img of imgs) {
    const src = img.getAttribute('src');
    if (!src || /^(https?:|data:|blob:|\/\/)/i.test(src)) continue;

    const key = normalisePath((doc.dir ? doc.dir + '/' : '') + decodeURIComponent(src.split(/[?#]/)[0]));
    const asset = state.assets.get(key) || state.assets.get(normalisePath(decodeURIComponent(src)));
    if (!asset) {
      img.addEventListener('error', () => {
        img.classList.add('is-broken');
        img.replaceWith(Object.assign(document.createElement('span'), {
          className: 'md-missing-image',
          textContent: `[image not found: ${src}]`,
          style: 'color:var(--text-mute);font-style:italic;font-size:.9em'
        }));
      }, { once: true });
      continue;
    }
    try {
      const file = asset instanceof File ? asset : await asset.getFile();
      const url = URL.createObjectURL(file);
      state.objectUrls.push(url);
      img.src = url;
    } catch (_) { /* leave the original src in place */ }
  }
}

function revokeObjectUrls() {
  state.objectUrls.forEach((u) => URL.revokeObjectURL(u));
  state.objectUrls = [];
}

/* --- KaTeX (lazy) ----------------------------------------------------- */
async function renderMath() {
  const nodes = renderedEl.querySelectorAll('.math-inline, .math-block');
  if (!nodes.length) return;
  try {
    await Promise.all([loadStyle(CDN.katexCss), loadScript(CDN.katexJs)]);
  } catch (_) {
    toast('Math needs an internet connection the first time');
    return;
  }
  nodes.forEach((el) => {
    const tex = el.textContent;
    try {
      window.katex.render(tex, el, {
        displayMode: el.classList.contains('math-block'),
        throwOnError: false,
        output: 'html'
      });
    } catch (err) {
      el.classList.add('math-error');
      el.textContent = tex;
    }
  });
}

/* --- Mermaid (lazy) --------------------------------------------------- */
async function renderMermaid(force) {
  const blocks = renderedEl.querySelectorAll('.mermaid-block');
  if (!blocks.length) return;

  const theme = document.documentElement.dataset.resolved === 'dark' ? 'dark' : 'default';
  if (!force && state.mermaidTheme === theme && !renderedEl.querySelector('.mermaid-block[data-state="pending"]')) return;

  blocks.forEach((b) => {
    if (!b.dataset.src) b.dataset.src = b.textContent;
    if (force || b.dataset.state === 'pending') {
      b.dataset.state = 'loading';
      b.textContent = 'Rendering diagram…';
    }
  });

  try { await loadScript(CDN.mermaid); }
  catch (_) {
    blocks.forEach((b) => {
      b.dataset.state = 'error';
      b.innerHTML = `<pre style="text-align:left;margin:0"><code>${escapeHtml(b.dataset.src || '')}</code></pre>`;
    });
    toast('Diagrams need an internet connection the first time');
    return;
  }

  window.mermaid.initialize({ startOnLoad: false, theme, securityLevel: 'strict', fontFamily: 'inherit' });
  state.mermaidTheme = theme;

  for (const b of blocks) {
    const src = b.dataset.src || '';
    try {
      const { svg } = await window.mermaid.render('mmd-' + (state.seq++), src);
      b.innerHTML = svg;
      b.dataset.state = 'done';
    } catch (err) {
      b.dataset.state = 'error';
      b.innerHTML = `<pre style="text-align:left;margin:0"><code>${escapeHtml(src)}</code></pre>`;
    }
  }
}

/* ======================================================================
   Outline
   ====================================================================== */

let headings = [];

function buildOutline() {
  const list = $('#outline-list');
  headings = Array.from(renderedEl.querySelectorAll('h1, h2, h3, h4, h5, h6'));

  if (!headings.length) {
    list.innerHTML = '<p class="outline-empty">No headings in this document.</p>';
    return;
  }
  list.innerHTML = headings.map((h, i) => {
    const text = h.textContent.replace(/^#/, '').trim();
    return `<button class="outline-link" data-level="${h.tagName[1]}" data-i="${i}" title="${escapeHtml(text)}">${escapeHtml(text)}</button>`;
  }).join('');

  list.querySelectorAll('.outline-link').forEach((btn) => {
    btn.addEventListener('click', () => {
      const h = headings[+btn.dataset.i];
      if (h) scrollPaneTo(h);
    });
  });
  updateOutlineActive();
}

function scrollPaneTo(el) {
  const scroller = $('#rendered-scroll');
  if (state.view === 'raw') setView('split');
  scroller.scrollTo({ top: el.offsetTop - 12, behavior: 'smooth' });
}

function updateOutlineActive() {
  if (!headings.length || $('#outline').hidden) return;
  const top = $('#rendered-scroll').scrollTop + 24;
  let idx = 0;
  for (let i = 0; i < headings.length; i++) {
    if (headings[i].offsetTop <= top) idx = i; else break;
  }
  $$('.outline-link').forEach((b, i) => b.classList.toggle('is-current', i === idx));
  const current = $('.outline-link.is-current');
  if (current) {
    const box = $('#outline-list');
    const cTop = current.offsetTop, cBot = cTop + current.offsetHeight;
    if (cTop < box.scrollTop || cBot > box.scrollTop + box.clientHeight) {
      box.scrollTop = cTop - box.clientHeight / 2;
    }
  }
}

/* ======================================================================
   Documents
   ====================================================================== */

function statsFor(text) {
  const words = (text.match(/[\p{L}\p{N}][\p{L}\p{N}'’-]*/gu) || []).length;
  const lines = text.split('\n').length;
  return { words, lines, minutes: Math.max(1, Math.round(words / 220)) };
}

function showDoc(id) {
  const doc = state.docs.find((d) => d.id === id);
  if (!doc) return;

  revokeObjectUrls();
  state.activeId = id;
  $('#app').classList.add('has-doc');

  const title = $('#doc-title');
  title.classList.remove('is-empty');
  title.innerHTML = `<span class="doc-name">${escapeHtml(doc.name)}</span>`
    + (doc.dir ? `<span class="doc-path">${escapeHtml(doc.dir)}</span>` : '');
  title.title = doc.path;
  document.title = doc.name + ' — Markdown Viewer';

  renderRaw(doc.text);
  renderMarkdown(doc.text);

  const s = statsFor(doc.text);
  $('#st-words').textContent = s.words.toLocaleString() + (s.words === 1 ? ' word' : ' words');
  $('#st-lines').textContent = s.lines.toLocaleString() + ' lines · ' + formatBytes(doc.size);
  $('#st-read').textContent = '~' + s.minutes + ' min read';

  $('#raw-scroll').scrollTop = 0;
  $('#rendered-scroll').scrollTop = 0;

  renderFileList();
  setupWatch();
}

function addDocs(entries, { activate = true, quiet = false } = {}) {
  let firstNew = null;
  let added = 0;

  for (const e of entries) {
    const path = e.path || e.name;
    const existing = state.docs.find((d) => d.path === path);
    const slash = path.lastIndexOf('/');
    const doc = {
      id: existing ? existing.id : 'd' + (state.seq++),
      name: path.slice(slash + 1),
      dir: slash > 0 ? path.slice(0, slash) : '',
      path,
      text: e.text,
      size: e.size != null ? e.size : new Blob([e.text]).size,
      handle: e.handle || null,
      lastModified: e.lastModified || 0
    };
    if (existing) Object.assign(existing, doc);
    else { state.docs.push(doc); added++; }
    if (!firstNew) firstNew = doc.id;
  }

  renderFileList();
  if (activate && firstNew) showDoc(firstNew);
  if (!quiet && added > 1) toast(added + ' files opened');
  return firstNew;
}

function renderFileList() {
  const list = $('#file-list');
  const query = $('#file-filter').value.trim().toLowerCase();
  const docs = query
    ? state.docs.filter((d) => d.path.toLowerCase().includes(query))
    : state.docs;

  $('#file-count').textContent = state.docs.length
    ? state.docs.length + (state.docs.length === 1 ? ' file' : ' files')
    : 'No files';
  $('#btn-clear').hidden = !state.docs.length;

  if (!docs.length) {
    list.innerHTML = state.docs.length
      ? '<p class="sidebar-empty">Nothing matches that filter.</p>'
      : '<p class="sidebar-empty">Drop Markdown files anywhere in the window, or use the buttons above.</p>';
    return;
  }

  const groups = new Map();
  for (const d of docs) {
    if (!groups.has(d.dir)) groups.set(d.dir, []);
    groups.get(d.dir).push(d);
  }

  const highlight = (name) => {
    if (!query) return escapeHtml(name);
    const i = name.toLowerCase().indexOf(query);
    if (i < 0) return escapeHtml(name);
    return escapeHtml(name.slice(0, i)) + '<mark>' + escapeHtml(name.slice(i, i + query.length))
      + '</mark>' + escapeHtml(name.slice(i + query.length));
  };

  let html = '';
  for (const [dir, items] of groups) {
    if (groups.size > 1 || dir) html += `<div class="file-group" title="${escapeHtml(dir || 'Loose files')}">${escapeHtml(dir || 'Loose files')}</div>`;
    for (const d of items) {
      html += `<button class="file-item${d.id === state.activeId ? ' is-active' : ''}" data-id="${d.id}" title="${escapeHtml(d.path)}">`
        + `<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M9.3 1.8H4.2a1.4 1.4 0 0 0-1.4 1.4v9.6a1.4 1.4 0 0 0 1.4 1.4h7.6a1.4 1.4 0 0 0 1.4-1.4V5.6z"/><path d="M9.3 1.8v3.8h3.9"/></svg>`
        + `<span class="name">${highlight(d.name)}</span></button>`;
    }
  }
  list.innerHTML = html;

  list.querySelectorAll('.file-item').forEach((btn) => {
    btn.addEventListener('click', () => showDoc(btn.dataset.id));
  });
}

function clearDocs() {
  revokeObjectUrls();
  stopWatch();
  state.docs = [];
  state.assets.clear();
  state.activeId = null;
  renderedEl.innerHTML = '';
  rawEl.innerHTML = '';
  $('#app').classList.remove('has-doc');
  $('#doc-title').innerHTML = '<span class="doc-name">No document</span>';
  $('#doc-title').classList.add('is-empty');
  $('#st-words').textContent = 'Ready';
  $('#st-lines').textContent = '';
  $('#st-read').textContent = '';
  document.title = 'Markdown Viewer';
  renderFileList();
  buildOutline();
}

/* ======================================================================
   File input: pickers, drag & drop, paste
   ====================================================================== */

const isMarkdown = (name) => MD_EXT.test(name);
const isImage = (name) => /\.(png|jpe?g|gif|svg|webp|avif|bmp|ico)$/i.test(name);

async function readFile(file, path) {
  return {
    path: path || file.webkitRelativePath || file.name,
    text: await file.text(),
    size: file.size,
    lastModified: file.lastModified
  };
}

/* --- File System Access API (gives us live handles) -------------------- */
const hasFsa = typeof window.showOpenFilePicker === 'function';

async function walkDirectory(dirHandle, prefix, out, depth = 0) {
  if (depth > 6) return;
  for await (const [name, handle] of dirHandle.entries()) {
    if (name.startsWith('.') || name === 'node_modules') continue;
    const path = prefix ? prefix + '/' + name : name;
    if (handle.kind === 'directory') await walkDirectory(handle, path, out, depth + 1);
    else out.push({ path, handle });
  }
}

async function ingestHandles(items) {
  const docs = [];
  for (const { path, handle } of items) {
    if (isImage(path)) { state.assets.set(normalisePath(path), handle); continue; }
    if (!isMarkdown(path)) continue;
    try {
      const file = await handle.getFile();
      docs.push({ ...(await readFile(file, path)), handle });
    } catch (_) { /* skip unreadable files */ }
  }
  return docs;
}

async function pickFiles() {
  if (hasFsa) {
    try {
      const handles = await window.showOpenFilePicker({
        multiple: true,
        types: [{ description: 'Markdown', accept: { 'text/markdown': ['.md', '.markdown', '.mdown', '.mkd', '.mdx'], 'text/plain': ['.txt'] } }]
      });
      const docs = await ingestHandles(handles.map((h) => ({ path: h.name, handle: h })));
      if (docs.length) addDocs(docs);
      return;
    } catch (err) {
      if (err && err.name === 'AbortError') return;
      /* fall through to the classic input */
    }
  }
  $('#input-files').click();
}

async function pickFolder() {
  if (typeof window.showDirectoryPicker === 'function') {
    try {
      const dir = await window.showDirectoryPicker();
      const found = [];
      await walkDirectory(dir, dir.name, found);
      const docs = await ingestHandles(found);
      if (docs.length) { addDocs(docs); toast(docs.length + ' Markdown files found'); }
      else toast('No Markdown files in that folder');
      return;
    } catch (err) {
      if (err && err.name === 'AbortError') return;
    }
  }
  $('#input-folder').click();
}

/* --- classic <input type=file> ----------------------------------------- */
async function ingestFileList(fileList) {
  const files = Array.from(fileList);
  const docs = [];
  for (const f of files) {
    const path = f.webkitRelativePath || f.name;
    if (isImage(path)) { state.assets.set(normalisePath(path), f); continue; }
    if (!isMarkdown(path)) continue;
    docs.push(await readFile(f, path));
  }
  if (docs.length) addDocs(docs);
  else toast('No Markdown files found');
}

/* --- drag & drop -------------------------------------------------------- */
function entryToFile(entry) {
  return new Promise((resolve) => entry.file(resolve, () => resolve(null)));
}

function readEntries(reader) {
  return new Promise((resolve) => reader.readEntries(resolve, () => resolve([])));
}

async function walkEntry(entry, prefix, out, depth = 0) {
  if (!entry || depth > 6) return;
  const path = prefix ? prefix + '/' + entry.name : entry.name;
  if (entry.isFile) {
    const file = await entryToFile(entry);
    if (file) out.push({ file, path });
    return;
  }
  if (entry.isDirectory) {
    if (entry.name.startsWith('.') || entry.name === 'node_modules') return;
    const reader = entry.createReader();
    let batch;
    do {
      batch = await readEntries(reader);
      for (const child of batch) await walkEntry(child, path, out, depth + 1);
    } while (batch.length);
  }
}

async function handleDrop(dt) {
  const items = dt.items ? Array.from(dt.items).filter((i) => i.kind === 'file') : [];

  // 1. Preferred: real handles, which allow auto-reload.
  if (items.length && typeof items[0].getAsFileSystemHandle === 'function') {
    try {
      const handles = (await Promise.all(items.map((i) => i.getAsFileSystemHandle()))).filter(Boolean);
      if (handles.length) {
        const found = [];
        for (const h of handles) {
          if (h.kind === 'directory') await walkDirectory(h, h.name, found);
          else found.push({ path: h.name, handle: h });
        }
        const docs = await ingestHandles(found);
        if (docs.length) { addDocs(docs); return; }
        if (found.length) { toast('No Markdown files found'); return; }
      }
    } catch (_) { /* not available on file:// — fall through */ }
  }

  // 2. Directory entries (works from file:// too).
  const entries = items.map((i) => (typeof i.webkitGetAsEntry === 'function' ? i.webkitGetAsEntry() : null)).filter(Boolean);
  if (entries.length) {
    const found = [];
    for (const e of entries) await walkEntry(e, '', found);
    const docs = [];
    for (const { file, path } of found) {
      if (isImage(path)) { state.assets.set(normalisePath(path), file); continue; }
      if (isMarkdown(path)) docs.push(await readFile(file, path));
    }
    if (docs.length) { addDocs(docs); return; }
    if (found.length) { toast('No Markdown files found'); return; }
  }

  // 3. Plain file list.
  if (dt.files && dt.files.length) { await ingestFileList(dt.files); return; }
  toast('Nothing to open');
}

/* --- auto-reload -------------------------------------------------------- */
function stopWatch() {
  clearInterval(state.watchTimer);
  state.watchTimer = null;
}

function setupWatch() {
  stopWatch();
  const doc = activeDoc();
  const btn = $('#btn-watch');
  btn.hidden = !(doc && doc.handle);
  if (!doc || !doc.handle) return;

  btn.classList.toggle('is-on', state.watching);
  btn.setAttribute('aria-pressed', String(state.watching));
  if (!state.watching) return;

  state.watchTimer = setInterval(async () => {
    const current = activeDoc();
    if (!current || !current.handle) return stopWatch();
    try {
      const file = await current.handle.getFile();
      if (file.lastModified === current.lastModified) return;
      const scrollRatio = getScrollRatio($('#rendered-scroll'));
      current.text = await file.text();
      current.size = file.size;
      current.lastModified = file.lastModified;
      renderRaw(current.text);
      renderMarkdown(current.text);
      const s = statsFor(current.text);
      $('#st-words').textContent = s.words.toLocaleString() + ' words';
      $('#st-lines').textContent = s.lines.toLocaleString() + ' lines · ' + formatBytes(current.size);
      $('#st-read').textContent = '~' + s.minutes + ' min read';
      requestAnimationFrame(() => setScrollRatio($('#rendered-scroll'), scrollRatio));
      toast('Reloaded ' + current.name);
    } catch (_) { /* file busy or removed; try again next tick */ }
  }, 1200);
}

function getScrollRatio(el) {
  const max = el.scrollHeight - el.clientHeight;
  return max > 0 ? el.scrollTop / max : 0;
}
function setScrollRatio(el, ratio) {
  const max = el.scrollHeight - el.clientHeight;
  if (max > 0) el.scrollTop = ratio * max;
}

/* ======================================================================
   View mode, splitter, scroll sync
   ====================================================================== */

function setView(view) {
  state.view = view;
  store.set('view', view);
  $('#panes').dataset.view = view;
  $$('#segmented button').forEach((b) => b.classList.toggle('is-active', b.dataset.view === view));
  $('#btn-sync').style.display = view === 'split' ? '' : 'none';
}

function setSplit(pct) {
  state.split = Math.min(85, Math.max(15, pct));
  $('#panes').style.setProperty('--split', state.split + '%');
  $('#gutter').setAttribute('aria-valuenow', Math.round(state.split));
  store.set('split', state.split);
}

function initGutter() {
  const gutter = $('#gutter');
  const panes = $('#panes');

  let dragging = false;

  const startDrag = (e) => {
    if (dragging) return;           // pointerdown and mousedown both fire
    dragging = true;
    e.preventDefault();
    $('#app').classList.add('is-dragging');

    const move = (ev) => {
      const rect = panes.getBoundingClientRect();
      setSplit(((ev.clientX - rect.left) / rect.width) * 100);
    };
    const stop = () => {
      dragging = false;
      $('#app').classList.remove('is-dragging');
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', stop);
      window.removeEventListener('pointercancel', stop);
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', stop);
    };
    // Listening on window (rather than relying on pointer capture) keeps the
    // drag alive even when the cursor outruns the 9px gutter.
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', stop);
    window.addEventListener('pointercancel', stop);
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', stop);
  };

  gutter.addEventListener('pointerdown', startDrag);
  gutter.addEventListener('mousedown', (e) => { if (e.button === 0) startDrag(e); });

  gutter.addEventListener('dblclick', () => { setSplit(50); toast('Panes reset'); });

  gutter.addEventListener('keydown', (e) => {
    const step = e.shiftKey ? 10 : 2;
    if (e.key === 'ArrowLeft')  { setSplit(state.split - step); e.preventDefault(); }
    if (e.key === 'ArrowRight') { setSplit(state.split + step); e.preventDefault(); }
    if (e.key === 'Home')       { setSplit(25); e.preventDefault(); }
    if (e.key === 'End')        { setSplit(75); e.preventDefault(); }
    if (e.key === 'Enter' || e.key === ' ') { setSplit(50); e.preventDefault(); }
  });
}

function initScrollSync() {
  const raw = $('#raw-scroll');
  const rendered = $('#rendered-scroll');
  let lock = 0;

  const mirror = (src, dst) => {
    if (!state.sync || state.view !== 'split') return;
    if (lock) return;
    lock = 1;
    setScrollRatio(dst, getScrollRatio(src));
    requestAnimationFrame(() => { lock = 0; });
  };

  raw.addEventListener('scroll', () => mirror(raw, rendered), { passive: true });
  rendered.addEventListener('scroll', () => {
    mirror(rendered, raw);
    updateOutlineActive();
  }, { passive: true });
}

/* ======================================================================
   Wiring
   ====================================================================== */

function toggleSidebar(force) {
  const app = $('#app');
  const hidden = force !== undefined ? !force : !app.classList.contains('sidebar-hidden');
  app.classList.toggle('sidebar-hidden', hidden);
  store.set('sidebar', !hidden);
}

function toggleOutline(force) {
  const panel = $('#outline');
  const open = force !== undefined ? force : panel.hidden;
  panel.hidden = !open;
  $('#app').classList.toggle('outline-open', open);
  $('#btn-outline').setAttribute('aria-pressed', String(open));
  store.set('outline', open);
  if (open) updateOutlineActive();
}

function initEvents() {
  // View modes
  $$('#segmented button').forEach((b) => b.addEventListener('click', () => setView(b.dataset.view)));

  // Toolbar
  $('#btn-theme').addEventListener('click', cycleTheme);
  $('#btn-outline').addEventListener('click', () => toggleOutline());
  $('#btn-outline-close').addEventListener('click', () => toggleOutline(false));
  $('#btn-collapse').addEventListener('click', () => toggleSidebar(false));
  $('#btn-expand').addEventListener('click', () => toggleSidebar(true));

  // Files
  $('#btn-open-files').addEventListener('click', pickFiles);
  $('#btn-open-folder').addEventListener('click', pickFolder);
  $('#empty-open').addEventListener('click', pickFiles);
  $('#empty-folder').addEventListener('click', pickFolder);
  $('#empty-sample').addEventListener('click', () => {
    addDocs([{ path: 'sample.md', text: SAMPLE }]);
  });
  $('#btn-clear').addEventListener('click', clearDocs);
  $('#file-filter').addEventListener('input', renderFileList);
  $('#input-files').addEventListener('change', (e) => { ingestFileList(e.target.files); e.target.value = ''; });
  $('#input-folder').addEventListener('change', (e) => { ingestFileList(e.target.files); e.target.value = ''; });

  // Status bar
  $('#btn-sync').addEventListener('click', () => {
    state.sync = !state.sync;
    store.set('sync', state.sync);
    $('#btn-sync').classList.toggle('is-on', state.sync);
    $('#btn-sync').setAttribute('aria-pressed', String(state.sync));
    toast('Scroll sync ' + (state.sync ? 'on' : 'off'));
  });
  $('#btn-watch').addEventListener('click', () => {
    state.watching = !state.watching;
    setupWatch();
    toast('Auto-reload ' + (state.watching ? 'on' : 'off'));
  });
  $('#btn-print').addEventListener('click', () => window.print());

  // Drag & drop
  let dragDepth = 0;
  const overlay = $('#drop-overlay');
  window.addEventListener('dragenter', (e) => {
    if (!e.dataTransfer || !Array.from(e.dataTransfer.types).includes('Files')) return;
    dragDepth++;
    overlay.hidden = false;
  });
  window.addEventListener('dragover', (e) => {
    if (!overlay.hidden) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }
  });
  window.addEventListener('dragleave', () => {
    if (--dragDepth <= 0) { dragDepth = 0; overlay.hidden = true; }
  });
  window.addEventListener('drop', (e) => {
    if (overlay.hidden) return;
    e.preventDefault();
    dragDepth = 0;
    overlay.hidden = true;
    handleDrop(e.dataTransfer);
  });

  // Paste
  window.addEventListener('paste', (e) => {
    const tag = (e.target && e.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA') return;
    const text = e.clipboardData && e.clipboardData.getData('text/plain');
    if (!text || !text.trim()) return;
    e.preventDefault();
    const n = state.docs.filter((d) => /^Pasted/.test(d.name)).length + 1;
    addDocs([{ path: 'Pasted ' + n + '.md', text }]);
    toast('Pasted Markdown opened');
  });

  // In-document links
  renderedEl.addEventListener('click', (e) => {
    const a = e.target.closest('a[href]');
    if (!a) return;
    const href = a.getAttribute('href') || '';

    if (href.startsWith('#')) {
      e.preventDefault();
      const target = renderedEl.querySelector('[id="' + CSS.escape(href.slice(1)) + '"]');
      if (target) {
        scrollPaneTo(target);
        history.replaceState(null, '', ' ');
      }
      return;
    }
    if (/^(https?:|mailto:|data:)/i.test(href)) return; // let the browser handle it

    // Relative link to another open document
    const doc = activeDoc();
    const key = normalisePath((doc && doc.dir ? doc.dir + '/' : '') + decodeURIComponent(href.split(/[?#]/)[0]));
    const other = state.docs.find((d) => normalisePath(d.path) === key);
    if (other) { e.preventDefault(); showDoc(other.id); }
  });

  // Keyboard
  window.addEventListener('keydown', (e) => {
    const tag = (e.target && e.target.tagName) || '';
    const typing = tag === 'INPUT' || tag === 'TEXTAREA' || e.target.isContentEditable;
    const mod = e.ctrlKey || e.metaKey;

    if (mod && e.key.toLowerCase() === 'o') { e.preventDefault(); pickFiles(); return; }
    if (mod && e.key.toLowerCase() === 'b') { e.preventDefault(); toggleSidebar(); return; }
    if (mod && e.key.toLowerCase() === 'p') { e.preventDefault(); window.print(); return; }
    if (mod && e.key.toLowerCase() === 'k') { e.preventDefault(); toggleSidebar(true); $('#file-filter').focus(); $('#file-filter').select(); return; }

    if (e.key === 'Escape' && tag === 'INPUT') { e.target.value = ''; e.target.blur(); renderFileList(); return; }
    if (typing || mod || e.altKey) return;

    if (e.key === '1') setView('raw');
    else if (e.key === '2') setView('split');
    else if (e.key === '3') setView('rendered');
    else if (e.key === '4') toggleOutline();
    else if (e.key === 't') cycleTheme();
    else if (e.key === '[' || e.key === ']') {
      const i = state.docs.findIndex((d) => d.id === state.activeId);
      if (i < 0 || state.docs.length < 2) return;
      const next = (i + (e.key === ']' ? 1 : -1) + state.docs.length) % state.docs.length;
      showDoc(state.docs[next].id);
    }
  });

  window.addEventListener('beforeunload', revokeObjectUrls);
}

/* ------------------------------------------------------------------- boot */

function init() {
  resolveTheme();
  setView(state.view);
  setSplit(state.split);
  toggleSidebar(store.get('sidebar', true));
  toggleOutline(store.get('outline', false));

  $('#btn-sync').classList.toggle('is-on', state.sync);
  $('#btn-sync').setAttribute('aria-pressed', String(state.sync));
  $('#doc-title').classList.add('is-empty');

  initGutter();
  initScrollSync();
  initEvents();
  renderFileList();
}

/* ------------------------------------------------------------- sample doc */

const SAMPLE = [
'---',
'title: Markdown Viewer — sample document',
'author: Markdown Viewer',
'status: draft',
'---',
'',
'# Markdown Viewer',
'',
'This sample exercises every construct the viewer knows about. Toggle **Raw**, ',
'**Split** and **Rendered** in the toolbar, and drag the divider between the panes.',
'',
'## Text',
'',
'Regular prose with *emphasis*, **strong emphasis**, ***both***, `inline code`, ',
'~~strikethrough~~, a [link](https://commonmark.org), and a footnote.[^1]',
'',
'[^1]: Footnotes are collected at the end of the document.',
'',
'> A blockquote, for when someone else said it better.',
'> — *Anonymous*',
'',
'## Alerts',
'',
'> [!NOTE]',
'> Useful information a reader should notice even when skimming.',
'',
'> [!TIP]',
'> Optional advice that helps a reader be more successful.',
'',
'> [!IMPORTANT]',
'> Key information the reader needs to achieve their goal.',
'',
'> [!WARNING]',
'> Urgent info that needs immediate attention.',
'',
'> [!CAUTION]',
'> Advises about risks or negative outcomes.',
'',
'## Lists',
'',
'- Unordered item',
'- Another item',
'  - Nested item',
'    - Deeper still',
'',
'1. First',
'2. Second',
'3. Third',
'',
'- [x] Task lists work',
'- [x] Including checked boxes',
'- [ ] And unchecked ones',
'',
'## Code',
'',
'```javascript',
'// Syntax highlighting covers ~40 common languages.',
'export function greet(name = "world") {',
'  const greeting = `Hello, ${name}!`;',
'  return greeting.toUpperCase();',
'}',
'```',
'',
'```python',
'from dataclasses import dataclass',
'',
'@dataclass',
'class Point:',
'    x: float',
'    y: float',
'',
'    def norm(self) -> float:',
'        return (self.x ** 2 + self.y ** 2) ** 0.5',
'```',
'',
'```diff',
'- const old = "removed";',
'+ const shiny = "added";',
'```',
'',
'## Tables',
'',
'| Feature          | Raw | Rendered | Notes                          |',
'| ---------------- | :-: | :------: | ------------------------------ |',
'| Headings         |  ✓  |    ✓     | Anchors appear on hover        |',
'| Tables           |  ✓  |    ✓     | Scroll horizontally if wide    |',
'| Task lists       |  ✓  |    ✓     | GitHub Flavored Markdown       |',
'| Diagrams         |  ✓  |    ✓     | Mermaid, loaded on demand      |',
'',
'## Diagrams',
'',
'```mermaid',
'flowchart LR',
'  A[Markdown file] --> B{View mode}',
'  B -->|Raw| C[Highlighted source]',
'  B -->|Rendered| D[Formatted document]',
'  B -->|Split| E[Both, side by side]',
'```',
'',
'## Math',
'',
'Inline math such as $e^{i\\pi} + 1 = 0$ sits in the flow of a sentence, while',
'display math gets its own line:',
'',
'$$',
'\\int_{-\\infty}^{\\infty} e^{-x^2}\\,dx = \\sqrt{\\pi}',
'$$',
'',
'---',
'',
'That is the whole feature set. Drop your own files anywhere in the window.',
''
].join('\n');

/* --------------------------------------------------------------------- go */

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
else init();

})();
