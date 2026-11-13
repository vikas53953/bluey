"""BOO-CI-001: preserve declared workflow gates. Requires the pinned CI PyYAML dependency."""
import argparse
import copy
import json
import os
import pathlib
import subprocess
import sys
import tempfile

import yaml


class UniqueLoader(yaml.BaseLoader):
    pass


def mapping(loader, node):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node)
        if not isinstance(key, str) or key in result:
            raise ValueError('duplicate or non-scalar YAML key')
        result[key] = loader.construct_object(value_node)
    return result


UniqueLoader.add_constructor('tag:yaml.org,2002:map', mapping)


def inspect(root, manifest):
    if set(manifest) != {'schema', 'jobs'} or manifest['schema'] != 1 or not manifest['jobs']:
        raise ValueError('invalid manifest schema')
    errors = []
    for contract in manifest['jobs']:
        if set(contract) != {'workflow', 'job', 'steps'} or not contract['steps']:
            raise ValueError('invalid job contract')
        path = pathlib.PurePosixPath(contract['workflow'])
        if path.is_absolute() or '..' in path.parts or path.parts[:2] != ('.github', 'workflows'):
            raise ValueError('manifest workflow must be under .github/workflows')
        workflow = yaml.load((root / path).read_text(encoding='utf-8-sig'), Loader=UniqueLoader)
        job = workflow['jobs'].get(contract['job'])
        where = f"{path}:{contract['job']}"
        if not isinstance(job, dict):
            errors.append(f'{where}: required job missing')
            continue
        for container in (workflow, job):
            if 'defaults' in container:
                errors.append(f'{where}: defaults may redirect a declared gate')
        if any(key in job for key in ('if', 'continue-on-error', 'uses', 'strategy', 'container')):
            errors.append(f'{where}: declared job must execute once and fail normally')
        if job.get('runs-on') != 'windows-latest':
            errors.append(f'{where}: declared job requires windows-latest')
        steps = job['steps']
        for expected in contract['steps']:
            if not isinstance(expected, dict) or not isinstance(expected.get('name'), str) or 'run' not in expected:
                raise ValueError('invalid step contract')
            matches = [step for step in steps if step.get('name') == expected['name']]
            if len(matches) != 1 or matches[0] != expected:
                errors.append(f"{where}: restore exactly one unchanged '{expected['name']}' step")
    return errors


def check(root, manifest_path):
    try:
        manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
        errors = inspect(root, manifest)
    except (OSError, ValueError, KeyError, TypeError, yaml.YAMLError, AttributeError, RecursionError) as error:
        errors = [f'unreadable workflow/manifest ({type(error).__name__}); failing closed']
    for error in errors:
        print(f'BOO-CI-001 {error}. Fix: restore the declared gate from the trusted base; '
              'intentional policy changes need a separate owner-reviewed policy update.')
    print('GATE_MANIFEST_FAILED' if errors else 'GATE_MANIFEST_OK')
    return int(bool(errors))


def self_test():
    """Exercise the command-line consumer with synthetic workflow contracts, never the product."""
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        workflows = root / '.github/workflows'
        workflows.mkdir(parents=True)
        expected = {'name': 'Oracle', 'run': 'python -I scripts/test_oracle.py'}
        guard = {'name': 'Guard', 'run': 'python -I scripts/quality/gate_manifest.py'}
        manifest = {'schema': 1, 'jobs': [
            {'workflow': '.github/workflows/test.yml', 'job': 'checks', 'steps': [expected]},
            {'workflow': '.github/workflows/test.yml', 'job': 'guard', 'steps': [guard]}]}
        manifest_path = root / 'manifest.json'
        manifest_path.write_text(json.dumps(manifest), encoding='utf-8')
        valid = {'jobs': {'checks': {'runs-on': 'windows-latest', 'steps': [expected]},
                          'guard': {'runs-on': 'windows-latest', 'steps': [guard]}}}
        cases = [('valid', valid, 0)]
        for name, mutate in [
            ('missing gate', lambda w: w['jobs']['checks']['steps'].clear()),
            ('duplicate gate', lambda w: w['jobs']['checks']['steps'].append(expected)),
            ('changed command', lambda w: w['jobs']['checks']['steps'][0].update(run='echo ok')),
            ('conditional step', lambda w: w['jobs']['checks']['steps'][0].update({'if': 'false'})),
            ('soft step', lambda w: w['jobs']['checks']['steps'][0].update({'continue-on-error': 'true'})),
            ('conditional job', lambda w: w['jobs']['checks'].update({'if': 'false'})),
            ('soft job', lambda w: w['jobs']['checks'].update({'continue-on-error': 'true'})),
            ('wrong runner', lambda w: w['jobs']['checks'].update({'runs-on': 'ubuntu-latest'})),
            ('job defaults', lambda w: w['jobs']['checks'].update(defaults={'run': {'working-directory': 'elsewhere'}})),
            ('workflow defaults', lambda w: w.update(defaults={'run': {'shell': 'bash'}})),
            ('removed checker job', lambda w: w['jobs'].pop('guard')),
            ('disabled checker job', lambda w: w['jobs']['guard'].update({'if': 'false'})),
            ('rewritten checker command', lambda w: w['jobs']['guard']['steps'][0].update(run='echo ok')),
        ]:
            changed = copy.deepcopy(valid)
            mutate(changed)
            cases.append((name, changed, 1))
        cases.extend([('duplicate YAML key', 'jobs: {}\njobs: {}\n', 1),
                      ('malformed YAML', 'jobs: [', 1), ('missing jobs', {}, 1)])
        for name, workflow, expected_code in cases:
            text = workflow if isinstance(workflow, str) else yaml.safe_dump(workflow)
            (workflows / 'test.yml').write_text(text, encoding='utf-8')
            result = subprocess.run([sys.executable, '-I', str(pathlib.Path(__file__).resolve()),
                                     '--root', str(root), '--manifest', str(manifest_path)],
                                    capture_output=True, text=True)
            marker = 'GATE_MANIFEST_OK' if expected_code == 0 else 'BOO-CI-001'
            if result.returncode != expected_code or marker not in result.stdout:
                print(f'SELF_TEST_FAILED {name}: {result.stdout} {result.stderr}')
                return 1
        # The workflow block is a machine-consumed interface: execute it with real git base states.
        source = pathlib.Path(__file__).resolve()
        workflow = yaml.load((source.parents[2] / '.github/workflows/gate-manifest.yml').read_text(), Loader=UniqueLoader)
        bootstrap = next(step['run'] for step in workflow['jobs']['gate-manifest']['steps']
                         if step.get('name') == 'Trusted base gate manifest')
        for index, (has_checker, has_manifest, missing_commit) in enumerate(
                [(True, True, False), (False, False, False), (True, False, False),
                 (False, True, False), (True, True, True)]):
            repo = root / f'base-{index}'
            repo.mkdir()
            def git(*arguments):
                return subprocess.run(['git', '-C', str(repo), *arguments], check=True,
                                      capture_output=True, text=True).stdout.strip()
            git('init')
            (repo / '.github/workflows').mkdir(parents=True)
            (repo / '.github/workflows/test.yml').write_text(yaml.safe_dump(valid), encoding='utf-8')
            if has_checker:
                (repo / 'scripts/quality').mkdir(parents=True)
                (repo / 'scripts/quality/gate_manifest.py').write_text(source.read_text(), encoding='utf-8')
            if has_manifest:
                (repo / 'docs/quality').mkdir(parents=True)
                (repo / 'docs/quality/gate-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            git('add', '.')
            git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                '-c', 'commit.gpgsign=false', 'commit', '-m', 'base fixture')
            environment = dict(os.environ, BASE_SHA='0' * 40 if missing_commit else git('rev-parse', 'HEAD'),
                               RUNNER_TEMP=str(root))
            result = subprocess.run(['pwsh', '-NoProfile', '-Command', bootstrap], cwd=repo, env=environment,
                                    capture_output=True, text=True)
            should_pass = has_checker == has_manifest and not missing_commit
            marker = ('GATE_MANIFEST_OK' if has_checker else 'Base has no manifest') if should_pass else 'BOO-CI-001'
            if (result.returncode == 0) != should_pass or marker not in result.stdout + result.stderr:
                print(f'SELF_TEST_FAILED base-{index}: {result.stdout} {result.stderr}')
                return 1
        print(f'GATE_MANIFEST_SELF_TEST_OK {len(cases)} CLI cases + 5 real-git bootstrap cases')
        return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=pathlib.Path, default=pathlib.Path.cwd())
    parser.add_argument('--manifest', type=pathlib.Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    return check(args.root, args.manifest or args.root / 'docs/quality/gate-manifest.json')


if __name__ == '__main__':
    sys.exit(main())
