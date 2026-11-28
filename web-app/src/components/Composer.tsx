import type { ComponentChildren } from 'preact';
import { useEffect, useRef, useState } from 'preact/hooks';
import { uploadAttachment } from '../runtime/api';

interface Props {
  /** Drafts are kept per conversation under this key. */
  draftKey: string;
  placeholder: string;
  /** Project receiving uploaded attachments. Empty when this conversation cannot take files. */
  attachmentDir: string;
  allowFiles: boolean;
  busy: boolean;
  canStop: boolean;
  /** True when submitting appends to the local pending batch instead of firing a separate turn now. */
  queueMode?: boolean;
  /** True while a stop request is in flight: new input dispatches right after stop. */
  stopping?: boolean;
  disabled?: string;
  /** Text to put into the box, e.g. a cited file (changes when `nonce` does). */
  insert?: { nonce: number; text: string } | null;
  onSend: (text: string, pictures: string[]) => Promise<void>;
  onStop: () => Promise<void>;
  onStopAndSend?: (text: string, pictures: string[]) => Promise<void>;
  /** What stands at the end of the box before the round button (the tool and its model). */
  tail?: ComponentChildren;
}

const MAX_ATTACHMENTS = 4;

/** The box a message is written in: the words take the whole width, what is done with them sits under them. */
export function Composer(p: Props) {
  const [text, setText] = useState('');
  const [pictures, setPictures] = useState<string[]>([]);
  const [uploading, setUploading] = useState(false);
  const [sending, setSending] = useState(false);
  const [note, setNote] = useState('');
  const box = useRef<HTMLTextAreaElement | null>(null);
  const picker = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    try {
      setText(sessionStorage.getItem(p.draftKey) || '');
    } catch {
      setText('');
    }
    setPictures([]);
    setNote('');
  }, [p.draftKey]);

  useEffect(() => {
    if (!p.insert) return;
    const next = text ? `${text.trimEnd()} ${p.insert.text} ` : `${p.insert.text} `;
    keep(next);
    window.setTimeout(() => box.current?.focus(), 30);
  }, [p.insert?.nonce]);

  useEffect(() => {
    const el = box.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 260)}px`;
  }, [text]);

  const keep = (value: string) => {
    setText(value);
    try {
      if (value) sessionStorage.setItem(p.draftKey, value);
      else sessionStorage.removeItem(p.draftKey);
    } catch {
      // the draft lasts as long as the page
    }
  };

  const addFiles = async (files: File[]) => {
    if (!p.attachmentDir || uploading || sending) return;
    const chosen = files.slice(0, MAX_ATTACHMENTS - pictures.length);
    if (chosen.length === 0) return;
    setUploading(true);
    setNote(files.length > chosen.length ? '最多添加 4 个附件' : '');
    const projectBox = box.current;
    try {
      for (const file of chosen) {
        const path = await uploadAttachment(p.attachmentDir, file, p.allowFiles);
        if (box.current !== projectBox) break;
        setPictures(prev => [...prev, path]);
      }
    } catch (err: any) {
      setNote(err?.message || '附件上传失败');
    } finally {
      setUploading(false);
    }
  };

  const send = async () => {
    const words = text.trim();
    if ((!words && pictures.length === 0) || sending || uploading || p.disabled) return;
    setSending(true);
    setNote('');
    try {
      await p.onSend(words, pictures);
      if (box.current?.value === text) keep('');
      setPictures((current) => current.filter((path) => !pictures.includes(path)));
    } catch (err: any) {
      setNote(err?.message || '没有发出');
    } finally {
      setSending(false);
    }
  };

  const stop = async () => {
    setNote('');
    try {
      await p.onStop();
    } catch (err: any) {
      setNote(err?.message || '停止失败');
    }
  };

  const stopAndSend = async () => {
    const words = text.trim();
    if ((!words && pictures.length === 0) || sending || uploading || p.disabled || !p.onStopAndSend) return;
    setSending(true);
    setNote('');
    try {
      await p.onStopAndSend(words, pictures);
      if (box.current?.value === text) keep('');
      setPictures((current) => current.filter((path) => !pictures.includes(path)));
    } catch (err: any) {
      setNote(err?.message || '停止并发送失败');
    } finally {
      setSending(false);
    }
  };

  const hasDraft = Boolean(text.trim() || pictures.length > 0);
  const hintText = uploading
    ? '正在上传附件…'
    : p.stopping
      ? '已请求停止 · 现在发送将立即执行新指令'
      : p.queueMode
        ? 'Enter 加入待发（本轮结束后合并一次发出）'
        : p.busy
          ? '可以继续补充'
          : 'Enter 发送 · Shift+Enter 换行';

  return (
    <form
      class="composer"
      onSubmit={(e) => {
        e.preventDefault();
        send();
      }}
    >
      {(note || p.disabled) && <div class={`composer-note${note ? ' bad' : ''}`}>{note || p.disabled}</div>}
      {pictures.length > 0 && (
        <div class="composer-pics">
          {pictures.map((path) => (
            <span key={path} class="pic-chip">
              {window.RemoteAttachmentNames.label(path.split('/').pop() || '') || path.split('/').pop()}
              <button type="button" aria-label="移除" onClick={() => setPictures((prev) => prev.filter((x) => x !== path))}>
                ×
              </button>
            </span>
          ))}
        </div>
      )}
      <div class="composer-box">
        <textarea
          ref={box}
          rows={1}
          value={text}
          placeholder={p.placeholder}
          disabled={Boolean(p.disabled) && !text}
          onInput={(e) => keep((e.target as HTMLTextAreaElement).value)}
          onKeyDown={(e) => {
            // (Enter that ends an input method's composition is the method's, not a send)
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
              e.preventDefault();
              send();
            }
          }}
          onPaste={(e) => {
            const files = Array.from(e.clipboardData?.items || [])
              .filter((i) => i.type.startsWith('image/'))
              .map((i) => i.getAsFile())
              .filter(Boolean) as File[];
            if (files.length && p.attachmentDir) {
              e.preventDefault();
              addFiles(files);
            }
          }}
        />
        <div class="composer-row">
          <div class="composer-left">
            {p.attachmentDir && (
              <>
                <input
                  ref={picker}
                  type="file"
                  accept={p.allowFiles ? undefined : "image/png,image/jpeg,image/webp,image/gif"}
                  multiple
                  hidden
                  onChange={(e) => {
                    const input = e.target as HTMLInputElement;
                    if (input.files) addFiles(Array.from(input.files));
                    input.value = '';
                  }}
                />
                <button
                  type="button"
                  class="icon-btn"
                  title={p.allowFiles ? "添加文件" : "添加图片（也可以直接粘贴）"}
                  disabled={sending || uploading || pictures.length >= MAX_ATTACHMENTS}
                  onClick={() => picker.current?.click()}
                >
                  <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
                    {p.allowFiles ? (
                      <path d="m7 11 6-6a2 2 0 0 1 3 3l-7 7a3.5 3.5 0 0 1-5-5l7-7" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" fill="none" />
                    ) : (
                      <>
                        <rect x="3" y="4.5" width="14" height="11" rx="2" fill="none" stroke="currentColor" stroke-width="1.5" />
                        <circle cx="7.5" cy="8.5" r="1.3" fill="currentColor" />
                        <path d="m4 14 4-3.5 3 2.5 2.5-2 3 3" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round" />
                      </>
                    )}
                  </svg>
                </button>
              </>
            )}
            <span class="composer-hint">{hintText}</span>
          </div>
          <div class="composer-right">
            {p.tail}
            {p.busy && p.canStop && p.onStopAndSend && hasDraft && (
              <button
                type="button"
                class="btn stop-send"
                disabled={sending || uploading || Boolean(p.disabled)}
                title="先停止当前这一轮，然后立即发送并运行这条新指令"
                onClick={stopAndSend}
              >
                停止并发送
              </button>
            )}
            {p.busy && p.canStop && (
              <button type="button" class="round-btn stop" title="停止" disabled={sending} onClick={stop}>
                <span class="stop-mark" aria-hidden="true" />
                <span class="vh">停止</span>
              </button>
            )}
            {(!p.busy || !p.canStop || hasDraft) && (
              <button
                type="submit"
                class={`round-btn send${hasDraft ? ' ready' : ''}`}
                title={p.queueMode ? '加入待发' : '发送'}
                disabled={sending || uploading || Boolean(p.disabled) || !hasDraft}
              >
                <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
                  <path d="M12 19V5.5M6 11l6-6 6 6" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round" />
                </svg>
                <span class="vh">{sending ? '发送中' : p.queueMode ? '加入待发' : '发送'}</span>
              </button>
            )}
          </div>
        </div>
      </div>
    </form>
  );
}
