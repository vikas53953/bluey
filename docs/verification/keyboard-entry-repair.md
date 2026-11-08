# Keyboard entry repair work source

Frozen NAT-023 requires a keyboard path for notch expansion and every core
flow. Current source exposes a mouse button and notification-area menu for
opening the panel, but registers only the emergency Stop shortcut. Add
Ctrl+Alt+B as a documented global Open Boo controls shortcut, alongside the
existing Ctrl+Alt+F12 Stop shortcut. Keep the compact notch non-activating,
off the taskbar, and excluded from capture.

Register/unregister both shortcuts with the notch lifecycle. Report a shortcut
registration failure honestly and retain mouse/tray recovery. Show the shortcut
in the existing button tooltip/accessibility name and tray command label.

Verification: Release build, oracle verification, and direct native keyboard
observation on the next exact candidate. This repairs the keyboard entry path;
it does not assume the desktop testing tool will enumerate the panel.
