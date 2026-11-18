"""Narrow static guard: selected timing actions invoke pre-resolved controls. No runtime proof."""
from pathlib import Path
import re
import sys

FILES = ['FocusEndTimingTests','FocusSequenceTests','FocusResumeTests','TrayStopTests','PausedStopTests','HoverEdgeTests','FocusEdgeTests']
BEGIN = '// QA_TIMED_INVOKE_BEGIN'
END = '// QA_TIMED_INVOKE_END'

def check(text):
    errors=[]
    if text.count(BEGIN) != text.count(END) or not text.count(BEGIN): errors.append('missing_or_unbalanced_regions')
    for body in re.findall(re.escape(BEGIN)+r'(.*?)'+re.escape(END),text,re.S):
        body=re.sub(r'//[^\n]*','',body).strip()
        # Deliberately narrow: only Invoke on a local or cached button dictionary entry.
        if not re.fullmatch(r'(?:\s*(?:[A-Za-z_]\w*|buttons\[id\])\.Invoke\(\);\s*)+',body):
            errors.append('timed_region_not_cached_invoke_only')
    for body in re.findall(r'void Press\(string id\)\s*(?:=>[^;]*;|\{[^}]*\})',text,re.S):
        if re.search(r'Find\w*\s*\(|AsButton\s*\(|AsMenuItem\s*\(',body): errors.append('press_searches_for_control')
    for body in re.findall(r'var invocationStart = recorder.Mark\(\);(.*?)var invocationEnd = recorder.Mark\(\);',text,re.S):
        if re.search(r'Find\w*\s*\(|AsButton\s*\(|AsMenuItem\s*\(',body): errors.append('invocation_bracket_search')
    return errors

def self_test():
    good=BEGIN+'\nendButton.Invoke();\n'+END
    assert not check(good)
    for call in ['Find("FocusEndButton").AsButton().Invoke();','panel.FindFirstDescendant(cf => cf.ByName("End")).AsButton().Invoke();','stop.AsMenuItem().Invoke();']:
        assert check(BEGIN+'\n'+call+'\n'+END)
    assert check('void Press(string id) => Find(id).AsButton().Invoke();\n'+good)
    assert check('var invocationStart = recorder.Mark(); Find("End"); var invocationEnd = recorder.Mark();\n'+good)
    assert check(good.replace(END,''))
    print('TIMING_GUARD_SELF_TEST_OK')

if __name__=='__main__':
    if '--self-test' in sys.argv: self_test()
    errors=[]
    for name in FILES:
        errors += [name+':'+e for e in check((Path(__file__).parent/'Journeys'/(name+'.cs')).read_text(encoding='utf-8-sig'))]
    if errors: print('\n'.join(errors)); sys.exit(1)
    print('TIMING_GUARD_OK')
