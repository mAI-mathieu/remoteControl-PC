import { TrackpadGestures } from './trackpad.js';
import { FocusNavigation } from './focus-navigation.js';
import { LiveTyping } from './live-typing.js';
const $ = selector => document.querySelector(selector);
let token = ''; try { token = localStorage.getItem('tvremote-token') || ''; } catch { }
let socket, connected = false, retry = 0, reconnectTimer, heartbeat, lastPong = 0, toastTimer, composing = false, loginOnly = false;
const modifiers = new Map();
const focusNavigation = new FocusNavigation({
  currentView: () => $('.view.active')?.id.replace('view-', ''),
  interactionActive: () => gestures.points.size > 0 || gestures.dragging,
  navigate: (name, options) => navigate(name, options),
  showKeyboardHint: (password, state) => {
    if (state?.revision !== liveFieldRevision) { resetLive(); liveFieldRevision = state?.revision; }
    $('#keyboard-context').textContent = loginOnly ? 'Windows sign-in. Select the PIN/password field on your PC, then type here.' : password ? 'Password field selected on your PC.' : 'Text field selected on your PC.';
    $('#live-input').classList.toggle('private-input', password);
    $('#live-input').setAttribute('aria-label', password ? 'Type into the selected password field' : 'Type into the selected text field');
  }
});
const haptic = () => navigator.vibrate?.(8);
function toast(message) { $('#toast').textContent = message; $('#toast').classList.add('visible'); clearTimeout(toastTimer); toastTimer = setTimeout(() => $('#toast').classList.remove('visible'), 4000); }
function status(text, ready = false) { $('#connection span').textContent = text; $('#connection').classList.toggle('connected', ready); }
function send(command) {
  if (!connected || socket?.readyState !== WebSocket.OPEN) { if (!['mouse_move','scroll','mouse_up','release'].includes(command.type)) toast('Reconnect to your PC to use the remote.'); return false; }
  if (socket.bufferedAmount > 4096) { if (!['mouse_move','scroll'].includes(command.type)) { socket.close(); toast('Connection is busy. Reconnecting…'); } return false; }
  socket.send(JSON.stringify(command)); return true;
}
function showPairing(message = '') {
  connected = false; clearTimeout(reconnectTimer); clearInterval(heartbeat); status('Pair your phone');
  $('#pair-error').textContent = message; if (!$('#pair-dialog').open) $('#pair-dialog').showModal();
}
function forgetToken() { token = ''; try { localStorage.removeItem('tvremote-token'); } catch { } }
function connect() {
  clearTimeout(reconnectTimer); clearInterval(heartbeat);
  const previous = socket; socket = null; if (previous && previous.readyState < WebSocket.CLOSING) previous.close();
  if (!token) { showPairing(); return; }
  status(retry ? 'Reconnecting…' : 'Connecting…'); connected = false;
  const current = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws`); socket = current;
  const authTimeout = setTimeout(() => current.close(), 7000);
  current.onopen = () => current.send(JSON.stringify({ type: 'auth', token }));
  current.onmessage = event => {
    if (socket !== current) return;
    let message; try { message = JSON.parse(event.data); } catch { current.close(); return; }
    if (message.type === 'ready') {
      clearTimeout(authTimeout); connected = true; retry = 0; lastPong = Date.now(); status('Connected', true);
      $('#device-name').textContent = message.deviceName; renderApps(message.apps);
      loginOnly = message.loginOnly === true;
      document.querySelectorAll('.volume-row, .quick-row [data-shortcut], nav [data-nav="apps"], #text-mode, .system-grid, #shortcuts, .modifier-row').forEach(element => element.hidden = loginOnly);
      document.querySelectorAll('#special-keys [data-key="F4"], #special-keys [data-key="F5"]').forEach(element => element.hidden = loginOnly);
      if (loginOnly) { modifiers.clear(); renderModifiers(); $('#text-panel').hidden = true; $('#live-panel').hidden = false; $('#live-mode').classList.add('selected'); $('#text-mode').classList.remove('selected'); navigate('remote', { automatic: true }); }
      installSetup = message.install; updateInstallButton();
      if ($('#pair-dialog').open) $('#pair-dialog').close();
      resetLive(); liveFieldRevision = null;
      focusNavigation.reset(); focusNavigation.receive(message.inputFocus);
      heartbeat = setInterval(() => { if (Date.now() - lastPong > 30_000) current.close(); else send({ type: 'ping' }); }, 10_000);
    } else if (message.type === 'input_focus') { if (!document.hidden) focusNavigation.receive(message.state); }
    else if (message.type === 'apps_changed') renderApps(message.apps);
    else if (message.type === 'pong') { lastPong = Date.now(); if (!document.hidden) focusNavigation.receive(message.inputFocus); }
    else if (message.type === 'error') toast(message.message);
  };
  current.onclose = event => {
    clearTimeout(authTimeout); if (socket !== current) return;
    connected = false; clearInterval(heartbeat); gestures.reset();
    if (event.code === 1008) { forgetToken(); showPairing('This phone needs to pair again. Generate a new code on your PC.'); return; }
    if (!token) return;
    status('Reconnecting…');
    reconnectTimer = setTimeout(connect, Math.min(8000, 500 * 2 ** Math.min(retry++, 4)) + Math.random() * 250);
  };
  current.onerror = () => { if (socket === current) status('Reconnecting…'); };
}
$('#pair-dialog').addEventListener('cancel', event => event.preventDefault());
$('#pair-form').addEventListener('submit', async event => {
  event.preventDefault(); $('#pair-submit').disabled = true; $('#pair-error').textContent = '';
  try {
    const response = await fetch('/api/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: $('#pair-code').value, name: $('#phone-name').value.trim() }) });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.message || (response.status === 429 ? 'Too many attempts. Wait a minute, then generate a new code.' : 'Could not pair this phone.'));
    token = result.token;
    try { localStorage.setItem('tvremote-token', token); } catch { toast('Browser storage is unavailable. You will need to pair next time.'); }
    $('#pair-code').value = ''; connect();
  } catch (error) { $('#pair-error').textContent = error.message || 'PC is unreachable.'; }
  finally { $('#pair-submit').disabled = false; }
});
const trackpad = $('#trackpad');
const gestures = new TrackpadGestures(send, { state: (touching, dragging) => { trackpad.classList.toggle('touching', touching); trackpad.classList.toggle('dragging', dragging); } });
trackpad.addEventListener('pointerdown', event => {
  if (event.pointerType === 'mouse' && event.button !== 0) return;
  event.preventDefault(); trackpad.setPointerCapture(event.pointerId);
  gestures.down(event.pointerId, event.clientX, event.clientY); positionRing(event);
});
trackpad.addEventListener('pointermove', event => { gestures.move(event.pointerId, event.clientX, event.clientY); positionRing(event); });
trackpad.addEventListener('pointerup', event => { gestures.up(event.pointerId); focusNavigation.flushPending(); haptic(); });
trackpad.addEventListener('pointercancel', event => { gestures.up(event.pointerId, true); focusNavigation.flushPending(); });
trackpad.addEventListener('lostpointercapture', event => { gestures.up(event.pointerId, true); focusNavigation.flushPending(); });
trackpad.addEventListener('contextmenu', event => event.preventDefault());
function positionRing(event) { const rect = trackpad.getBoundingClientRect(); $('#touch-ring').style.transform = `translate(${event.clientX - rect.left - 19}px,${event.clientY - rect.top - 19}px)`; }
let frame;
function animate() { gestures.flush(); frame = requestAnimationFrame(animate); }
animate();
document.addEventListener('visibilitychange', () => {
  gestures.reset(); send({ type: 'release' });
  if (document.hidden) { cancelAnimationFrame(frame); frame = null; }
  else { if (!frame) animate(); if (!connected) { socket?.close(); connect(); } else send({ type: 'ping' }); }
});
window.addEventListener('pagehide', () => { gestures.reset(); send({ type: 'release' }); socket?.close(); });
window.addEventListener('online', () => { if (!connected) connect(); });
function openNativeKeyboard() {
  const input = $('#text-panel').hidden ? $('#live-input') : $('#text-input');
  input.focus({ preventScroll: true });
  if ('virtualKeyboard' in navigator) {
    try { navigator.virtualKeyboard.show(); } catch { }
  }
}
function navigate(name, { automatic = false } = {}) {
  if (!automatic) focusNavigation.manualNavigation();
  gestures.reset(); $('.view.active')?.classList.remove('active'); $(`#view-${name}`).classList.add('active');
  document.querySelectorAll('nav button').forEach(button => button.classList.toggle('active', button.dataset.nav === (name === 'keyboard' ? 'remote' : name)));
  if (name === 'keyboard') openNativeKeyboard();
  else { if (document.activeElement instanceof HTMLTextAreaElement) document.activeElement.blur(); try { navigator.virtualKeyboard?.hide(); } catch { } }
  window.scrollTo(0, 0); haptic();
}
function consumeModifiers() { const active = [...modifiers.keys()]; for (const [name, state] of modifiers) if (state === 1) modifiers.delete(name); renderModifiers(); return active; }
function key(key, mods) {
  const active = mods ?? consumeModifiers();
  if (!active.length && $('#view-keyboard').classList.contains('active') && !$('#live-panel').hidden && ['BACKSPACE','DELETE','SPACE','ENTER'].includes(key)) {
    liveKey(key); haptic(); return;
  }
  if (send({ type: 'key', key, modifiers: active }) && !['ESCAPE'].includes(key)) resetLive();
  haptic();
}
function shortcut(value) { const parts = value.split('+'); key(parts.pop(), parts); }
function renderModifiers() { document.querySelectorAll('[data-mod]').forEach(button => { const state = modifiers.get(button.dataset.mod); button.classList.toggle('once', state === 1); button.classList.toggle('locked', state === 2); button.setAttribute('aria-pressed', state ? 'true' : 'false'); }); }
document.addEventListener('click', async event => {
  const button = event.target.closest('button'); if (!button || button.disabled) return;
  const data = button.dataset;
  if (data.nav) navigate(data.nav);
  if (data.click) { gestures.flush(); send({ type: 'mouse_click', button: data.click }); haptic(); }
  if (data.key) key(data.key);
  if (data.shortcut) shortcut(data.shortcut);
  if (data.mod) { const state = modifiers.get(data.mod) || 0; if (state === 2) modifiers.delete(data.mod); else modifiers.set(data.mod, state + 1); renderModifiers(); haptic(); }
  if (data.volume) { send({ type: 'volume', action: data.volume }); haptic(); }
  if (data.app) { send({ type: 'app', id: data.app }); haptic(); }
  if (data.power) {
    const action = data.power;
    if (await confirm(`${action[0].toUpperCase() + action.slice(1)} ${$('#device-name').textContent}?`, action === 'sleep' ? 'The remote will reconnect when you wake the PC. It cannot wake a sleeping PC.' : 'This action affects the Windows session.', action)) send({ type: 'power', action, confirm: true });
  }
});
async function confirm(title, description, label = 'Confirm') {
  const dialog = $('#confirm-dialog'); $('#confirm-title').textContent = title; $('#confirm-description').textContent = description; $('#confirm-button').textContent = label[0].toUpperCase() + label.slice(1); dialog.returnValue = ''; dialog.showModal();
  return new Promise(resolve => dialog.addEventListener('close', () => resolve(dialog.returnValue === 'confirm'), { once: true }));
}
const specialKeys = [['ESCAPE','Esc'],['TAB','Tab'],['BACKSPACE','⌫'],['DELETE','Delete'],['HOME','Home'],['UP','↑'],['END','End'],['PAGEUP','Pg Up'],['LEFT','←'],['DOWN','↓'],['RIGHT','→'],['PAGEDOWN','Pg Dn'],['ENTER','Enter'],['SPACE','Space']];
for (const [name, label] of specialKeys) { const button = document.createElement('button'); button.dataset.key = name; button.textContent = label; $('#special-keys').append(button); }
for (const [value, label] of [['CTRL+C','Copy'],['CTRL+V','Paste'],['CTRL+X','Cut'],['CTRL+A','Select all'],['CTRL+Z','Undo'],['CTRL+Y','Redo'],['ALT+TAB','Switch app'],['ALT+F4','Close app'],['WIN+D','Desktop'],['WIN+E','Files'],['WIN+R','Run'],['WIN+TAB','Task view'],['CTRL+SHIFT+ESCAPE','Task manager']]) {
  const button = document.createElement('button'); button.dataset.shortcut = value; button.textContent = label; $('#shortcuts').append(button);
}
const live = $('#live-input');
const sentinel = '\u200B';
const liveTyping = new LiveTyping(send);
let liveFieldRevision = null;
function resetLive() { composing = false; liveTyping.reset(); live.value = sentinel; live.setSelectionRange(1, 1); }
function localLive() {
  const offset = live.value.startsWith(sentinel) ? 1 : 0;
  return { value: live.value.slice(offset), start: Math.max(0, live.selectionStart - offset), end: Math.max(0, live.selectionEnd - offset) };
}
function showLive(value, start = value.length, end = start) {
  live.value = value || sentinel; live.setSelectionRange(value ? start : 1, value ? end : 1);
}
function syncLive() {
  const { value, start, end } = localLive();
  if (liveTyping.update(value, end)) showLive(value, start, end);
  else { showLive(liveTyping.value); if (liveTyping.error) toast(liveTyping.error); }
}
function liveKey(name) {
  if (composing) return;
  const local = localLive();
  let start = local.start, end = local.end, insert = name === 'SPACE' ? ' ' : name === 'ENTER' ? '\n' : '';
  if (name === 'BACKSPACE' && start === end && start > 0) start -= liveTyping.parts(local.value.slice(0, start)).at(-1).length;
  if (name === 'DELETE' && start === end && end < local.value.length) end += liveTyping.parts(local.value.slice(end))[0].length;
  if (start === end && !insert) { send({ type: 'key', key: name, modifiers: [] }); return; }
  const value = local.value.slice(0, start) + insert + local.value.slice(end);
  if (liveTyping.update(value, start + insert.length)) showLive(value, start + insert.length);
}
live.addEventListener('focus', () => { if (!live.value) showLive(liveTyping.value); });
live.addEventListener('compositionstart', () => composing = true);
live.addEventListener('compositionend', () => { composing = false; syncLive(); });
live.addEventListener('beforeinput', event => {
  if (composing || event.isComposing) return;
  if (modifiers.size && event.inputType.startsWith('insert')) {
    event.preventDefault();
    if (/^[a-z0-9]$/i.test(event.data || '')) key(event.data.toUpperCase());
    else { toast('Use the special keys or shortcuts with modifiers.'); consumeModifiers(); }
    return;
  }
  const local = localLive();
  if (event.inputType.startsWith('delete') && !local.value) {
    event.preventDefault(); send({ type: 'key', key: event.inputType.includes('Forward') ? 'DELETE' : 'BACKSPACE', modifiers: [] });
  }
  if (event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph') { event.preventDefault(); liveKey('ENTER'); }
});
live.addEventListener('input', event => { if (!composing && !event.isComposing) syncLive(); });
live.addEventListener('keydown', event => { if (event.key === 'Enter' && !composing && !event.isComposing) { event.preventDefault(); liveKey('ENTER'); } });
$('#live-mode').onclick = () => { $('#text-panel').hidden = true; $('#live-panel').hidden = false; $('#live-mode').classList.add('selected'); $('#text-mode').classList.remove('selected'); live.focus(); };
$('#text-mode').onclick = () => { $('#text-panel').hidden = false; $('#live-panel').hidden = true; $('#text-mode').classList.add('selected'); $('#live-mode').classList.remove('selected'); $('#text-input').focus(); };
$('#send-text').onclick = () => { const value = $('#text-input').value; if (value && send({ type: 'text', value })) { $('#text-input').value = ''; toast('Sent to your PC.'); haptic(); } };
const icons = { gamepad:'▣',film:'▻',music:'♫',play:'▶',globe:'◎',folder:'▤' };
let appIconRequests, appIconUrls = [];
function clearAppIcons() {
  appIconRequests?.abort(); appIconRequests = null;
  for (const url of appIconUrls) URL.revokeObjectURL(url);
  appIconUrls = [];
}
async function loadProgramIcon(app, icon, signal) {
  try {
    const response = await fetch(`/api/app-icon/${encodeURIComponent(app.id)}`, {
      method: 'POST', headers: { Authorization: `Bearer ${token}` }, signal, cache: 'no-store'
    });
    if (!response.ok || !response.headers.get('content-type')?.startsWith('image/png')) return;
    const blob = await response.blob(); if (signal.aborted || !icon.isConnected) return;
    const url = URL.createObjectURL(blob); appIconUrls.push(url);
    const image = document.createElement('img'); image.src = url; image.alt = ''; image.width = image.height = 32;
    image.addEventListener('error', () => { if (icon.isConnected) icon.textContent = icons[app.icon] || '▦'; }, { once: true });
    icon.replaceChildren(image);
  } catch { /* Keep the fallback icon if this program disappears or the PC reconnects. */ }
}
function renderApps(apps) {
  clearAppIcons(); appIconRequests = new AbortController();
  $('#apps-list').replaceChildren();
  for (const app of apps) {
    const button = document.createElement('button'); button.dataset.app = app.id; button.disabled = !app.available;
    const icon = document.createElement('span'); icon.className = 'app-icon'; icon.textContent = icons[app.icon] || '▦';
    const label = document.createElement('span'); label.textContent = app.name; button.append(icon, label);
    if (!app.available) { const caption = document.createElement('small'); caption.textContent = 'Not installed'; button.append(caption); }
    $('#apps-list').append(button);
    if (app.hasProgramIcon) loadProgramIcon(app, icon, appIconRequests.signal);
  }
  $('#playnite-panel').hidden = !apps.some(app => app.id === 'playnite' && app.available);
}
$('#close-playnite').onclick = async () => { if (await confirm('Close Playnite?', 'Playnite will receive a normal close request.', 'Close')) send({ type: 'playnite_close' }); };
$('#forget-device').onclick = async () => { if (await confirm('Forget this phone?', 'To revoke access permanently, also remove this device in the PC tray app.', 'Forget')) { clearAppIcons(); forgetToken(); socket?.close(); showPairing(); } };
function sensitivity(value) { if (!['0.5','0.75','1','1.25','1.5','2'].includes(value)) value = '1'; gestures.sensitivity = Number(value); $('#sensitivity').value = value; $('#sensitivity-button').textContent = `${Number(value).toFixed(2).replace(/0$/, '')}× sensitivity`; try { localStorage.setItem('tvremote-sensitivity', value); } catch { } }
$('#sensitivity').onchange = event => sensitivity(event.target.value);
try { sensitivity(localStorage.getItem('tvremote-sensitivity') || '1'); } catch { sensitivity('1'); }
$('#sensitivity-button').onclick = () => { navigate('system'); $('#sensitivity').focus(); };
$('#connection').onclick = () => { if (!connected && token) { socket?.close(); connect(); } else toast(connected ? 'Connected directly to your Windows PC.' : 'Open TV Remote on your PC to see a pairing code.'); };
$('#pair-security').textContent = location.protocol === 'https:' ? 'Encrypted local connection. Your pairing stays on this phone.' : 'HTTP is unencrypted. Enable local HTTPS in the PC tray app for protected input and full PWA installation.';
let installPrompt = null, installSetup = null;
function installedOnAndroid() { return window.matchMedia('(display-mode: standalone)').matches; }
function updateInstallButton() {
  $('#install-android').hidden = installedOnAndroid();
  $('#install-hint').textContent = installedOnAndroid() ? 'Installed Android app.' : 'Install TV Remote on Android for its own icon and full-screen window.';
}
window.addEventListener('beforeinstallprompt', event => { event.preventDefault(); installPrompt = event; updateInstallButton(); });
window.addEventListener('appinstalled', () => { installPrompt = null; updateInstallButton(); toast('TV Remote installed. Open it from your Android home screen.'); });
$('#install-android').onclick = async () => {
  if (installPrompt && isSecureContext && location.protocol === 'https:') {
    const prompt = installPrompt; installPrompt = null;
    try { await prompt.prompt(); await prompt.userChoice; } catch { }
    updateInstallButton(); return;
  }
  const secure = isSecureContext && location.protocol === 'https:';
  $('#install-http-steps').hidden = secure;
  $('#install-secure-steps').hidden = !secure;
  const setup = installSetup;
  const url = new URL(location.href); url.protocol = 'https:'; url.port = String(setup?.httpsPort || 8124); url.pathname = '/'; url.search = url.hash = '';
  $('#install-open-https').href = url.href;
  $('#install-open-https').hidden = secure || !setup?.httpsEnabled;
  $('#install-certificate').hidden = secure || !setup?.certificateFingerprint;
  $('#install-fingerprint').textContent = setup?.certificateFingerprint ? `Certificate SHA-256: ${setup.certificateFingerprint}` : '';
  $('#install-fingerprint').hidden = secure || !setup?.certificateFingerprint;
  $('#install-dialog').showModal();
};
updateInstallButton();
if ('serviceWorker' in navigator && isSecureContext) navigator.serviceWorker.register('/service-worker.js').catch(() => toast('Home-screen cache could not be enabled. The remote still works online.'));
connect();
