from pathlib import Path
import re


ROOT = Path(__file__).parents[1] / 'CivicSurvival'
MEMBERS = {
    'IPostLoadValidation': ('HydrationOrder', 'ValidateAfterLoad', 'PurgeAfterLoad'),
    'IInitializable': ('InitOrder', 'OnInitialize'),
    'IBuildingRefRebindOwner': ('RebindOrder', 'ReboundComponentTypes', 'RebindBuildingRefsAfterLoad'),
    'INarrativeResolver': ('NotifyDeserialized', 'Reset'),
}


def sources():
    for path in ROOT.rglob('*.cs'):
        if not {'bin', 'obj'} & set(path.parts):
            yield path, re.sub(r'//[^\n]*|/\*.*?\*/', '', path.read_text(encoding='utf-8-sig'), flags=re.S)


def test_net48_lifecycle_interfaces_have_no_runtime_default_bodies():
    for path, source in sources():
        if path.stem in MEMBERS:
            body = source.split('public interface ' + path.stem, 1)[1]
            body = body.split('public static class', 1)[0]
            assert '=>' not in body, path
            assert not re.search(r'\)\s*\{', body), path


def test_every_lifecycle_implementer_explicitly_preserves_the_contract():
    classes = {}
    implementations = []
    for path, source in sources():
        for match in re.finditer(r'\bclass\s+(\w+)\s*(?::([^{}]+))?\s*\{', source):
            classes.setdefault(match[1], []).append(source[match.end():])
            for interface in MEMBERS:
                if re.search(r'\b' + interface + r'\b', match[2] or ''):
                    implementations.append((path, match[1], interface))
    assert len(implementations) >= 100
    for path, name, interface in implementations:
        source = '\n'.join(classes[name])
        for member in MEMBERS[interface]:
            assert re.search(r'\bpublic\s+(?:(?:override|virtual)\s+)?[\w<>\[\]]+\s+' + member + r'\b', source), (path, member)
