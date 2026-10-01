import test from 'node:test';
import assert from 'node:assert/strict';
import { FocusNavigation } from '../TvRemote.Server/wwwroot/focus-navigation.js';
function setup() {
  let view = 'remote', touching = false; const transitions = [], hints = [];
  const focus = new FocusNavigation({ currentView: () => view, navigate: name => { view = name; transitions.push(name); }, showKeyboardHint: value => hints.push(value), interactionActive: () => touching });
  return { focus, transitions, hints, setView: name => { view = name; focus.manualNavigation(); }, setTouching: value => touching = value };
}
test('editable focus opens keyboard and leaving it returns to remote', () => {
  const { focus, transitions } = setup(); focus.receive({editable:true,revision:1}); focus.receive({editable:false,revision:2});
  assert.deepEqual(transitions,['keyboard','remote']);
});
test('duplicate or stale focus cannot override a manual return to trackpad', () => {
  const { focus, transitions, setView } = setup(); focus.receive({editable:true,revision:3}); setView('remote');
  focus.receive({editable:true,revision:3}); focus.receive({editable:true,revision:2}); assert.equal(transitions.length,1);
  focus.receive({editable:true,revision:4}); assert.equal(transitions.length,2);
});
test('focus updates leave Apps and System alone', () => {
  const { focus, transitions, setView } = setup(); setView('apps'); focus.receive({editable:true,revision:1});
  setView('system'); focus.receive({editable:true,revision:2}); assert.equal(transitions.length,0);
});
test('manually opened keyboard stays open when PC focus changes', () => {
  const { focus, transitions, setView } = setup(); setView('keyboard'); focus.receive({editable:false,revision:1}); assert.equal(transitions.length,0);
});
test('password hints contain only metadata and reconnect resets revisions', () => {
  const { focus, hints, transitions } = setup(); focus.receive({editable:true,password:true,revision:100});
  assert.deepEqual(hints,[true]); focus.reset(); focus.receive({editable:false,revision:0});
  assert.deepEqual(transitions,['keyboard']);
});
test('focus waits until an active drag or two-finger gesture finishes', () => {
  const { focus, transitions, setTouching } = setup(); setTouching(true); focus.receive({editable:true,revision:1});
  assert.equal(transitions.length,0); setTouching(false); focus.flushPending(); assert.deepEqual(transitions,['keyboard']);
});
test('the latest pending focus wins and manual navigation clears pending focus', () => {
  const { focus, transitions, setTouching, setView } = setup(); setTouching(true); focus.receive({editable:true,revision:1}); focus.receive({editable:false,revision:2});
  setTouching(false); focus.flushPending(); assert.equal(transitions.length,0);
  setTouching(true); focus.receive({editable:true,revision:3}); setView('apps'); setTouching(false); focus.flushPending(); assert.equal(transitions.length,0);
});
