import { memo } from 'preact/compat';
import { renderMarkdownBlock, splitMarkdownBlocks } from '../runtime/markdown';

interface Props {
  text: string;
  streaming?: boolean;
}

const StaticBlock = memo(function StaticBlock({ html }: { html: string }) {
  return <div class="md-block" dangerouslySetInnerHTML={{ __html: html }} />;
});

export const MarkdownView = memo(function MarkdownView({ text, streaming = false }: Props) {
  const blocks = splitMarkdownBlocks(text || '');

  const handleClick = (e: MouseEvent) => {
    const target = e.target as HTMLElement | null;
    if (!target || !target.dataset.copy) return;
    const pre = target.closest('pre');
    const code = pre?.querySelector('code')?.textContent || '';
    if (!code) return;
    navigator.clipboard?.writeText(code).then(() => {
      const orig = target.textContent;
      target.textContent = '已复制';
      window.setTimeout(() => {
        target.textContent = orig || '复制';
      }, 1200);
    });
  };

  return (
    <div class="md-root" onClick={handleClick}>
      {blocks.map((block, index) => {
        const isLast = index === blocks.length - 1;
        const html = renderMarkdownBlock(block, !(streaming && isLast));
        return <StaticBlock key={`${index}-${block.length}`} html={html} />;
      })}
    </div>
  );
});
