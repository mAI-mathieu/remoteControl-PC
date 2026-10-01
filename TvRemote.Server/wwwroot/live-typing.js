// Keep a local echo of text typed by this phone, without reading the PC's field.
export class LiveTyping {
  constructor(send) {
    this.send = send;
    this.segmenter = typeof Intl.Segmenter === 'function' ? new Intl.Segmenter(undefined, { granularity: 'grapheme' }) : null;
    this.reset();
  }
  parts(value) { return this.segmenter ? Array.from(this.segmenter.segment(value), part => part.segment) : Array.from(value); }
  reset() { this.value = ''; this.caret = 0; this.error = null; }
  update(value, selectionEnd = value.length) {
    this.error = null;
    if (value === this.value) return true;
    const old = this.parts(this.value), next = this.parts(value);
    let prefix = 0, suffix = 0;
    while (prefix < old.length && prefix < next.length && old[prefix] === next[prefix]) prefix++;
    while (suffix < old.length - prefix && suffix < next.length - prefix && old[old.length - suffix - 1] === next[next.length - suffix - 1]) suffix++;
    const inserted = next.slice(prefix, next.length - suffix).join('');
    const before = old.length - suffix - this.caret;
    const remove = old.length - prefix - suffix;
    const caret = this.parts(value.slice(0, selectionEnd)).length;
    const after = caret - (next.length - suffix);
    if (value.length > 10000 || Math.abs(before) + remove + inserted.length + Math.abs(after) > 10000) {
      this.error = 'This edit is too large. Use a shorter selection.'; return false;
    }
    const command = before === 0 && remove === 0 && after === 0 && inserted && !inserted.includes('\n')
      ? { type: 'text', value: inserted }
      : { type: 'text_edit', before, remove, value: inserted, after };
    if (!this.send(command)) return false;
    this.value = value; this.caret = caret;
    return true;
  }
}
