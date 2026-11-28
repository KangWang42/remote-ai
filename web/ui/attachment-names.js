/* Shared by the mobile picker and the desktop composer. The dot after the ID identifies
   a native image. A dash introduces the display name of a file handed to the AI by path. */
(() => {
  const prefix = '[0-9]{8}-[0-9]{6}-[0-9a-f]{6}';
  const image = /\.(png|jpe?g|webp|gif)$/i;
  const unsafe = /[<>:"/\\|?*\x00-\x1f\x7f-\x9f\u2028\u2029]/g;
  const name = new RegExp('^' + prefix + '(?:\\.(?:png|jpe?g|webp|gif)|-[^<>:"/\\\\|?*\\x00-\\x1f\\x7f-\\x9f\\u2028\\u2029]{1,60})$');
  const mime = { 'image/png': 'png', 'image/jpeg': 'jpg', 'image/webp': 'webp', 'image/gif': 'gif' };
  const label = stored => stored.charAt(22) === '-' ? stored.slice(23) : '';

  function storedName(file, allowFiles) {
    const ext = mime[file.type] || (image.exec(file.name) || [])[1]?.toLowerCase();
    let suffix;
    if (ext) {
      suffix = '.' + ext;
    } else {
      if (!allowFiles) return '';
      const clean = file.name.replace(unsafe, '_').replace(/[. ]+$/, '') || '文件';
      const extension = (/\.[a-zA-Z0-9]{1,12}$/.exec(clean) || [''])[0];
      const stem = extension ? clean.slice(0, -extension.length) : clean;
      suffix = '-' + stem.slice(0, 60 - extension.length).replace(/[\uD800-\uDBFF]$/, '').replace(/[. ]+$/, '') + extension;
    }
    const now = new Date(), two = n => String(n).padStart(2, '0');
    const stamp = now.getFullYear() + two(now.getMonth() + 1) + two(now.getDate()) + '-' + two(now.getHours()) + two(now.getMinutes()) + two(now.getSeconds());
    const hex = Array.from(crypto.getRandomValues(new Uint8Array(3)), b => b.toString(16).padStart(2, '0')).join('');
    return stamp + '-' + hex + suffix;
  }
  window.RemoteAttachmentNames = { name, storedName, label };
})();
