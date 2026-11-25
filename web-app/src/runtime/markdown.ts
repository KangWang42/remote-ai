/**
 * Incremental streaming Markdown renderer inspired by T3 Code's block-splitting cache:
 * splits Markdown at top-level blank lines outside code fences, caches every closed prefix block's
 * sanitized HTML string, and only re-parses the active tail block while tokens stream in.
 */

const BLOCK_CACHE_LIMIT = 400;
const blockCache = new Map<string, string>();

function escapeHtml(raw: string): string {
  return raw
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function renderInline(raw: string): string {
  let out = escapeHtml(raw);
  // Inline code spans first
  const codeSpans: string[] = [];
  out = out.replace(/`([^`\n]+)`/g, (_, code: string) => {
    const idx = codeSpans.length;
    codeSpans.push(`<code>${code}</code>`);
    return `\u0000C${idx}\u0000`;
  });
  // Safe links (http/https only)
  out = out.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
  // Strong / emphasis / strikethrough
  out = out.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
  out = out.replace(/\*([^*\n]+)\*/g, '<em>$1</em>');
  out = out.replace(/~~([^~\n]+)~~/g, '<del>$1</del>');
  // Restore code spans
  out = out.replace(/\u0000C(\d+)\u0000/g, (_, i) => codeSpans[Number(i)] || '');
  return out;
}

function renderBlock(block: string): string {
  const trimmed = block.trim();
  if (!trimmed) return '';

  // Fenced code block
  const fence = trimmed.match(/^```([A-Za-z0-9_+-]*)\n?([\s\S]*?)(?:\n```)?$/);
  if (fence && trimmed.startsWith('```')) {
    const lang = escapeHtml(fence[1] || '');
    const code = escapeHtml(fence[2].replace(/\n```$/, ''));
    return `<pre class="md-pre"><div class="md-pre-head"><span>${lang || 'text'}</span><button type="button" class="md-copy" data-copy="1">复制</button></div><code>${code}</code></pre>`;
  }

  // Heading
  const heading = trimmed.match(/^(#{1,4})\s+(.+)$/);
  if (heading && !trimmed.includes('\n')) {
    const level = heading[1].length;
    return `<h${level} class="md-h">${renderInline(heading[2])}</h${level}>`;
  }

  const lines = trimmed.split('\n');

  // Blockquote
  if (lines.every((l) => l.startsWith('>'))) {
    const inner = lines.map((l) => renderInline(l.replace(/^>\s?/, ''))).join('<br>');
    return `<blockquote class="md-quote">${inner}</blockquote>`;
  }

  // Unordered or ordered list
  if (lines.every((l) => /^\s*[-*+]\s+/.test(l))) {
    const items = lines.map((l) => `<li>${renderInline(l.replace(/^\s*[-*+]\s+/, ''))}</li>`).join('');
    return `<ul class="md-list">${items}</ul>`;
  }
  if (lines.every((l) => /^\s*\d+\.\s+/.test(l))) {
    const items = lines.map((l) => `<li>${renderInline(l.replace(/^\s*\d+\.\s+/, ''))}</li>`).join('');
    return `<ol class="md-list">${items}</ol>`;
  }

  // Simple GFM table
  if (lines.length >= 2 && lines[0].includes('|') && /^\s*\|?[\s:-]+\|/.test(lines[1])) {
    const splitRow = (row: string) =>
      row
        .trim()
        .replace(/^\||\|$/g, '')
        .split('|')
        .map((c) => renderInline(c.trim()));
    const headers = splitRow(lines[0]).map((c) => `<th>${c}</th>`).join('');
    const body = lines
      .slice(2)
      .map((r) => `<tr>${splitRow(r).map((c) => `<td>${c}</td>`).join('')}</tr>`)
      .join('');
    return `<div class="md-table-wrap"><table class="md-table"><thead><tr>${headers}</tr></thead><tbody>${body}</tbody></table></div>`;
  }

  return `<p class="md-p">${lines.map(renderInline).join('<br>')}</p>`;
}

export function splitMarkdownBlocks(text: string): string[] {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const blocks: string[] = [];
  let buf: string[] = [];
  let inFence = false;

  for (const line of lines) {
    if (/^\s*```/.test(line)) {
      if (!inFence && buf.length > 0) {
        blocks.push(buf.join('\n'));
        buf = [];
      }
      inFence = !inFence;
      buf.push(line);
      if (!inFence) {
        blocks.push(buf.join('\n'));
        buf = [];
      }
      continue;
    }
    if (!inFence && line.trim() === '') {
      if (buf.length > 0) {
        blocks.push(buf.join('\n'));
        buf = [];
      }
      continue;
    }
    buf.push(line);
  }
  if (buf.length > 0) blocks.push(buf.join('\n'));
  return blocks;
}

export function renderMarkdownBlock(block: string, cacheable: boolean): string {
  if (!cacheable) return renderBlock(block);
  const hit = blockCache.get(block);
  if (hit !== undefined) return hit;
  const rendered = renderBlock(block);
  if (blockCache.size >= BLOCK_CACHE_LIMIT) {
    const oldest = blockCache.keys().next().value;
    if (oldest !== undefined) blockCache.delete(oldest);
  }
  blockCache.set(block, rendered);
  return rendered;
}
