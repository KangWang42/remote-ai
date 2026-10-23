/* One bounded upload at a time. Paths and transfer IDs stay fixed while the user browses other folders. */
(() => {
  'use strict';
  const LIMIT = 300 * 1024 * 1024, PIECE = 737280;
  class Upload {
    constructor(ask, id, progress) { this.ask = ask; this.id = id; this.progress = progress; this.cancelled = false; }
    cancel() { this.cancelled = true; }
    check() { if (this.cancelled) throw new Error('已取消上传'); }
    async send(file, path) {
      this.check();
      if (file.size > LIMIT) throw new Error('单个文件上传上限为 300 MB');
      const upload = this.id();
      let finished = false;
      try {
        this.progress(0, file.size);
        await this.ask('file_upload_start', path, 0, { upload, size: file.size });
        this.check();
        for (let at = 0; at < file.size;) {
          const bytes = new Uint8Array(await file.slice(at, at + PIECE).arrayBuffer());
          this.check();
          if (!bytes.length) throw new Error('无法读取手机文件，请重新选择');
          let raw = '';
          for (let i = 0; i < bytes.length; i += 8192) raw += String.fromCharCode(...bytes.subarray(i, i + 8192));
          const answer = await this.ask('file_upload_chunk', path, at, { upload, data: btoa(raw) });
          if (answer.offset !== at + bytes.length) throw new Error('上传进度不一致，请重新上传');
          at = answer.offset;
          this.progress(at, file.size);
          this.check();
        }
        const answer = await this.ask('file_upload_finish', path, 0, { upload });
        if (answer.offset !== file.size) throw new Error('电脑没有确认完整文件，请刷新文件夹检查');
        finished = true;
      } finally {
        if (!finished) {
          try { await this.ask('file_upload_cancel', path, 0, { upload }); }
          catch (_) { /* Abandoned transfers expire on the computer. */ }
        }
      }
    }
  }
  window.RemoteCliUpload = Upload;
})();
