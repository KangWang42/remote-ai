/* Exercise the upload client with real Blob slicing, byte boundaries and interrupted requests. */
const assert = require('node:assert/strict');
global.window = global;
require('../web/files/transfers.js');
async function main() {
  const bytes = Buffer.alloc(1600001);
  for (let i = 0; i < bytes.length; i++) bytes[i] = i % 251;
  const received = [], actions = [];
  let at = 0;
  const ask = async (action, path, offset, payload) => {
    actions.push(action);
    assert.equal(path, '子目录/手机文件.bin');
    if (action === 'file_upload_chunk') {
      assert.equal(offset, at);
      const part = Buffer.from(payload.data, 'base64');
      assert.ok(part.length <= 737280);
      received.push(part); at += part.length;
    }
    return { offset: at };
  };
  await new RemoteCliUpload(ask, () => 'a'.repeat(32), () => {}).send(new Blob([bytes]), '子目录/手机文件.bin');
  assert.deepEqual(Buffer.concat(received), bytes);
  assert.equal(actions.filter(x => x === 'file_upload_chunk').length, 3);
  assert.equal(actions.at(-1), 'file_upload_finish');
  const empty = [];
  await new RemoteCliUpload(async action => { empty.push(action); return { offset: 0 }; }, () => 'b'.repeat(32), () => {}).send(new Blob([]), 'empty');
  assert.deepEqual(empty, ['file_upload_start', 'file_upload_finish']);
  for (const mode of ['cancel', 'network', 'offset', 'start']) {
    const calls = [];
    const transfer = new RemoteCliUpload(async action => {
      calls.push(action);
      if (action === 'file_upload_start' && mode === 'start') throw Error('start lost');
      if (action === 'file_upload_chunk') {
        if (mode === 'cancel') transfer.cancel();
        if (mode === 'network') throw Error('offline');
        return { offset: mode === 'offset' ? 1 : 737280 };
      }
      return { offset: 0 };
    }, () => 'c'.repeat(32), () => {});
    await assert.rejects(transfer.send(new Blob([bytes]), 'x'));
    assert.equal(calls.at(-1), 'file_upload_cancel');
    assert.ok(!calls.includes('file_upload_finish'));
  }
  const large = new RemoteCliUpload(() => { throw Error('must not send'); }, () => '', () => {});
  await assert.rejects(large.send({ size: 300 * 1024 * 1024 + 1 }, 'x'), /300 MB/);
  console.log('Upload client: bytes, empty file, cancellation, failures and bounds passed');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
