const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const icon = name => `<svg aria-hidden="true"><use href="#i-${name}"/></svg>`;
const state = {demo:'explain', form:'pocket', avatar:'ghost', type:'rounded', generation:0, timers:[], marks:false, focusRunning:false, seconds:25*60, sampleEdited:false};
const names = {pocket:'Pocket notch',float:'Floating friend',strip:'Assistant strip',ghost:'Little ghost',berry:'Blueberry',eyes:'Just eyes',rounded:'Warm & rounded',native:'Familiar Windows'};
const scenes = {
  explain:{number:'MOMENT 01 / EXPLAIN',title:'“Boo, show me what to change.”',runner:'Explain the button on this screen',description:'Boo points to the exact spot and keeps the explanation beside it.',action:'Show me',icon:'spark'},
  voice:{number:'MOMENT 02 / CONVERSATION',title:'“Boo, can we talk this through?”',runner:'Try a short conversation',description:'Preview listening, captions, a spoken reply, and interruption.',action:'Play conversation',icon:'mic'},
  research:{number:'MOMENT 03 / DELEGATE',title:'“Boo, find a few good references.”',runner:'Research, then prepare a shortlist',description:'See sources, a proposed file change, and an approval step.',action:'Try the task',icon:'search'},
  focus:{number:'BONUS / FOCUS',title:'A small nudge. A little momentum.',runner:'Start a quiet focus session',description:'A timer, clear screen-sharing status, and a gentle way back.',action:'Start preview',icon:'clock'}
};
function avatar(kind){
  if(kind==='ghost')return '<img src="assets/boo-ghost.png" alt="">';
  if(kind==='eyes')return '<svg viewBox="0 0 72 72" aria-hidden="true"><rect x="1" y="1" width="70" height="70" rx="24" fill="#292736"/><ellipse cx="25" cy="36" rx="11" ry="15" fill="#fcfaf5"/><ellipse cx="49" cy="36" rx="11" ry="15" fill="#fcfaf5"/><ellipse cx="28" cy="38" rx="5" ry="7" fill="#5f5689"/><ellipse cx="52" cy="38" rx="5" ry="7" fill="#5f5689"/></svg>';
  return '<svg viewBox="0 0 80 80" aria-hidden="true"><path d="M12 28Q10 13 31 14Q40 8 50 14Q72 13 70 33L73 55Q71 72 54 70Q39 76 25 70Q7 72 8 54Z" fill="#a9bcff"/><path d="m30 15 3-9 7 6 7-6 4 10-11 5Z" fill="#4254d6"/><ellipse cx="28" cy="39" rx="13" ry="17" fill="#fffdf7"/><ellipse cx="55" cy="39" rx="13" ry="17" fill="#fffdf7"/><ellipse cx="32" cy="42" rx="6" ry="8" fill="#252756"/><ellipse cx="59" cy="42" rx="6" ry="8" fill="#252756"/><ellipse cx="16" cy="56" rx="7" ry="4" fill="#e7b1cd"/><ellipse cx="65" cy="56" rx="7" ry="4" fill="#e7b1cd"/><ellipse cx="41" cy="59" rx="3" ry="3" fill="#2b2f8f"/></svg>';
}
function renderAvatars(){ $('#notch-avatar').innerHTML=avatar(state.avatar); $('#panel-avatar').innerHTML=avatar(state.avatar); $$('[data-avatar-swatch]').forEach(e=>e.innerHTML=avatar(e.dataset.avatarSwatch)); }
function setPrivacy(text,active=false){$('#privacy-label').innerHTML=`<i class="${active?'active':''}"></i>${text}`;$('#notch-signal').style.background=active?'#b6a7fa':'#a2a6ba';}
function showPanel(status='Ready to help'){ $('#companion-panel').hidden=false;$('#notch').setAttribute('aria-expanded','true');$('#panel-status').textContent=status; }
function setBody(html){$('#panel-body').innerHTML=html;}
function closePanel(){ $('#companion-panel').hidden=true;$('#notch').setAttribute('aria-expanded','false'); }
function later(fn,ms){const generation=state.generation;state.timers.push(setTimeout(()=>{if(generation===state.generation)fn();},ms));}
function cancelPending(){state.generation++;state.timers.forEach(clearTimeout);state.timers=[];state.focusRunning=false;if('speechSynthesis'in window)speechSynthesis.cancel();}
function clearMarks(){state.marks=false;$('#annotations').hidden=true;$('#annotation-controls').hidden=true;}
function toast(message){$('#toast').textContent=message;$('#toast').hidden=false;clearTimeout(window.toastTimer);window.toastTimer=setTimeout(()=>$('#toast').hidden=true,4500);}
function setStatus(text){$('#notch-status').textContent=text;}
function switchDemo(demo){
  cancelPending();clearMarks();closePanel();setBody('');$('#panel-status').textContent='Ready to help';state.demo=demo;state.seconds=1500;
  setSampleEdit(false);
  $$('.demo').forEach(b=>{b.classList.toggle('active',b.dataset.demo===demo);b.setAttribute('aria-pressed',b.dataset.demo===demo)});
  const s=scenes[demo];$('#scene-number').textContent=s.number;$('#scene-title').textContent=s.title;$('#runner-title').textContent=s.runner;$('#runner-description').textContent=s.description;$('#run-demo').innerHTML=s.action+icon('arrow');$('#runner-icon').innerHTML=icon(s.icon);
  $('#sample-app').hidden=demo==='research'||demo==='focus';$('#research-workspace').hidden=demo!=='research';$('#focus-workspace').hidden=demo!=='focus';$('#research-results').hidden=true;$('.research-placeholder').hidden=false;
  $('#research-results').innerHTML='';setPrivacy('Screen off · Mic off');setStatus('Here when you need me');$('#desktop-status').textContent='Illustrative desktop · no live capture';
}
function introPanel(){
  cancelPending();clearMarks();setStatus('Here when you need me');
  showPanel();setBody(`<h3>Hey, I’m Boo.</h3><p>We can talk it through, look at something together, or get a small task done.</p><span class="sample-tag">Choose a sample to try this visual concept.</span><div class="panel-actions"><button class="secondary-button" data-action="explain">${icon('spark')} Explain</button><button class="secondary-button" data-action="voice">${icon('mic')} Talk</button><button class="secondary-button" data-action="research">${icon('search')} Do a task</button></div><form class="input-row" id="chat-form"><input id="chat-input" aria-label="Message Boo in the preview" placeholder="Type to Boo…" autocomplete="off"><button aria-label="Send preview message">${icon('arrow')}</button></form>`);setPrivacy('Screen off · Mic off');
}
function drawAnnotation(){
  state.marks=true;$('#annotations').hidden=false;$('#annotation-controls').hidden=false;layoutAnnotation();
}
function layoutAnnotation(){
  if(!state.marks)return;
  const screen=$('#desktop').getBoundingClientRect(),target=$('#target-button').getBoundingClientRect();
  const left=target.left-screen.left,top=target.top-screen.top;
  const ring=$('#target-ring');Object.assign(ring.style,{left:`${left-7}px`,top:`${top-6}px`,width:`${target.width+14}px`,height:`${target.height+12}px`});
  const note=$('#annotation-note'),small=screen.width<650;
  const noteX=small?Math.max(15,left-15):Math.max(14,left-244),noteY=small?Math.max(150,top-160):Math.max(165,top-90);
  note.style.left=noteX+'px';note.style.top=noteY+'px';
  const nw=note.offsetWidth,nh=note.offsetHeight;
  $('#annotation-svg').setAttribute('viewBox',`0 0 ${screen.width} ${screen.height}`);
  const x1=small?noteX+nw*.72:noteX+nw,y1=small?noteY+nh:noteY+nh*.6,x2=small?left+target.width*.8:left-12,y2=small?top-10:top+target.height*.5;
  $('#annotation-arrow').setAttribute('d',small?`M${x1} ${y1} Q${x1+26} ${y1+15} ${x2} ${y2}`:`M${x1+4} ${y1} Q${x2-22} ${y1-5} ${x2} ${y2}`);
}
function explain(){
  cancelPending();switchDemo('explain');showPanel('Looking together · sample');setStatus('Let’s look together');setPrivacy('Sample screen shared · Mic off',true);
  setBody(`<div class="user-bubble">Boo, why is this button hard to find?</div><h3>It’s getting a little lost.</h3><p>The button’s fill is close to the background. I’ve marked it for you.</p><div class="panel-actions"><button class="primary-button" data-action="apply-edit">Make that change ${icon('arrow')}</button><button class="secondary-button" data-action="speak-explanation">${icon('volume')} Hear it</button></div><div class="speak-status" id="speak-status"></div><span class="sample-tag">Scripted explanation. The edit changes this sample canvas.</span>`);
  drawAnnotation();
  if($('#desktop').clientWidth<650){closePanel();toast('Boo marked the button. Click Boo to read the explanation.');}
}
function setSampleEdit(edited){
  state.sampleEdited=edited;$('#target-button').classList.toggle('edited',edited);
  const note=$('#annotation-note');
  note.querySelector('strong').textContent=edited?'One clear next step.':'Give the next step a little more presence.';
  note.querySelector('p').textContent=edited?'A deeper green fill and light text make the button easier to find.':'The button blends into the page. A stronger fill makes it easier to find.';
}
function applySampleEdit(){
  setSampleEdit(true);showPanel('Applied to the sample');setStatus('A small change, made');setPrivacy('Sample screen shared · Mic off',true);drawAnnotation();
  setBody(`<div class="user-bubble">Boo, make that change.</div><h3>There. A clearer next step.</h3><p>I gave the button a deeper green fill and light text. You can see the change on the canvas.</p><div class="edit-receipt"><span class="before-swatch"></span>${icon('arrow')}<span class="after-swatch"></span><span>Button fill & text updated</span>${icon('check')}</div><div class="panel-actions"><button class="secondary-button" data-action="undo-edit">Undo change</button><button class="secondary-button" data-action="minimize-explanation">See the whole screen ${icon('arrow')}</button></div><span class="sample-tag" style="margin-top:12px">This sample changed. No connected app or file was edited.</span>`);
  $('#desktop-status').textContent='Sample canvas edited · Undo available';
  $('#runner-title').textContent='Boo made the change on the sample screen';$('#runner-description').textContent='The button now has a stronger fill. Try Undo to compare with the original.';
  if($('#desktop').clientWidth<650)closePanel();
}
function undoSampleEdit(){
  setSampleEdit(false);showPanel('Sample change undone');setStatus('Back to the original');drawAnnotation();
  setBody(`<h3>Back to how it was.</h3><p>The sample button’s original fill and text color are restored.</p><div class="panel-actions"><button class="primary-button" data-action="apply-edit">Apply the change again ${icon('arrow')}</button><button class="secondary-button" data-action="minimize-explanation">See the screen</button></div><span class="sample-tag" style="margin-top:12px">Undo applies only to this visual demo.</span>`);
  $('#desktop-status').textContent='Sample edit undone · original restored';$('#runner-title').textContent='Compare the original button';$('#runner-description').textContent='Reapply the change from Boo’s panel, or reset the demo.';
}
const voiceAnswer='Of course. Let’s make one small change first. Give the button a stronger fill, so the next step is easy to spot. I can point it out for you.';
function waveform(playing=false){return `<div class="waveform ${playing?'playing':''}" aria-hidden="true">${[10,15,24,15,32,21,12,26,34,18,27,15,9,19,28,14,23,11,17].map((h,i)=>`<i style="--height:${h}px;--delay:${i*-.09}s"></i>`).join('')}</div>`;}
function voiceDemo(){
  switchDemo('voice');showPanel('Listening state · simulated');setStatus('Listening · sample');setPrivacy('Screen off · Mic simulated',true);
  setBody(`<span class="sample-tag">SCRIPTED VOICE DEMO · MICROPHONE OFF</span><h3>I’m listening.</h3>${waveform(true)}<div class="voice-label">A listening-state rehearsal</div><div class="voice-caption">“I’m not sure where to start with this screen.”</div><div class="panel-actions"><button class="secondary-button" data-action="stop">${icon('stop')} Interrupt demo</button></div>`);
  later(voiceReply,2000);
}
function voiceReply(){
  $('#panel-status').textContent='Reply ready · sample';setStatus('One small step');setPrivacy('Screen off · Mic off');
  setBody(`<div class="user-bubble">I’m not sure where to start with this screen.</div><h3>One small change first.</h3><p>${voiceAnswer}</p><span class="sample-tag">Sample reply · audio uses your browser’s available voice.</span><div class="panel-actions"><button class="primary-button" data-action="speak-voice">${icon('volume')} Hear Boo</button><button class="secondary-button" data-action="explain">Point it out ${icon('arrow')}</button></div><div class="speak-status" id="speak-status"></div><form class="input-row" id="chat-form"><input id="chat-input" aria-label="Message Boo in the preview" placeholder="Or type a follow-up…" autocomplete="off"><button aria-label="Send preview message">${icon('arrow')}</button></form>`);
}
function speak(text){
  if(!('speechSynthesis' in window)){toast('This browser has no speech playback. The full reply is shown as text.');return;}
  speechSynthesis.cancel();const utterance=new SpeechSynthesisUtterance(text);utterance.rate=.97;utterance.pitch=1.04;
  const voice=speechSynthesis.getVoices().find(v=>v.lang.startsWith('en')&&/Natural|Aria|Zira|Jenny/.test(v.name))||speechSynthesis.getVoices().find(v=>v.lang.startsWith('en'));
  if(voice)utterance.voice=voice;
  const generation=state.generation;
  utterance.onstart=()=>{if(generation!==state.generation)return;$('#panel-status').textContent='Speaking sample · mic off';if($('#speak-status'))$('#speak-status').textContent='Sample audio is playing. Stop interrupts it.';};
  utterance.onend=()=>{if(generation!==state.generation)return;$('#panel-status').textContent='Reply complete · sample';if($('#speak-status'))$('#speak-status').textContent='Sample audio finished.';};
  utterance.onerror=()=>{if(generation!==state.generation)return;if($('#speak-status'))$('#speak-status').textContent='Audio could not play. You can read the same reply above.';};
  speechSynthesis.speak(utterance);
}
const references=[{title:'Mobbin — onboarding patterns',description:'Explore onboarding examples from real products. Review access and select relevant flows.',url:'https://mobbin.com/',domain:'mobbin.com'}, {title:'HeyClicky — Windows companion',description:'A reference for a screen-top assistant and explanations directly on your screen.',url:'https://www.heyclicky.com/for-windows',domain:'heyclicky.com'}, {title:'Riley’s Bluey — companion behavior',description:'Source reference for expressive eyes, focus sessions, and a friendly presence.',url:'https://github.com/rbrown101010/bluey-by-riley',domain:'github.com'}];
function revealReferences(){
  $('.research-placeholder').hidden=true;$('#research-results').hidden=false;
  $('#research-results').innerHTML='<div class="research-tag">ILLUSTRATIVE SHORTLIST · NOT LIVE SEARCH RESULTS</div>'+references.map(r=>`<a class="reference-link" href="${r.url}" target="_blank" rel="noreferrer"><strong>${r.title} ↗</strong><p>${r.description}</p><span>${r.domain}</span></a>`).join('');
}
function researchDemo(){
  switchDemo('research');showPanel('Preparing sample shortlist');setStatus('Working on your request');
  setBody(`<div class="user-bubble">Find a few good references for this onboarding screen, then save me a shortlist.</div><h3>I’ll bring the useful bits back.</h3><div class="task-list"><div class="task-row">${icon('check')} Understand your request</div><div class="task-row"><span class="step-circle"></span> Gather references</div><div class="task-row"><span class="step-circle"></span> Prepare a shortlist</div></div><span class="sample-tag">Scripted task progress. No live search or file access.</span>`);
  later(()=>{revealReferences();researchReview();},1200);
}
function researchReview(){
  $('#panel-status').textContent='Ready for your review · sample';setStatus('A small check before I act');
  setBody(`<h3>Here’s a starting point.</h3><p>The sample shortlist includes Mobbin, HeyClicky, and Riley’s Bluey. You can inspect their links behind this panel.</p><div class="approval-box"><strong>Review this proposed file change</strong><div>Replace <code>Documents / Boo / onboarding-references.md</code> with the three source links and notes shown here.</div></div><div class="panel-actions"><button class="primary-button" data-action="approve">Approve sample</button><button class="secondary-button" data-action="cancel-action">Cancel</button></div><span class="sample-tag" style="margin-top:10px">A simulated overwrite. This preview cannot read or write your files.</span>`);
}
function approved(){
  $('#panel-status').textContent='Rehearsal complete';setStatus('All set · sample');
  setBody(`<h3>That’s the handoff.</h3><p class="success-note">You approved the sample change.</p><p>In the Windows app, Boo would write the file, read it back, and only then report success.</p><div class="file-preview">${icon('file')} onboarding-references.md</div><p>No file was changed in this preview.</p><div class="panel-actions"><button class="secondary-button" data-action="view-sources">View the sources ${icon('arrow')}</button><button class="secondary-button" data-action="research">Replay task</button></div>`);
}
function cancelledAction(){
  cancelPending();setStatus('Nothing changed');$('#panel-status').textContent='Action cancelled';setBody('<h3>We’ll leave it there.</h3><p>The proposed action was cancelled. Nothing was changed.</p><div class="panel-actions"><button class="secondary-button" data-action="research">Try again</button></div>');
}
function focusDemo(){
  switchDemo('focus');state.focusRunning=true;showPanel('Focus session · preview');setStatus('25:00 · Finish the welcome screen');setPrivacy('Screen sharing simulated · Mic off',true);renderFocus();tickFocus();
}
function renderFocus(){
  const m=Math.floor(state.seconds/60).toString().padStart(2,'0'),s=(state.seconds%60).toString().padStart(2,'0');
  setBody(`<span class="sample-tag">FOCUS PREVIEW · NO SCREEN CAPTURE</span><div class="focus-current">Finish the welcome screen</div><div class="focus-timer">${m}:${s}</div><div class="focus-current">${state.focusRunning?'A little space to do your thing.':'Paused. Take the time you need.'}</div><div class="panel-actions" style="justify-content:center"><button class="secondary-button" data-action="toggle-focus">${icon(state.focusRunning?'pause':'play')} ${state.focusRunning?'Pause':'Resume'}</button><button class="secondary-button" data-action="nudge">Preview a nudge</button></div><div class="focus-track" aria-hidden="true"><i style="width:68%"></i><i style="width:10%"></i><i style="width:22%"></i></div><div class="focus-legend"><span>On task</span><span>Off task</span><span>Unknown</span></div><p class="notice">Illustrative history, not measurements. Uncertain screen checks stay “unknown.”</p>`);
}
function tickFocus(){
  if(!state.focusRunning)return;
  later(()=>{if(!state.focusRunning)return;state.seconds=Math.max(0,state.seconds-1);const m=Math.floor(state.seconds/60).toString().padStart(2,'0'),s=(state.seconds%60).toString().padStart(2,'0');const el=$('.focus-timer');if(el)el.textContent=`${m}:${s}`;setStatus(`${m}:${s} · Focusing`);if(state.seconds>0)tickFocus();else stopAll();},1000);
}
function toggleFocus(){
  if(state.focusRunning){cancelPending();setPrivacy('Screen off · Mic off');setStatus('Focus paused');}
  else{state.focusRunning=true;setPrivacy('Screen sharing simulated · Mic off',true);tickFocus();}
  renderFocus();
}
function nudge(){
  cancelPending();showPanel('Gentle nudge · sample');setStatus('A little way back');setPrivacy('Screen off · Mic off');
  setBody(`<h3>Shall we come back to it?</h3><p>You were working on the welcome screen. We can take one small step, or call this a break.</p><span class="sample-tag">Manually triggered example. No browsing was observed.</span><div class="panel-actions"><button class="primary-button" data-action="back-focus">Back to my task</button><button class="secondary-button" data-action="take-break">Take a break</button></div>`);
}
function stopAll(){
  cancelPending();clearMarks();setPrivacy('Screen off · Mic off');setStatus('Stopped · you’re in control');showPanel('Stopped');setBody('<h3>Stopped.</h3><p>Audio and the demo have stopped. Any pending sample action has been cancelled.</p><div class="panel-actions"><button class="secondary-button" data-action="home">Start something new</button></div>');
}
function changeForm(form){
  state.form=form;$('#desktop').dataset.form=form;$('#desktop').dataset.ceVariant={pocket:'pocket-notch',float:'floating-friend',strip:'assistant-strip'}[form];
  $$('[data-form-choice]').forEach(b=>{const selected=b.dataset.formChoice===form;b.classList.toggle('chosen',selected);b.setAttribute('aria-pressed',selected);b.querySelector('.choice-indicator').textContent=selected?'✓':'';});
  updateSelection();if(state.marks)requestAnimationFrame(layoutAnnotation);
}
function updateSelection(){ $('#selection-summary').textContent=`${names[state.form]} · ${names[state.avatar]} · ${names[state.type]}`; }
const actions={explain,voice:voiceDemo,research:researchDemo,focus:focusDemo,stop:stopAll,home:introPanel,approve:approved,'apply-edit':applySampleEdit,'undo-edit':undoSampleEdit,'cancel-action':cancelledAction,'minimize-explanation':closePanel,'view-sources':closePanel,'speak-explanation':()=>speak('The button’s fill is close to the background. Give it a stronger fill so the next step is easier to find.'),'speak-voice':()=>speak(voiceAnswer),'toggle-focus':toggleFocus,nudge,'back-focus':()=>{state.focusRunning=true;showPanel('Focus session · preview');setPrivacy('Screen sharing simulated · Mic off',true);renderFocus();tickFocus();},'take-break':()=>{closePanel();setStatus('Taking a break');toast('Focus preview paused. Resume only when you choose.');}};
document.addEventListener('click',event=>{const a=event.target.closest('[data-action]');if(a&&actions[a.dataset.action])actions[a.dataset.action]();});
document.addEventListener('submit',event=>{
  if(event.target.id!=='chat-form')return;event.preventDefault();const input=$('#chat-input'),message=input.value.trim();if(!message)return;
  const normalized=message.toLowerCase();if(/explain|button|draw|point/.test(normalized)){explain();return;}if(/search|research|reference|mobbin/.test(normalized)){researchDemo();return;}if(/talk|voice|hello|hi boo/.test(normalized)){voiceDemo();return;}
  const bubble=document.createElement('div');bubble.className='user-bubble';bubble.textContent=message;$('#panel-body').insertBefore(bubble,event.target);
  const reply=document.createElement('p');reply.className='notice';reply.textContent='I can only play the three prepared examples in this preview. Try “explain the button,” “let’s talk,” or “find references.” Live AI comes after the design review.';$('#panel-body').insertBefore(reply,event.target);input.value='';reply.scrollIntoView({block:'nearest'});
});
$$('.demo').forEach(b=>b.addEventListener('click',()=>switchDemo(b.dataset.demo)));
$$('[data-form-choice]').forEach(b=>b.addEventListener('click',()=>{changeForm(b.dataset.formChoice);toast(`${names[state.form]} is now on the sample screen.`);}));
$$('input[name=avatar]').forEach(input=>input.addEventListener('change',()=>{state.avatar=input.value;renderAvatars();updateSelection();}));
$$('input[name=type]').forEach(input=>input.addEventListener('change',()=>{state.type=input.value;$('#desktop').dataset.type=state.type;updateSelection();}));
$('#run-demo').addEventListener('click',()=>actions[state.demo]());$('#reset').addEventListener('click',()=>{switchDemo(state.demo);toast('Demo reset. Your appearance choices are kept.');});
$('#notch').addEventListener('click',()=>{if($('#companion-panel').hidden){if($('#panel-body').childElementCount)showPanel($('#panel-status').textContent);else introPanel();}else closePanel();});
$('#close-panel').addEventListener('click',closePanel);$('#clear-marks').addEventListener('click',()=>{clearMarks();setPrivacy('Screen off · Mic off');setStatus('Marks cleared');toast('Marks cleared from the sample screen.');});$('#stop-all').addEventListener('click',stopAll);
$('#open-idle').addEventListener('click',introPanel);$('#compare-button').addEventListener('click',()=>$('#directions').scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth'}));
$('#try-selection').addEventListener('click',()=>{$('.preview-area').scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth',block:'start'});introPanel();});
$('#target-button').addEventListener('click',()=>toast('This is an illustrative design, not a connected app. Ask Boo to explain the button.'));
document.addEventListener('keydown',e=>{if(e.key==='Escape'){cancelPending();clearMarks();closePanel();setPrivacy('Screen off · Mic off');setStatus('Here when you need me');}});
window.addEventListener('resize',layoutAnnotation);window.addEventListener('pagehide',cancelPending);
renderAvatars();switchDemo('explain');
