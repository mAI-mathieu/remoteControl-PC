export class TrackpadGestures {
  constructor(send, { now = () => performance.now(), later = (fn, ms) => setTimeout(fn, ms), cancel = id => clearTimeout(id), state = () => {} } = {}) {
    this.send = send; this.now = now; this.later = later; this.cancel = cancel; this.state = state;
    this.points = new Map(); this.sensitivity = 1; this.reset();
  }
  reset() {
    this.cancel(this.hold); this.hold = null;
    if (this.dragging) this.send({ type: 'mouse_up', button: 'left' });
    this.dragging = false; this.points?.clear(); this.dx = this.dy = this.scrollX = this.scrollY = 0;
    this.maxFingers = 0; this.distance = 0; this.state(false, false);
  }
  down(id, x, y) {
    if (!this.points.size) { this.started = this.now(); this.distance = 0; this.maxFingers = 0; this.canceled = false; }
    this.points.set(id, { x, y }); this.maxFingers = Math.max(this.maxFingers, this.points.size);
    this.cancel(this.hold);
    if (this.points.size === 1 && this.maxFingers === 1) {
      this.hold = this.later(() => {
        if (this.points.size === 1 && this.distance < 9) {
          this.flush(); this.dragging = true; this.send({ type: 'mouse_down', button: 'left' }); this.state(true, true);
        }
      }, 450);
    } else if (this.dragging) {
      this.send({ type: 'mouse_up', button: 'left' }); this.dragging = false;
    }
    this.state(true, this.dragging);
  }
  move(id, x, y) {
    const point = this.points.get(id); if (!point) return;
    const dx = x - point.x, dy = y - point.y;
    point.x = x; point.y = y; this.distance += Math.hypot(dx, dy) / this.points.size;
    if (this.distance > 9) this.cancel(this.hold);
    if (this.points.size === 1 && this.maxFingers === 1) { this.dx += dx * this.sensitivity; this.dy += dy * this.sensitivity; }
    else if (this.points.size === 2) { this.scrollY += dy * 3 / 2; this.scrollX -= dx * 3 / 2; }
  }
  up(id, canceled = false) {
    if (!this.points.has(id)) return;
    this.canceled ||= canceled;
    this.cancel(this.hold); this.points.delete(id);
    if (!this.points.size) {
      this.flush();
      if (this.dragging) { this.send({ type: 'mouse_up', button: 'left' }); this.dragging = false; }
      else if (!this.canceled && this.distance < 9 && this.now() - this.started < 400 && this.maxFingers <= 2) {
        this.send({ type: 'mouse_click', button: this.maxFingers === 2 ? 'right' : 'left' });
      }
      this.state(false, false);
    }
  }
  flush() {
    const clamp = (value, limit) => Math.max(-limit, Math.min(limit, Math.trunc(value)));
    const dx = clamp(this.dx, 2000), dy = clamp(this.dy, 2000);
    const delta = clamp(this.scrollY, 2400), horizontal = clamp(this.scrollX, 2400);
    if (dx || dy) { this.send({ type: 'mouse_move', dx, dy }); this.dx -= dx; this.dy -= dy; }
    if (delta || horizontal) { this.send({ type: 'scroll', delta, horizontal }); this.scrollY -= delta; this.scrollX -= horizontal; }
  }
}
