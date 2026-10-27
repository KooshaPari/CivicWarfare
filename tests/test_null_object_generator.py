import json
from pathlib import Path
import re
import shutil
import subprocess

import pytest


ROOT = Path(__file__).parents[1]


def test_null_generator_compiles_and_executes_real_emitted_source(tmp_path):
    result = run_generator_fixture(tmp_path, 'NullObjectGenerator', 'null_object_generator_driver')
    assert 'null generator emitted-source behavior passed' in result


def run_generator_fixture(tmp_path, generator, driver):
    dotnet = shutil.which('dotnet')
    if not dotnet:
        pytest.skip('Installed .NET 8 SDK is required')
    sdks = subprocess.check_output([dotnet, '--list-sdks'], text=True)
    matches = re.findall(r'^(8\.\S+) \[(.+)\]$', sdks, re.M)
    if not matches:
        pytest.skip('Installed .NET 8 SDK is required')
    version, location = matches[-1]
    sdk = Path(location) / version
    compiler = sdk / 'Roslyn/bincore/csc.dll'
    packs = sorted((sdk.parents[1] / 'packs/Microsoft.NETCore.App.Ref').glob('8.*/ref/net8.0'))
    assert packs, 'Installed .NET 8 reference pack is required'
    executable = tmp_path / 'NullObjectGeneratorDriver.dll'
    references = [f'-reference:{path}' for path in packs[-1].glob('*.dll')]
    for name in ('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll'):
        references.append(f'-reference:{compiler.parent / name}')
        shutil.copyfile(compiler.parent / name, tmp_path / name)
    build = subprocess.run([
        dotnet, str(compiler), '-nologo', '-target:exe', '-nostdlib+',
        '-langversion:latest', '-nullable:enable', f'-out:{executable}',
        *references, str(ROOT / f'CivicSurvival.Analyzers/Generators/{generator}.cs'),
        str(ROOT / f'tests/fixtures/{driver}.cs'),
    ], capture_output=True, text=True, timeout=45, check=False)
    assert build.returncode == 0, build.stdout + build.stderr
    executable.with_suffix('.runtimeconfig.json').write_text(json.dumps({
        'runtimeOptions': {'tfm': 'net8.0', 'framework': {
            'name': 'Microsoft.NETCore.App', 'version': '8.0.0',
        }},
    }))
    result = subprocess.run([dotnet, str(executable), str(ROOT)],
                            capture_output=True, text=True, timeout=45, check=False)
    assert result.returncode == 0, result.stdout + result.stderr
    return result.stdout
