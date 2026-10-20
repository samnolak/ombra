#!/usr/bin/env python3
"""Run pinned pristine upstream tests against owned source, with explicit divergences."""
import argparse
import json
import os
from pathlib import Path
import re
import runpy
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
ENGINE = Path(__file__).resolve().parent

def check_report(report_path):
    report = json.loads(report_path.read_text())
    expected = json.loads((ENGINE / 'upstream-expected-failures.json').read_text())
    version = json.loads((ENGINE / 'upstream.json').read_text())['version']
    if expected['version'] != version:
        raise SystemExit('Expected failures must be reviewed for this upstream version')
    allowed = {test: group['reason'] for group in expected['groups'] for test in group['tests']}
    unexpected = []
    observed = set()
    for failure in report['failures']:
        output = re.sub(r'\x1b\[[0-9;]*m', '', failure['output'])
        names = set(re.findall(r'^\(fail\)\s+(.+?)(?:\s+\[[\d.]+\s*m?s\])?$', output, re.M))
        package = failure['label'].split(' (', 1)[0]
        keys = {package + ' > ' + name for name in names}
        observed.update(keys)
        # Never forgive crashes, watchdog kills, import errors, or a chunk with
        # unaccounted failures merely because one expected test also failed.
        counts = re.findall(r'^\s*(\d+) fail\s*$', output, re.M)
        errors = re.search(r'^\s*[1-9]\d* errors?\s*$', output, re.M)
        if failure['exitCode'] != 1 or not counts or int(counts[-1]) != len(names) or errors:
            unexpected.append(failure['label'] + ': unclassified process/import failure')
        unexpected.extend(sorted(keys - allowed.keys()))
    result = {'totalChunks': report['total'], 'failedChunks': len(report['failures']),
              'expectedTests': sorted(observed & allowed.keys()), 'unexpected': unexpected,
              'unusedExpectations': sorted(allowed.keys() - observed)}
    (report_path.parent / 'verdict.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))
    return bool(unexpected)



def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bun', required=True, type=Path)
    parser.add_argument('--work', type=Path, default=ROOT / '.temp/upstream-gate')
    parser.add_argument('--python', type=Path, default=Path(sys.executable))
    args = parser.parse_args()
    bun = args.bun.resolve()
    work = args.work.resolve()
    work.mkdir(parents=True, exist_ok=True)
    patcher = runpy.run_path(str(ENGINE / 'patcher.py'))
    manifest = patcher['manifest']()
    workspace = work / 'upstream'
    if workspace.exists():
        raise SystemExit(f'Refusing to reuse workspace: {workspace}')
    subprocess.run(['git', 'clone', '--depth', '1', '--branch', 'v' + manifest['version'],
                    'https://github.com/can1357/oh-my-pi.git', str(workspace)], check=True)
    revision = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=workspace, text=True).strip()
    if manifest['version'] == '18.12.2' and revision != '1d5aff90b057ce7dbfe32ea557a527c1b3144b72':
        raise SystemExit('Upstream tag differs from the npm provenance revision')
    # A short HOME/TMPDIR also avoids macOS Unix-domain socket path limits.
    with tempfile.TemporaryDirectory(prefix='omp-up-', dir='/tmp') as home:
        env = {'HOME': home, 'TMPDIR': '/tmp', 'TERM': 'dumb',
               'PATH': str(bun.parent) + ':' + str(args.python.resolve().parent) + ':/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin',
               'PYTHON': str(args.python.resolve()), 'OMP_TEST_CONCURRENCY': '2'}
        install_env = dict(env)
        for key in ('NODE_EXTRA_CA_CERTS', 'SSL_CERT_FILE', 'SSL_CERT_DIR'):
            if os.environ.get(key):
                install_env[key] = os.environ[key]
        subprocess.run([str(bun), 'install', '--frozen-lockfile', '--ignore-scripts'],
                       cwd=workspace, env=install_env, check=True)
        source = workspace / 'packages/coding-agent/src'
        shutil.rmtree(source)
        shutil.copytree(ENGINE / 'coding-agent/src', source)
        import platform
        system = {'Darwin': 'darwin', 'Linux': 'linux'}[platform.system()]
        arch = {'arm64': 'arm64', 'aarch64': 'arm64', 'x86_64': 'x64'}[platform.machine()]
        package = '@oh-my-pi/pi-natives-' + system + '-' + arch
        if system == 'linux':
            package += '-gnu'
        archive = patcher['tarball'](manifest['packages'][package])
        for name, member, tar in patcher['archive_files'](archive):
            if name.endswith('.node'):
                (workspace / 'packages/natives/native' / Path(name).name).write_bytes(tar.extractfile(member).read())
        subprocess.run(['git', 'apply', str(ENGINE / 'upstream-fixtures.patch')],
                       cwd=workspace, check=True)
        # SDKs are not vendored. Regenerate only those fixtures from our wire schema;
        # keep owned schema/TypeScript untouched so their consistency is still tested.
        subprocess.run([str(bun), '-e',
            'import {generateRpcArtifacts} from "./packages/coding-agent/scripts/gen-rpc"; '
            'for (const [file, text] of generateRpcArtifacts()) '
            'if (file.includes("/sdk/")) await Bun.write(file, text);'], cwd=workspace, env=env, check=True)
        # Serialize files for stable failure attribution: Bun's parallel worker
        # output can interleave file headers and attribute a failure twice.
        runner = workspace / 'scripts/ci-test-ts.ts'
        runner_text = runner.read_text()
        anchor = 'const testCommands = applyChunkBudget(requestedCommands, poolWidth);'
        if runner_text.count(anchor) != 1:
            raise SystemExit('Upstream runner changed; review compatibility harness')
        runner_text = runner_text.replace(anchor,
            'const testCommands = applyChunkBudget(requestedCommands.map(c => ({...c, parallel: 1})), poolWidth);')
        anchor = '\tif (failures.length > 0) {\n\t\tprocess.exitCode = 1;'
        if runner_text.count(anchor) != 1:
            raise SystemExit('Upstream outcome reporting changed')
        runner_text = runner_text.replace(anchor,
            '\tawait Bun.write(\"../outcomes.json\", JSON.stringify({total: commands.length, failures}));\n' + anchor)
        runner.write_text(runner_text)
        result = subprocess.run([str(bun), 'scripts/ci-test-ts.ts', 'all'], cwd=workspace,
                                env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        (work / 'full.log').write_text(result.stdout)
        print(result.stdout)
        if result.returncode not in (0, 1):
            raise SystemExit(f'Upstream runner crashed: exit {result.returncode}')
        report_path = work / 'outcomes.json'
        if not report_path.is_file():
            raise SystemExit('Upstream runner did not produce a complete outcome report')
        raise SystemExit(check_report(report_path))


if __name__ == '__main__':
    main()
