export class FocusNavigation {
  constructor({ currentView, navigate, showKeyboardHint, interactionActive = () => false }) {
    this.currentView = currentView; this.navigate = navigate; this.showKeyboardHint = showKeyboardHint;
    this.revision = -1; this.autoOpened = false;
    this.interactionActive = interactionActive; this.pending = null;
  }
  receive(state) {
    if (!state || !Number.isSafeInteger(state.revision) || state.revision <= this.revision) return;
    if (this.pending && state.revision <= this.pending.revision) return;
    if (this.interactionActive()) { this.pending = state; return; }
    this.revision = state.revision;
    const view = this.currentView();
    if (state.editable && (view === 'remote' || view === 'keyboard')) {
      this.showKeyboardHint(state.password === true, state);
      if (view !== 'keyboard') { this.autoOpened = true; this.navigate('keyboard', { automatic: true }); }
    } else if (!state.editable && this.autoOpened && view === 'keyboard') {
      this.autoOpened = false; this.navigate('remote', { automatic: true });
    }
  }
  flushPending() {
    if (this.pending && !this.interactionActive()) { const state = this.pending; this.pending = null; this.receive(state); }
  }
  manualNavigation() { this.autoOpened = false; this.pending = null; }
  reset() { this.revision = -1; this.autoOpened = false; this.pending = null; }
}
