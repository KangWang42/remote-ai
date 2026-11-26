import { useEffect, useRef, useState } from 'preact/hooks';
import { uploadImageAttachment } from '../runtime/api';

interface Props {
  /** Drafts are kept per conversation under this key. */
  draftKey: string;
  placeholder: string;
  /** Where pictures are uploaded to (a project); none: no pictures. */
  pictureDir: string;
  busy: boolean;
  canStop: boolean;
  disabled?: string;
  /** Text to put into the box, e.g. a cited file (changes when `nonce` does). */
  insert?: { nonce: number; text: string } | null;
  onSend: (text: string, pictures: string[]) => Promise<void>;
  onStop: () => Promise<void>;
}

const MAX_PICTURES = 4;

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

  const addPictures = async (files: File[]) => {
    if (!p.pictureDir) return;
    const chosen = files.filter((f) => f.type.startsWith('image/')).slice(0, MAX_PICTURES - pictures.length);
    if (chosen.length === 0) return;
    setUploading(true);
    setNote('');
    try {
      const added: string[] = [];
      for (const f of chosen) added.push(await uploadImageAttachment(p.pictureDir, f));
      setPictures((prev) => [...prev, ...added].slice(0, MAX_PICTURES));
    } catch (err: any) {
      setNote(err?.message || '图片上传失败');
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
              {path.split('/').pop()}
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
            if (files.length && p.pictureDir) {
              e.preventDefault();
              addPictures(files);
            }
          }}
        />
        <div class="composer-row">
          <div class="composer-left">
            {p.pictureDir && (
              <>
                <input
                  ref={picker}
                  type="file"
                  accept="image/png,image/jpeg,image/webp,image/gif"
                  multiple
                  hidden
                  onChange={(e) => {
                    const input = e.target as HTMLInputElement;
                    if (input.files) addPictures(Array.from(input.files));
                    input.value = '';
                  }}
                />
                <button
                  type="button"
                  class="icon-btn"
                  title="添加图片（也可以直接粘贴）"
                  disabled={uploading || pictures.length >= MAX_PICTURES}
                  onClick={() => picker.current?.click()}
                >
                  <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
                    <rect x="3" y="4.5" width="14" height="11" rx="2" fill="none" stroke="currentColor" stroke-width="1.5" />
                    <circle cx="7.5" cy="8.5" r="1.3" fill="currentColor" />
                    <path d="m4 14 4-3.5 3 2.5 2.5-2 3 3" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round" />
                  </svg>
                </button>
              </>
            )}
            <span class="composer-hint">{uploading ? '正在上传图片…' : p.busy ? '可以继续补充' : 'Enter 发送 · Shift+Enter 换行'}</span>
          </div>
          <div class="composer-right">
            {p.busy && p.canStop && (
              <button type="button" class="btn quiet" onClick={stop}>
                停止
              </button>
            )}
            <button
              type="submit"
              class="btn primary"
              disabled={sending || uploading || Boolean(p.disabled) || (!text.trim() && pictures.length === 0)}
            >
              {sending ? '发送中' : '发送'}
            </button>
          </div>
        </div>
      </div>
    </form>
  );
}
