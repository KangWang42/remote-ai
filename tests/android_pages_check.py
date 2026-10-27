"""Check the APK's UI bundle and native URL resolver against public-relay and direct routes."""
import argparse
import subprocess
import tempfile
import zipfile
from pathlib import Path

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--jdk', required=True)
    parser.add_argument('--apk', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    out = Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.apk) as apk:
        manifest = apk.read('assets/web-assets.properties').decode('utf-8')
        entries = dict(line.split('=', 1) for line in manifest.splitlines() if line)
        for name in entries:
            assert apk.read('assets/web/' + name) == (root / 'web' / name).read_bytes(), name
        assert b'pond:' in apk.read('assets/web/terminal/skins.js')
        assert b'Copyright (c) 2026 stqfdyr' in apk.read('assets/web/themes/vendor/ascii-pond/LICENSE')
        for name in ('terminal/index.html', 'chat/index.html', 'files/index.html', 'computer/index.html', 'themes/pond.js', 'themes/pond.css'):
            assert name in entries, name
    manifest_path = out / 'web-assets.properties'
    manifest_path.write_text(manifest, encoding='utf-8')
    with tempfile.TemporaryDirectory(prefix='java-', dir=out) as classes:
        subprocess.run([str(Path(args.jdk) / 'bin/javac.exe'), '-J-Duser.language=en', '-Xlint:-options', '--release', '8', '-encoding', 'UTF-8', '-d', classes,
                        str(root / 'android/src/io/github/kangwang42/remotecli/PageAssets.java'), str(root / 'tests/PageAssetsCheck.java')], check=True)
        subprocess.run([str(Path(args.jdk) / 'bin/java.exe'), '-cp', classes, 'io.github.kangwang42.remotecli.PageAssetsCheck', str(manifest_path)], check=True)
    print('APK page sources match the workspace; theme modules, fonts, viewers and license are included.')

if __name__ == '__main__':
    main()
