import test from 'node:test';
import assert from 'node:assert/strict';
import { LiveTyping } from '../TvRemote.Server/wwwroot/live-typing.js';
function setup() {
  const commands = [], typing = new LiveTyping(command => { commands.push(command); return true; });
  return { typing, commands };
}
test('keeps typed text visible and sends only each new character', () => {
  const { typing, commands } = setup(); typing.update('H'); typing.update('He'); typing.update('Hello');
  assert.equal(typing.value, 'Hello'); assert.deepEqual(commands, [{type:'text',value:'H'},{type:'text',value:'e'},{type:'text',value:'llo'}]);
});
test('backspace updates the phone and removes text on the PC', () => {
  const { typing, commands } = setup(); typing.update('Hello'); typing.update('Hell');
  assert.equal(typing.value, 'Hell'); assert.deepEqual(commands.at(-1), {type:'text_edit',before:0,remove:1,value:'',after:0});
});
test('Android replacement and autocorrection edit the existing PC word', () => {
  const { typing, commands } = setup(); typing.update('teh'); typing.update('the');
  assert.equal(typing.value, 'the'); assert.deepEqual(commands.at(-1), {type:'text_edit',before:0,remove:2,value:'he',after:0});
});
test('editing a selection in the middle retains the suffix and restores the caret', () => {
  const { typing, commands } = setup(); typing.update('Hello world'); typing.update('Hello sofa', 10);
  assert.deepEqual(commands.at(-1), {type:'text_edit',before:0,remove:5,value:'sofa',after:0});
  typing.update('Hey sofa', 3);
  assert.deepEqual(commands.at(-1), {type:'text_edit',before:-5,remove:3,value:'y',after:0});
});
test('deletion treats emoji and combining marks as complete graphemes', () => {
  const { typing, commands } = setup(); typing.update('a👨‍👩‍👧‍👦'); typing.update('a');
  assert.equal(commands.at(-1).remove, 1);
  typing.reset(); typing.update('e\u0301'); typing.update(''); assert.equal(commands.at(-1).remove, 1);
});
test('final Android composition event is not sent twice', () => {
  const { typing, commands } = setup(); typing.update('Příliš žluťoučký 🛋️'); typing.update('Příliš žluťoučký 🛋️');
  assert.equal(commands.length, 1); assert.equal(typing.value, 'Příliš žluťoučký 🛋️');
});
test('Enter uses an edit command so Windows receives an Enter key', () => {
  const { typing, commands } = setup(); typing.update('search'); typing.update('search\n');
  assert.deepEqual(commands.at(-1), {type:'text_edit',before:0,remove:0,value:'\n',after:0});
});
test('failed sends preserve the last sent echo and field changes reset it', () => {
  const typing = new LiveTyping(() => false); assert.equal(typing.update('unsent'), false); assert.equal(typing.value, '');
  typing.send = () => true; typing.update('old field'); typing.reset(); assert.equal(typing.value, ''); assert.equal(typing.caret, 0);
});
test('large replacement is rejected before sending oversized native edits', () => {
  const { typing, commands } = setup(); typing.update('a'.repeat(6000));
  assert.equal(typing.update('b'.repeat(6000)), false); assert.equal(commands.length, 1); assert.ok(typing.error);
});
