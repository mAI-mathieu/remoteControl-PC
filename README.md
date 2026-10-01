# TV Remote

A Windows 11 tray app that turns a phone on the same LAN into a trackpad, keyboard, volume control and application launcher. The PC displays on the TV; no screen streaming, account or cloud service is involved. The default server port is **8123**.

## Start using it

1. Build from source below, or extract the complete `artifacts/TV-Remote-win-x64.zip` release. Run `TvRemote.Server.exe` alongside its `wwwroot` folder. The self-contained release does not require .NET to be installed.
2. The host window displays the PC's actual LAN URL, a URL-only QR code, and a temporary six-digit code. Scan the QR or open `http://192.168.x.x:8123` on your phone. `http://tvpc.local:8123` also works when multicast DNS is available. Port 8123 is required; a URL without a port would require a separate listener on port 80.
3. Give the phone a name and enter the code. Codes expire after five minutes, allow at most ten attempts, and are consumed on successful pairing. Generate another code to pair another phone (up to 32).
4. Open a normal Windows application and try the remote. One finger moves, tap clicks, two taps double-click, a stationary 450 ms hold starts dragging, two fingers scroll in both directions, and a two-finger tap right-clicks. Movement is coalesced on animation frames; fractional movement is retained. Select sensitivity from 0.5× through 2× in System.
5. Selecting a supported text field on the PC automatically opens Keyboard. The manual Keyboard button also opens it. Live typing keeps the text typed from this phone visible, including backspaces, selections and Android corrections; it never reads existing PC field contents. The local buffer resets when the selected PC field changes or after shortcuts. **Send a sentence** composes up to 10,000 characters before inserting them. Expand **Keys & shortcuts** for modifiers and special keys. Tap CTRL/ALT/SHIFT/WIN once for the next special key or ASCII letter; tap again to lock; tap a third time to clear. Modifiers apply to key commands, not to composed Unicode sentences.
6. Choose **Android app install…** on the PC for the encrypted address and certificate fingerprint. Set up trusted local HTTPS below, then choose **System → Install on Android** on the phone, or Chrome's **Install app / Add to Home screen → Install**. The installed app has its own 🖲️ icon and standalone window. Pairing on the HTTP and HTTPS origins is separate: pair again after switching to HTTPS. **Create shortcut** is also available in Chrome but may open a browser tab.
7. Enable **Start with Windows (after sign-in)** in the host window after placing the app in a permanent folder. This uses the current user's Run registry key at sign-in. The optional **Windows sign-in setup…** service below handles input before sign-in. Use `install-user.ps1` from an extracted release for a simple per-user install and Start Menu shortcut.

Closing the host window hides it in the tray. Double-click the tray icon to reopen it; the tray menu can restart the server, generate a code or quit. Devices show connection status and last-connected time. Revocation immediately disconnects all sessions for that device and invalidates its token. Forgetting a phone in the web UI clears its local token; revoke it on the PC to invalidate any copied token.

## Local HTTPS and PWA installation

HTTP works for LAN input, but its code, token and keyboard traffic are **unencrypted**. Use it only for initial setup on a trusted network. A trusted HTTPS origin is necessary for service workers and reliable PWA installation; an HTTP LAN address cannot provide those guarantees ([browser requirement](https://developer.mozilla.org/en-US/docs/Web/API/Service_Worker_API/Using_Service_Workers)).

1. In the host window, choose **Android app install…** (or **Enable local HTTPS / create certificate**). The app creates a private local root and server certificate, enables port **8124**, and protects the server private key with Windows DPAPI. This HTTPS action never installs trust or changes Firewall automatically. The root signing private key is discarded; only the public root certificate is exported.
2. Find `%APPDATA%\TvRemote\TV-Remote-Root.cer`. Transfer this public certificate to your phone yourself, or download it from `http://<PC-IP>:8123/TV-Remote-Root.cer`. Compare its SHA-256 fingerprint with the file on the PC using `Get-FileHash "$env:APPDATA\TvRemote\TV-Remote-Root.cer" -Algorithm SHA256` before trusting it. Transferring directly from the PC avoids trusting a certificate delivered over unencrypted HTTP.
3. On Android: install it as a CA certificate through the security/certificate settings (labels vary by manufacturer). Only trust the root you just generated on your own PC. Remove it when retiring the remote.
4. Open `https://<PC-IP>:8124` (or `https://tvpc.local:8124`), pair, and install the PWA. If the browser still reports an invalid certificate, resolve trust before pairing. Do not rely on clicking through a certificate warning to make a PWA work.

The certificate includes the private LAN IP addresses present when generated, `tvpc.local`, `localhost`, and loopback. Use a DHCP reservation for the PC. The server certificate is valid for one year; the root is valid for five. To regenerate for a changed IP or expiry, quit the app, remove `server.dpapi` and `TV-Remote-Root.cer` from its configuration folder, restart, and enable HTTPS again. This creates a new root which must be trusted again on each phone. No private key or token is embedded in a QR code or URL.

PWA shell caching is network-first. API responses, pairing, WebSockets and certificates are never cached by the service worker. Offline, the interface remains visible and shows reconnection status; it cannot control a sleeping, disconnected or switched-off PC. The app does not implement Wake-on-LAN. Android haptics are used where the browser supports vibration.

## Optional Windows sign-in service

Pair the Android phone on the trusted **HTTPS** address first. Choose **Windows sign-in setup…** on the PC and accept its Windows administrator prompt. This copies the host to a protected `%ProgramFiles%\TV Remote Sign-in` installation, registers the automatic LocalSystem service `TvRemoteSignIn`, and adds an inbound HTTPS firewall rule limited to Private networks and LocalSubnet. Keep **Start with Windows (after sign-in)** enabled for the normal remote afterward. Setup is optional; simply ticking the startup checkbox does not install a privileged service.

Before a console user signs in, the service serves the same HTTPS origin and accepts the existing paired phones. A separate SYSTEM input helper runs in the console session, communicates over a SYSTEM-only named pipe, checks the active desktop is `Winlogon`, and injects only bounded typing/navigation/trackpad input. It creates no windows, captures no screen and reads no credential fields. The phone masks its typing buffer throughout sign-in; select the Windows PIN/password field with the trackpad and type. App launches, Windows/modifier shortcuts, volume, power and new pairing are blocked in this mode. After sign-in, the service closes its listener and helper; the ordinary user app takes over and the phone reconnects.

Settings are an owner-access-controlled machine-DPAPI snapshot under `%ProgramData%\TvRemoteSignIn\settings`. Normal saves refresh it, so pairing changes and revocations are picked up by the service within its one-second supervision interval. The service never overwrites this snapshot. Logs go to the administrator-protected parent folder and contain error categories/native error numbers, never typed credentials. Use **Remove Windows sign-in service…** to stop/delete the service and its firewall rule; installation files are retained, and the ordinary remote remains usable. Rerun setup after updating the app or moving to another HTTPS port so the service binary/firewall rule are updated too.

This mode has automated TLS/authentication/configuration/command-restriction tests, but **boot and actual credential entry have not been verified on this PC**. Test it when ready to restart, with a local keyboard available. It supports the console sign-in screen before a user session, not unlocking an already signed-in session, UAC prompts, Remote Desktop sessions or BitLocker/preboot screens. Ctrl+Alt+Delete is unsupported; a policy requiring that sequence needs a local keyboard. It does not store your Windows password, enable automatic login or change UAC/secure-desktop policies. Windows services cannot directly drive a user's desktop; the separate helper is required ([Microsoft service guidance](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)).

## Build, run and publish

Windows and the **.NET 10 SDK** are required for source development. No JavaScript bundler is needed. QRCoder is the sole external library and is used entirely offline at runtime.

```powershell
dotnet restore TvRemote.sln
dotnet build TvRemote.sln
dotnet run --project TvRemote.Server
dotnet run --project TvRemote.Tests
node --test TvRemote.Tests/*.test.js
```

Tests use an executable harness with failing exit codes rather than a test-runner package; run `dotnet run --project TvRemote.Tests`, not `dotnet test`. All native controls are replaced by fakes in integration tests, which bind loopback only and use disposable configuration storage.

```powershell
# Builds a self-contained release and ZIP. Keep wwwroot with the executable.
.\scripts\publish.ps1
# Or publish directly:
dotnet publish TvRemote.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
# ARM64 is also supported by the release script:
.\scripts\publish.ps1 -Runtime win-arm64
```

The executable contains the managed assemblies and runtime. Static assets remain in `wwwroot`, so distribute the complete folder/ZIP. Trimming is intentionally disabled for WinForms and reflection-based JSON. Install/update while the app is stopped. Autostart follows the installed executable's path; re-enable it after moving the installation.

Development: use `dotnet watch --project TvRemote.Server run`; edit HTML/CSS/JS and reload the phone after the watch process rebuilds. Files are served from the build output, so editing source requires a rebuild/copy, not just refreshing an unchanged output directory. Browser caching is disabled and the service worker uses network-first requests. Clear browser site data to reset pairing during development. A safe simulated UI preview is available via `dotnet run --project TvRemote.Tests -- --preview`; it listens at `http://127.0.0.1:18765`, prints a disposable pairing code, and stops on Enter. Preview never injects input or launches apps.

## Configuration and shortcuts

Choose **Manage apps…** in the PC window or tray menu. **Add program…** searches installed programs from the Start menu and Windows App Paths; filter by name, or choose **Browse files…** for a portable program. Local `.exe` files and ordinary `.lnk` shortcuts targeting them are supported. Shortcuts with arguments, scripts, Microsoft Store launch commands or nonlocal targets need a supported local executable instead. Detection reads metadata and never launches a program.

Use **Add website…** for a web address (HTTPS is added when omitted). Name the shortcut; programs automatically use their actual Windows icon in the PC manager, program picker and phone Apps page. Website shortcuts use a selectable fallback symbol. Select a row to edit, remove, or move it up/down to set its phone order; missing program files are marked in the list. **Save changes** persists the list and updates connected phones immediately. **Cancel** discards the pending changes. Existing phone pairing, ports and other settings are preserved. Removing a shortcut does not uninstall its program.

Program icons are extracted locally from the configured executable and cached in memory. The phone loads their PNG images through an authenticated same-origin request; executable paths and bearer tokens are not included in image URLs. Missing icons keep the fallback symbol. No online icon service is used.

Configuration is created at `%APPDATA%\TvRemote\config.json`. Edit it through the host's **Open configuration** button, save and use **Restart server / reload configuration**. Invalid configuration produces a visible error and is preserved. Quit the host before replacing paired-device data or editing the file extensively, since successful connections persist last-connected timestamps.

```json
{
  "serverPort": 8123,
  "httpsPort": 8124,
  "enableHttps": false,
  "startWithWindows": false,
  "deviceName": "Living Room PC",
  "mouseSensitivity": 1.0,
  "pairedDevices": [],
  "appShortcuts": [
    { "id": "playnite", "name": "Playnite", "icon": "gamepad", "type": "executable", "path": "C:\\Program Files\\Playnite\\Playnite.FullscreenApp.exe" },
    { "id": "youtube", "name": "YouTube", "icon": "play", "type": "url", "url": "https://youtube.com" }
  ]
}
```

Do not replace `pairedDevices` with an empty example when editing a live installation. Tokens are generated from 32 cryptographically random bytes. Only a SHA-256 token hash, itself protected by DPAPI for the Windows user, is stored in config. The browser stores its bearer token locally; clearing site data requires pairing again. Other Windows users cannot decrypt this user's paired-device hashes.

Shortcut IDs must be unique, with letters, digits, dashes or underscores. Executables must be local absolute `.exe` paths; UNC paths, scripts and shell commands are rejected. URLs must be HTTP(S). There are no phone-supplied paths or arguments. Missing executables are disabled in the UI. Playnite is detected in the configured location, `%LOCALAPPDATA%\Playnite`, or `Program Files\Playnite`; the directional controls and normal-close button appear only when it is available. A portable install needs an explicit configured path. The remote closes only a process whose full path matches the resolved Playnite executable. Default Spotify/Plex/Netflix shortcuts open web versions; set executable paths for native applications.

## Networking, Firewall and discovery

Kestrel binds explicit private IPv4 / IPv6 ULA addresses plus IPv4 loopback; it never uses `0.0.0.0`, a public interface or automatic router port forwarding. Incoming clients must have local/private addresses. Router guest isolation, separate VLANs, VPNs and corporate Firewall policies may prevent connections. The tray prominently displays actual LAN addresses. Network-address changes trigger a debounced server restart so listeners rebind; already paired phones reconnect automatically if the URL remains reachable.

An IPv4 multicast DNS responder tries `tvpc.local` on UDP 5353 and advertises an HTTP service. Existing-name conflicts disable the alias. Discovery is best effort and may be blocked by Bonjour ownership, multicast filtering, multiple network adapters or Windows Firewall. Use the numeric LAN address/QR as fallback. IPv6-only networks use the displayed bracketed IP URL. Name conflicts and lack of discovery do not stop the web server.

Windows Firewall may ask for access. Allow **Private networks**, keep your Ethernet connection's network profile Private, and keep router port forwarding disabled. If no prompt appears, create an inbound rule scoped to the app, TCP 8123/8124, Private profile and LocalSubnet. Optionally, from an elevated PowerShell explicitly run:

```powershell
.\scripts\firewall.ps1 -Executable 'C:\Path\To\Published\TvRemote.Server.exe'
```

This script creates Private/LocalSubnet web and discovery rules only. Normal use never requires admin rights. With `dotnet run`, the hosting process is dotnet.exe; testing from a published executable makes program-scoped Firewall rules clearer. Custom ports require matching Firewall arguments/rules. HTTP listeners exist even when HTTPS is enabled to allow initial certificate download; authenticated input should use the HTTPS origin once trusted.

## Security and protocol

No input is processed before authentication. Each WebSocket must have an exact same-origin Origin header and send `{ "type": "auth", "token": "..." }` within five seconds; tokens never go in query strings. Host headers are checked against bound addresses, localhost and tvpc.local to defend against DNS rebinding. Pairing accepts JSON POSTs from the same origin with a global ten-request/minute limit. WebSocket sessions are capped at 16; the app rechecks token validity for every command. Malformed frames, binary frames, duplicate/unknown fields and unknown command types are rejected. Messages are capped at 64 KiB, text at 10,000 UTF-16 units, and sessions at 160 commands/second with further text/launch limits. Payloads and tokens are never logged.

Commands include `mouse_move`, `mouse_click`, `mouse_down`, `mouse_up`, `scroll` (`delta` and optional `horizontal`), `key` (`key` plus optional `modifiers`), `text`, `text_edit` (bounded caret moves, backspaces and inserted text), `volume`, `app` (`id` only), `playnite_close`, `power`, `release` and `ping`. Restart/shutdown require `confirm: true` and a UI confirmation. The protected Ctrl+Alt+Delete sequence is rejected. Modifiers are pressed/released for each key command. Drag ownership prevents competing phones from releasing or moving another phone's drag; disconnect/revocation/backgrounding releases it, with a 15-second inactivity fail-safe. Input is serialized so modifier sequences cannot interleave across phones.

SendInput follows Windows integrity isolation: a normal user process cannot control elevated apps, UAC prompts, the lock screen or secure desktops ([Microsoft documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)). Unicode and emoji rendering depend on the focused application. Volume controls use Windows system volume keys; scrolling direction is natural two-finger scrolling. Lock/sleep require an interactive desktop; Windows power policy may prevent sleep/restart. Sleeping the PC disconnects the remote until it is manually awakened.

Logs are `%APPDATA%\TvRemote\host.log`, rotated at approximately 2 MB with one backup. They include startup, connection IDs, pairing outcomes, shortcut IDs and error categories. Framework request logging is disabled to avoid incidental sensitive data. The host has a five-second UI status refresh; network I/O is event-driven, with ten-second phone heartbeats while connected. Idle CPU target and real LAN latency still require measurement on your hardware.

## Manual acceptance tests

Use a Windows 11 PC on Ethernet and a physical Android phone running Chrome on the same router's Wi-Fi. Desktop viewport checks cannot verify mobile IME, certificate trust, gesture latency or device power behavior.

- First run: confirm Private Firewall access, LAN URL/QR, expiring code, new-code generation, wrong-code rejection and successful pairing with a meaningful phone name. Verify a second phone needs a fresh code.
- Trackpad: test slow precision, fast motion, tap, double tap in Explorer, stationary hold and drag, two-finger vertical/horizontal scrolling and right-click. Rotate portrait/landscape. Lift one finger, cancel a touch or background the phone mid-drag; Windows must not retain a stuck button. Verify sensitivity values.
- Keyboard: tap Keyboard and verify the native software keyboard opens immediately. In Notepad type `Red Dead Redemption 2`, `Příliš žluťoučký kůň`, punctuation and an emoji, then Enter, Backspace and special keys. Verify text stays visible on the phone, including Gboard composition/autocorrection, deleting a word, replacing a selection and refocusing the phone field. Click a different PC field and verify the local buffer resets. Compose/send a sentence. Check one-shot/locked modifiers and shortcuts. No Ctrl+Alt+Delete control is offered.
- Volume/apps: use volume down, mute and volume up on the main Remote view. Launch installed shortcuts and confirm missing ones are disabled. Configure the correct Playnite Fullscreen executable, launch it, use the D-pad/Enter/Escape, and close it normally.
- Security/reliability: revoke a connected phone from the tray; it must lose input immediately and request pairing. Disable/re-enable Wi-Fi and confirm exponential reconnect within seconds without losing pairing. Reload the browser and restart the host. Remove the PC network temporarily, then restore it. Test a port conflict and invalid config; the host must show meaningful errors.
- HTTPS/PWA: independently verify/install/trust the local root, pair on the HTTPS origin, use System's Install on Android or Chrome's Install app menu, verify the 🖲️ icon and standalone launch, close/reopen it and reboot Windows. After sign-in with autostart enabled, the PWA should connect within a few seconds. Test notch safe areas and landscape. Confirm that an offline PWA shows reconnecting and resumes after the host returns.
- Optional sign-in service: install through Windows sign-in setup, enable ordinary startup, and restart deliberately with a local keyboard available. Before signing in, open the same HTTPS app on an already paired Android phone. Select the Windows PIN/password field and enter it; verify masking, typing/backspace and the handover to the ordinary remote. Verify app launches, power and modifier shortcuts are unavailable before sign-in. Revoke the test phone, then verify it cannot connect at the next boot. Test removal separately. Boot/secure-desktop acceptance has not been automated.
- Power, deliberately at the end: save your work. Test lock; unlock locally. Test Sleep and wake locally. Verify restart/shutdown dialogs cancel without action. Execute restart/shutdown only when ready, then verify autostart and reconnection after sign-in. These tests are intentionally not executed by the automated suite.
- Performance: measure idle CPU in Task Manager over several minutes, then test pointer feel from the sofa. Target <1% idle CPU and immediate LAN interaction; these are goals rather than measurements claimed by this build.

Automated checks cover schema parsing/rejection, configuration, token generation/DPAPI/expiry/revocation, executable validation, Unicode/key injection through fakes, drag ownership, live loopback WebSocket authentication and origin/Host defenses. JavaScript tests cover gesture coalescing, taps, holds, cancellation and bounded deltas. The browser preview checks the actual rendered interface and pairing flow. Physical-phone/TV/power acceptance remains a manual test.

## Automatic Android keyboard

The main remote now contains the trackpad, left/right click and volume down/mute/up. Navigation contains Remote, Apps and System. Keyboard is a contextual view, with a manual Keyboard button on the main remote; additional keys and shortcuts are collapsed by default.

Windows UI Automation reports when a focused control is editable. The server sends only editable/password flags and a monotonically increasing revision to authenticated phones; it never reads field names, values, passwords or document contents. Monitoring is event-driven on a separate MTA thread. Standard text fields, browser inputs/contenteditable documents and compatible editors switch Remote to Keyboard automatically. When focus leaves the editable control, an automatically opened keyboard returns to Remote. A manual return to Remote remains respected until a new field gains focus; manually opened Keyboard, Apps and System are not interrupted.

Chrome on Android is the target browser. The page focuses the typing field and requests the VirtualKeyboard API where available. That API requires a secure context and previous user interaction ([Chrome documentation](https://developer.chrome.com/docs/web-platform/virtual-keyboard), [activation requirement](https://www.w3.org/TR/virtual-keyboard/)). HTTP falls back to normal focus; if the phone does not raise its software keyboard, tap the typing field. Use local HTTPS for the fullest Android PWA experience. The viewport resizes with Android's keyboard to keep controls reachable.

Custom game UIs, inaccessible/elevated applications and some terminal controls do not expose editable state; use the manual Keyboard button for them. The phone follows PC focus, not merely clicks: clicking a button must not open the keyboard, while focusing another editable field must. When the application reports a password field, the live typing display is masked. Some accessibility providers omit that flag; do not compose passwords in the visible sentence editor.

Manual Android check: in a standard editor/browser on the PC, click an editable field using the phone trackpad. Confirm the keyboard view appears without opening a tab, Android's software keyboard appears (or opens when the field is tapped), and text reaches the PC. Click elsewhere and verify automatic return, then test manual return, Tab between fields, read-only fields and reconnect while a text field is selected. To exercise real Windows focus detection without sending PC input, run `dotnet run --project TvRemote.Tests -- --preview --native-focus`; pointer/key/launch/power commands remain fakes.
