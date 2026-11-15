"""Trusted build facts: the BASE branch's own extractor and checker, taken from git, read the PR's binary logs.

  python -I <base copy of trusted_facts.py> produce --base-sha SHA --repo . --work DIR --solution Boo.sln \
      --publish-dir artifacts/ci [--replay-under src/] build=artifacts/binlog/build.binlog publish=artifacts/binlog/publish.binlog
        windows-ci on a pull request, before a base-branch judge (arch_refs.py, later banned_apis.py) reads facts
  python -I scripts/quality/trusted_facts.py --self-test     export, isolation and provenance on throwaway git repos

Why: a base-branch judge (a PR cannot loosen its script) is only as good as the facts it reads. The normal facts
(windows-ci "Build facts") come from the PR's own scripts/quality/buildfacts extractor and build_facts.py, so a PR
could change the extractor to leave a new reference out of the facts, consistently, and the base judge would pass it.
Here neither of the PR's copies runs:

  1. Every file under scripts/quality/buildfacts/ and scripts/quality/build_facts.py is read from git at --base-sha
     (blob by blob; a symlink or anything that is not a plain file fails) into --work, which must be OUTSIDE the
     repository, so the PR's Directory.Build.*, NuGet.config and global.json do not touch the build.
  2. That extractor is restored in locked mode, built, self-tested and run on the PR build's binary logs; that
     build_facts.py checks the facts it wrote (schema, machine paths, unknown tasks/switches, publish folder traced).
  3. provenance.json beside build-facts.json records the base commit, the git tree of the extractor folder, the blob of
     build_facts.py, and the sha256 of every binary log and of the facts. A judge given --trusted-base SHA re-reads
     git and the files and refuses facts whose provenance is missing or does not match (fail closed).

Exit 1 when the facts could not be produced or checked (the job fails), 2 on a usage error. Never reads secrets.
Known gap (as for every gate here): a PR can edit the workflow file itself; review and required checks cover that.
"""
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

SCHEMA = 'boo-trusted-facts/1'
EXTRACTOR_DIR = 'scripts/quality/buildfacts'
CHECKER = 'scripts/quality/build_facts.py'
SHA = re.compile(r'^[0-9a-f]{40}$')


class Failed(Exception):
    """The trusted facts could not be produced (the caller fails closed)."""


def git(repo, *args):
    out = subprocess.run(['git', '-c', 'core.quotePath=false'] + list(args), cwd=repo, capture_output=True)
    if out.returncode != 0:
        raise Failed(f'git {args[0]} failed: {out.stderr.decode("utf-8", "replace").strip()[:200]}')
    return out.stdout


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def object_id(repo, base, path):
    """The git object id of base:path (a tree for the extractor folder, a blob for the checker)."""
    return git(repo, 'rev-parse', '--verify', f'{base}:{path}').decode().strip()


def export(repo, base, dest):
    """RED-FIRST STUB (today's behaviour): copy the extractor and checker from the PR's working tree."""
    for path in (EXTRACTOR_DIR, CHECKER):
        source = pathlib.Path(repo).joinpath(*path.split('/'))
        target = dest.joinpath(*path.split('/'))
        target.parent.mkdir(parents=True, exist_ok=True)
        if source.is_dir():
            shutil.copytree(source, target)
        else:
            shutil.copyfile(source, target)


def outside(path, repo):
    path, repo = pathlib.Path(path).resolve(), pathlib.Path(repo).resolve()
    return path != repo and repo not in path.parents


def produce(base, repo, work, binlogs, solution, publish_dir, replay_under=None, run=None):
    """Build the base extractor, extract and check the facts, write provenance. Returns the facts path."""
    run = run or (lambda cmd, cwd: subprocess.run(cmd, cwd=cwd).returncode)
    repo, work = pathlib.Path(repo).resolve(), pathlib.Path(work).resolve()
    if not SHA.match(base or ''):
        raise Failed(f'--base-sha must be a full 40-character commit, not {base!r}')
    if False:
        raise Failed(f'--work {work} is inside the repository: the PR\'s Directory.Build.*, NuGet.config and '
                     'global.json would reach the trusted build')
    git(repo, 'cat-file', '-e', f'{base}^{{commit}}')
    if not binlogs or any(not pathlib.Path(p).is_file() for p in binlogs.values()):
        raise Failed('every name=binlog must name an existing binary log')
    if work.exists():
        shutil.rmtree(work)
    src, tool = work / 'src', work / 'tool'
    export(repo, base, src)
    project = src / EXTRACTOR_DIR / 'BuildFacts.csproj'
    if not project.is_file():
        raise Failed(f'the base commit has no {EXTRACTOR_DIR}/BuildFacts.csproj')
    facts = work / 'build-facts.json'
    steps = [
        (['dotnet', 'restore', str(project), '--locked-mode'], work),
        (['dotnet', 'build', str(project), '-c', 'Release', '--no-restore', '-o', str(tool)], work),
        (['dotnet', str(tool / 'BuildFacts.dll'), '--self-test'], work),
        (['dotnet', str(tool / 'BuildFacts.dll'), '--repo', str(repo), '--out', str(facts)]
         + (['--replay-under', replay_under] if replay_under else [])
         + [f'{name}={pathlib.Path(p).resolve()}' for name, p in binlogs.items()], repo),
        ([sys.executable, '-I', str(src / CHECKER), 'check', str(facts), '--repo', str(repo),
          '--solution', str(pathlib.Path(repo, solution)), '--publish-dir', str(pathlib.Path(repo, publish_dir))],
         repo),
    ]
    for cmd, cwd in steps:
        code = run(cmd, cwd)
        if code != 0:
            raise Failed(f'the base branch\'s tooling failed (exit {code}): {" ".join(cmd[:4])} ...')
    if not facts.is_file():
        raise Failed('the base extractor wrote no facts')
    provenance = {
        'schema': SCHEMA,
        'base_sha': base,
        'extractor_tree': object_id(repo, base, EXTRACTOR_DIR),
        'checker_blob': object_id(repo, base, CHECKER),
        'binlogs': {name: {'path': str(pathlib.Path(p).resolve()), 'sha256': sha256(p)} for name, p in binlogs.items()},
        'facts_sha256': sha256(facts),
    }
    (work / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n', encoding='utf-8')
    return facts


def verify(facts_path, base, repo):
    """RED-FIRST STUB (today's behaviour): any facts are accepted."""
    return []


# ---------------------------------------------------------------- self-test (throwaway git repos, no .NET)

def self_test():
    results = []
    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        repo = tmp / 'repo'
        repo.mkdir()
        env = dict(os.environ, GIT_AUTHOR_NAME='t', GIT_AUTHOR_EMAIL='t@example.invalid', GIT_COMMITTER_NAME='t',
                   GIT_COMMITTER_EMAIL='t@example.invalid', GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)

        def sh(*args):
            subprocess.run(['git'] + list(args), cwd=repo, env=env, check=True, capture_output=True)

        def write(path, text):
            target = repo.joinpath(*path.split('/'))
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text, encoding='utf-8')

        sh('init', '-q', '-b', 'main')
        write(f'{EXTRACTOR_DIR}/BuildFacts.csproj', '<Project />\n')
        write(f'{EXTRACTOR_DIR}/Program.cs', '// honest extractor\n')
        write(CHECKER, '# honest checker\n')
        sh('add', '-A')
        sh('commit', '-q', '-m', 'base')
        base = git(repo, 'rev-parse', 'HEAD').decode().strip()
        # The PR head changes the extractor (a forger) in the working tree and in a commit.
        write(f'{EXTRACTOR_DIR}/Program.cs', '// FORGED extractor: leaves Boo.Providers out of the facts\n')
        sh('commit', '-q', '-am', 'head')
        head = git(repo, 'rev-parse', 'HEAD').decode().strip()
        binlog = tmp / 'build.binlog'
        binlog.write_bytes(b'binlog')
        seen = []

        def fake_run(cmd, cwd):
            seen.append((cmd, pathlib.Path(cwd)))
            if '--out' in cmd:
                pathlib.Path(cmd[cmd.index('--out') + 1]).write_text('{"schema": "boo-build-facts/1"}\n')
            return 0

        work = tmp / 'work'
        facts = produce(base, repo, work, {'build': binlog}, 'Boo.sln', 'artifacts/ci', run=fake_run)
        exported = (work / 'src' / EXTRACTOR_DIR / 'Program.cs').read_text(encoding='utf-8')
        results.append(('the extractor is taken from the BASE commit, not the PR head or working tree',
                        exported == '// honest extractor\n'))
        repo_paths = [c for c, _ in seen if any(str(repo / EXTRACTOR_DIR) in a or str(repo / CHECKER) == a
                                               for a in c)]
        results.append(('none of the PR\'s copies of the extractor or checker is ever run', repo_paths == []))
        results.append(('the trusted extractor is restored and built outside the repository',
                        all(cwd == work for c, cwd in seen[:3])))
        results.append(('good provenance verifies', verify(facts, base, repo) == []))

        def fails_with(problems, words):
            return any(words in p for p in problems)

        results.append(('a base sha that is not the one produced from fails',
                        fails_with(verify(facts, head, repo), 'not the base')))
        original = facts.read_text()
        facts.write_text(original.replace('1', '2'))
        results.append(('forged facts (edited after production) fail', fails_with(verify(facts, base, repo),
                                                                                    'changed after')))
        facts.write_text(original)
        binlog.write_bytes(b'other binlog')
        results.append(('a binary log swapped after production fails', fails_with(verify(facts, base, repo),
                                                                                  'binary log build changed')))
        binlog.write_bytes(b'binlog')
        prov_path = work / 'provenance.json'
        prov = json.loads(prov_path.read_text())
        prov['extractor_tree'] = object_id(repo, head, EXTRACTOR_DIR)
        prov_path.write_text(json.dumps(prov))
        results.append(('facts from a head-modified extractor (its tree recorded) fail',
                        fails_with(verify(facts, base, repo), f'{EXTRACTOR_DIR} that produced')))
        prov_path.unlink()
        results.append(('facts with no provenance (the normal head-produced facts) fail',
                        fails_with(verify(facts, base, repo), 'no readable provenance')))
        prov_path.write_text('{"schema": "something-else"}')
        results.append(('provenance of another schema fails', fails_with(verify(facts, base, repo), 'is not')))

        def refuses(fn, words):
            try:
                fn()
            except Failed as error:
                return words in str(error)
            return False

        results.append(('a work folder inside the repository is refused',
                        refuses(lambda: produce(base, repo, repo / 'w', {'build': binlog}, 'Boo.sln', 'a',
                                                run=fake_run), 'inside the repository')))
        results.append(('a short or symbolic base is refused',
                        refuses(lambda: produce('main', repo, work, {'build': binlog}, 'Boo.sln', 'a', run=fake_run),
                                'full 40-character')))
        results.append(('a failing base tool fails closed',
                        refuses(lambda: produce(base, repo, work, {'build': binlog}, 'Boo.sln', 'a',
                                                run=lambda c, w: 1), 'failed (exit 1)')))
        results.append(('a missing binary log is refused',
                        refuses(lambda: produce(base, repo, work, {'build': tmp / 'nope'}, 'Boo.sln', 'a',
                                                run=fake_run), 'existing binary log')))
        if os.name != 'nt':
            os.symlink('Program.cs', repo / EXTRACTOR_DIR / 'Link.cs')
            sh('add', '-A')
            sh('commit', '-q', '-m', 'link')
            linked = git(repo, 'rev-parse', 'HEAD').decode().strip()
            results.append(('a symlink in the base extractor folder is refused',
                            refuses(lambda: produce(linked, repo, work, {'build': binlog}, 'Boo.sln', 'a',
                                                    run=fake_run), 'not a plain file')))
    failed = [n for n, ok in results if not ok]
    for n in failed:
        print(f'  FAILED: {n}')
    print(f'TRUSTED_FACTS_SELF_TEST_{"FAILED" if failed else "PASSED"} {len(results) - len(failed)} of {len(results)}')
    return 1 if failed else 0


def main(argv):
    if argv == ['--self-test']:
        return self_test()
    if argv[:1] != ['produce']:
        print(__doc__, file=sys.stderr)
        return 2
    opts, binlogs, i = {}, {}, 1
    while i < len(argv):
        arg = argv[i]
        if arg in ('--base-sha', '--repo', '--work', '--solution', '--publish-dir', '--replay-under'):
            if i + 1 >= len(argv):
                print(f'{arg} needs a value', file=sys.stderr)
                return 2
            opts[arg], i = argv[i + 1], i + 2
        elif '=' in arg and not arg.startswith('--'):
            name, path = arg.split('=', 1)
            binlogs[name], i = path, i + 1
        else:
            print(f'unknown argument {arg}', file=sys.stderr)
            return 2
    missing = [o for o in ('--base-sha', '--work', '--solution', '--publish-dir') if o not in opts]
    if missing or not binlogs:
        print(f'produce needs {", ".join(missing) or "name=binlog"}', file=sys.stderr)
        return 2
    try:
        facts = produce(opts['--base-sha'], opts.get('--repo', '.'), opts['--work'], binlogs, opts['--solution'],
                        opts['--publish-dir'], opts.get('--replay-under'))
    except Failed as error:
        print(f'::error::TRUSTED FACTS FAILED: {error}')
        return 1
    print(f'TRUSTED FACTS OK: {facts} from the base commit {opts["--base-sha"][:12]}\'s own extractor and checker '
          f'(provenance.json beside it)')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
