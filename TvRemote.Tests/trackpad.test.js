import test from 'node:test';
import assert from 'node:assert/strict';
import { TrackpadGestures } from '../TvRemote.Server/wwwroot/trackpad.js';
function setup() {
  const messages = []; let time = 0, hold;
  const pad = new TrackpadGestures(value => messages.push(value), { now: () => time, later: fn => hold = fn, cancel: () => hold = null });
  return { pad, messages, advance: ms => time += ms, hold: () => hold?.() };
}
test('movement coalesces and preserves fractional movement', () => {
  const { pad, messages } = setup(); pad.down(1, 0, 0);
  for (let i = 1; i <= 20; i++) pad.move(1, i * .75, i * .25);
  assert.equal(messages.length, 0); pad.flush();
  assert.deepEqual(messages, [{ type:'mouse_move', dx:15, dy:5 }]);
});
test('tap and double tap produce clicks without artificial delay', () => {
  const { pad, messages, advance } = setup();
  pad.down(1, 5, 5); advance(30); pad.up(1); pad.down(1, 5, 5); advance(30); pad.up(1);
  assert.deepEqual(messages.map(m => m.type), ['mouse_click','mouse_click']);
});
test('two finger tap is only a right click', () => {
  const { pad, messages, advance } = setup(); pad.down(1, 0, 0); pad.down(2, 20, 0); advance(40); pad.up(1); pad.up(2);
  assert.deepEqual(messages, [{ type:'mouse_click', button:'right' }]);
});
test('two finger scroll uses average displacement on both axes', () => {
  const { pad, messages } = setup(); pad.down(1, 0, 0); pad.down(2, 20, 0); pad.move(1, 10, 20); pad.move(2, 30, 20); pad.flush(); pad.up(1); pad.up(2);
  assert.deepEqual(messages, [{ type:'scroll', delta:60, horizontal:-30 }]);
});
test('hold starts a drag and cancellation releases it', () => {
  const { pad, messages, hold } = setup(); pad.down(1, 0, 0); hold(); pad.move(1, 20, 30); pad.flush(); pad.reset();
  assert.deepEqual(messages.map(m => m.type), ['mouse_down','mouse_move','mouse_up']);
});
test('moving before hold prevents an accidental drag', () => {
  const { pad, messages, hold } = setup(); pad.down(1, 0, 0); pad.move(1, 20, 0); hold(); pad.up(1);
  assert.deepEqual(messages, [{ type:'mouse_move', dx:20, dy:0 }]);
});
test('third finger and pointer cancellation never click', () => {
  const { pad, messages } = setup(); pad.down(1, 0, 0); pad.down(2, 10, 0); pad.down(3, 20, 0); pad.up(1); pad.up(2); pad.up(3);
  pad.down(1, 0, 0); pad.up(1, true); assert.equal(messages.length, 0);
});
test('huge movement is bounded and residuals are flushed next frame', () => {
  const { pad, messages } = setup(); pad.down(1, 0, 0); pad.move(1, 3000, 0); pad.flush(); pad.flush();
  assert.equal(messages[0].dx, 2000); assert.equal(messages[1].dx, 1000);
});
test('canceling either finger suppresses the entire two-finger tap', () => {
  const { pad, messages } = setup(); pad.down(1, 0, 0); pad.down(2, 10, 0); pad.up(1, true); pad.up(2);
  assert.equal(messages.length, 0);
});
