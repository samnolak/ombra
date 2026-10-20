# Platform notes

Behaviour that differs by OS and is set on purpose. Keep each entry: what, why, how it is checked.

## macOS: full-screen ownership and restoring the main window

The green button belongs to Ombra's NSWindow, not the embedded Nightly window. View → Enter/Exit Full Screen
and ⌃⌘F therefore toggle the same host window. Avalonia disables AppKit's automatic full-screen menu item,
so Ombra supplies it explicitly. `NightlyShortcutMonitor` leaves ⌃⌘F to that menu; forwarding it to the hidden
browser window cannot exit Ombra's full-screen Space. Plain ⌘F still belongs to Nightly's Find command.
`WindowSafetyTests` check the menu command/title/gesture, and `NightlyShortcutMonitorTests` check routing.

Nightly also refuses fullscreen in an embedded DOM window before changing its fullscreen state or freezing
the pres shell. Its Cocoa widget rejects native fullscreen and size-mode transitions, including a session's
saved maximized state. Embedded startup normalizes the profile's persisted `sizemode` and the attached
browser chrome attribute; only Ombra supplies the native frame. Standalone Nightly retains its usual behavior.
The runtime smoke must include a profile saved with `sizemode=fullscreen` and one with `sizemode=maximized`:
open Browser, run `restore green-key` and `restore green-menu`, then restart. The host must enter/exit
fullscreen, Nightly must remain embedded without its own Space, and Browser must fill its pane after restart.

AppKit application activation and window activation are different: a minimized window cannot receive
`Window.Activated`. Avalonia forwards Dock `applicationShouldHandleReopen` through `IActivatableLifetime`;
the application handles `Reopen` by showing, deminiaturizing and activating its existing main window.
Returning from the background also restores a minimized main window. A visible maximized or full-screen
window is not normalized by this path. Browser child windows and Dock attention do not own this lifecycle.
Headless `WindowSafetyTests` cover repeated activation from minimized state, preserving maximized/full-screen
state, and reopening a hidden window without treating every background activation as a reopen request.

The native Nightly acceptance driver has `restore dock|activate|hide|zoom|doubleclick|fullscreen|close`.
Each runs ten cycles and records NSWindow `isMiniaturized`, `isVisible`, frame, Avalonia state and presence
in the on-screen `CGWindowList`. `dock` invokes the actual application delegate reopen callback; `activate`
switches to Finder then invokes `applicationDidBecomeActive:` without calling `makeKeyAndOrderFront` to repair it.
These exercise AppKit callbacks, not physical clicks on the Dock or the system application switcher.
The `green`, `green-key`, `green-menu`, `green-minimize` and `green-activate` modes enter through
the real green button's action, then exercise the named exit/activation path. Logs include the native
full-screen style bit and `isZoomed` as well as Avalonia state, so a stale managed state cannot hide a stuck Space.
Run on Chats and Browser; prefix a mode with `popup-` to open a Nightly context menu before each cycle,
or `starting-` to restart the embedded runtime before each cycle. The driver requires negative X so tests
stay on the left display. The Browser `close` case sends ⌘W to Nightly and checks Ombra survives.
On Chats, ⌘W intentionally follows the quit sequence: the process exits, so a later Dock launch is a new
application, not `applicationShouldHandleReopen` on a hidden retained window.

## Window placement across runs (and the window found at 1100×760)

**What.** The close sequence saves the normal frame, whether the window was maximized and the panels
(`WindowLayoutOptions`, `MainWindow.UiState.cs`). At the next start the saved frame is applied when the window starts
to show (`Window.WindowOpenedEvent`, raised before Avalonia sizes, places and shows it), on the display the frame
overlaps most, fitted to its working area; the primary display takes a frame whose display is gone. The normal frame
is taken once a move or resize has held for 600 ms in the normal state and AppKit does not call the window zoomed,
full screen or minimized; full screen keeps the maximized state under it.

**Why.** Restored after the show (as before), the window first appeared at the XAML default (1100×760 in the middle of
the main display) and was then moved, and a saved maximized window was zoomed before its saved size reached AppKit:
AppKit's zoom keeps the frame it zoomed from, so Zoom/the green button with ⌥/a title-bar double-click later brought the
window back to 1100×760 at the saved corner. AppKit also resizes a window to the zoomed or full-screen frame (and
through the zoom animation) before Avalonia reports the new state, so the "normal" frame saved at quit was the
maximized or full-screen one (a saved layout of `2560×1288 maximized` on a 2560×1440 display). A frame whose top-left
corner was just past a display's edge (a window dragged beyond the left edge of a display at negative X) went to the
primary display.

**Diagnostics.** client.log, source `window`: the saved layout, the displays and the frame chosen at launch, the frame
shown, every state change and display change, the frame saved at quit, and each settled burst of resizes that neither
the user's drag nor Ombra's layout asked for (zoom, full screen, a display going away …) or of moves onto another display:
from where to where, AppKit's zoomed/full-screen/minimized state, the `NSEvent` being handled, and for the first ten
per run the native and managed stacks. A window found at 1100×760 again is explained by these lines.

**Checks.** `MicroUxStateTests`: the display a frame overlaps most, the frame the window is shown and maximized from,
the normal frame not taken from frames passed on the way to maximized, full screen keeping the maximized state (the
two window tests fail on the old order). AppKit's side, with NSWindows never put on screen (osascript): zoomed from
1100×760 at a corner, then given 1320×860, zoom twice brings back 1100×760 at that corner; zoomed from 1320×860, it
comes back to 1320×860.

## macOS: the app icon (no grey plate on macOS 26)

**What.** `Ombra.app` carries two icons (`tools/package/package.sh`, `packaging/Info.plist`): `Assets.car` with
the Icon Composer icon `Ombra` (`CFBundleIconName`) and `ombra.icns` (`CFBundleIconFile`). Both come from
`tools/icon/make_icon.py`: the swirl on a full-bleed black square (`packaging/macos/Ombra.icon`: black fill, the
swirl as one layer without glass), compiled by Xcode 26's `actool`; the icns is that square inside Apple's app icon
shape (continuous corners, radius 185.4 on the 824/1024 grid). The catalog also holds flat renditions for macOS 14–15.

**Why.** macOS 26 draws an icon whose outline is not its own app icon shape shrunk on a light-grey rounded plate
(“icon jail”). The first swirl icon kept the presentation sheet's tile — a rounded rectangle of its own, wider than
tall, with a rim and glow — and was jailed. Checked with `NSWorkspace.icon(forFile:)` on test bundles: that icns was
jailed whether the launcher names SDK 26 or 15 (the `vtool` SDK marking is unrelated and stays for the window
corners); a full-bleed square, the Big Sur shape and the macOS 26 shape are all re-masked by the system without a
plate; the catalog icon adds the system's dark, tinted and clear renderings. Windows and Linux do not mask app
icons, so `icon.ico` and the hicolor PNGs keep the shaped tile (a full-bleed square would show as a hard black
square there).

**Checked.** A package from `package.sh osx-arm64` rendered through IconServices (`ISIcon` with an
`ISImageDescriptor`; `NSWorkspace.icon(forFile:)` agrees for the light look) at Dock size, light and dark appearance
and both greyscale variants of the tinted/clear modes: full size in the system shape, no plate. The previous icon
from the same renderer: on the plate in all of them.

**Regenerating.** `actool` for `.icon` needs Xcode 26 on a macOS 26 host (on macos-15 its asset runtime crashes);
without one, the `icon` job of `avalonia-package.yml` (runner macos-26) compiles the checked-in `Ombra.icon` and
uploads `Assets.car` as artifact `ombra-icon-catalog`.

## macOS: embedded browser print sheets

Nightly's embedded window must never become an on-screen sheet parent. Its native print and page-setup requests
go through `ZenEmbedDialogs` to `NightlyPrintPanel`, which presents AppKit's panel on Ombra's own window.
The authenticated runtime channel carries a securely archived `NSPrintInfo`; cancellation returns no print info.
Printer, paper, orientation, copies, range and scaling return to Nightly for the actual print job. Gecko-specific
options remain the print preview's settings; the standalone browser retains its original accessory controller.

Acceptance driver `printpanel` checks a system dialog already opened from print preview: visible sheet attached
to Ombra, no on-screen Nightly windows, and Escape dismisses it. Driver `dnd` samples `CGWindowList` throughout
the drag/drop cases and checks that Nightly has zero on-screen windows during and after the run.

## macOS: TLS through Network.framework

**What.** `Ombra.App` (and the test projects `Ombra.Tests`, `Ombra.Teams.Tests`) set the .NET runtime option
`System.Net.Security.UseNetworkFramework = true` (`RuntimeHostConfigurationOption` in the csproj). On macOS every
`SslStream`/`HttpClient` TLS connection of the process then goes through Network.framework instead of the legacy
SecureTransport. Windows and Linux ignore the option.

**Why.** SecureTransport has no TLS 1.3: a client that asks for `SslProtocols.Tls13` fails with "The requested security
protocol is not supported". The team server in IP mode accepts TLS 1.3 only (SERVER_DESIGN §6.2, SPKI pin), so
without the option Ombra on macOS cannot reach a team server at all. With it, TLS 1.3 works and
`RemoteCertificateValidationCallback` is still called, so the SPKI pin check (`Ombra.ClientCore.Teams.ServerEndpoint`)
keeps refusing a wrong key without any "continue anyway".

**Known difference (test servers).** A Network.framework client loses the tail of a response when the server drops
the TCP connection with the client's TLS records unread (an abortive close/RST): bodies over ~16 KB end with
"response ended prematurely". Real servers send close_notify and read until the client closes; hand-written test
servers must do the same (`UpdateServerTests.ReleaseServer` does `ShutdownAsync` and drains). Downloads from real
HTTPS servers (GitHub release assets) are unaffected.

**Checked by.**
- `Ombra.Teams.Tests` (13 tests; the production server image over TLS 1.3 with a pin, including
  `Ip_mode_refuses_a_wrong_pin_and_accepts_the_pinned_key_over_tls13`).
- `Ombra.Tests` network suites with the option on: corporate trust, network privacy, update/update server, provider
  usage, agent browser, preview, model settings.
- The built app: `Ombra --update` reads the real update feed over HTTPS.

**Note.** Kestrel on macOS still uses SecureTransport for *server* sockets, so a team server cannot serve TLS 1.3 on a
Mac; the Teams integration tests run the production server image (Linux) in a container.

## macOS: the traffic lights pinned in the middle of the 44 pt band

**What.** The window's client area extends under the title bar and the app's 44 pt band is the title bar; the traffic
lights sit in its middle, 16 pt from the left edge (`MainWindow.Chrome.cs`, `Platform/Mac/MacTrafficLights.cs`). The
title-bar container and the three buttons are made instances of runtime subclasses of their own classes (as
key-value observing does) whose frame setters — `setFrame:`, `setFrameOrigin:`, `setFrameSize:` and the buttons'
private `setFrameOrigin:ignoreRentry:` — hand AppKit's own setters the band's place while the window is not in full
screen.

**Why.** AppKit tiles its title bar again on a new window title, a resize and a change of appearance
(`-[NSThemeFrame _tileTitlebarAndRedisplay:]` → `_updateButtonPositions` → `-[_NSThemeWidget setFrameOrigin:ignoreRentry:]`,
which calls NSView's setter directly) and puts the buttons back at the top of its 28 pt bar, 8 pt above the band's
middle. Placing them once after opening and after resizes held only until the window title changed (the chat's title,
the attention dot) or the theme switched. Placing them again from `NSViewFrameDidChangeNotification` does not hold
either: the notification comes from inside AppKit's setter, which overwrites the change as it returns.

**Checked by.** The feature smoke's `title-bar` section (`tools/verify/feature-smoke/FeatureSmoke.TitleBar.cs`): in the
real window, wide and narrow, light and dark, sidebar shown and hidden, after a theme change, a new window title and a
resize, on the conversation and the Settings, Team and Browser pages — the lights' frames in the middle of the band,
every icon and text in the band on their middle (±0.5 pt) and clear of them, and in a `screencapture -l` shot the
lights' circles and the band's panel, search, ×, + and back icons on one pixel row (±1 px).
