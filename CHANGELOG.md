# Changelog

## 0.2.2

- Added a **Proxy** option under **Connection settings** to choose between **Use system proxy** and **No proxy** for Home Assistant connections.

## 0.2.1

- Fixed `clear_notification` not removing tagged notifications, including ones shown with a `group` when the clear sends only the `tag`.
- Errors while clearing a notification now appear in settings.

## 0.2.0

- Action buttons from `actions`: `URI` buttons open a link, and other buttons send a `mobile_app_notification_action` event to Home Assistant.
- Clicking a notification opens its `clickAction` or `url` link.
- `tag` and `group` replace earlier notifications, and `clear_notification` removes them.
- Buttons and links keep working from the tray and after HA Notify restarts.

## 0.1.2

- Checks for updates automatically and from settings.
- Shows the current and new version before downloading an update.
- Installer closes and reopens the running app, preserving connection settings.

## 0.1.1

- Added an EXE installer with bundled runtimes, shortcuts, and uninstall support.

## 0.1.0

Initial release.

- Windows notifications for Home Assistant titles and messages.
- WinUI 3 settings with system theme support.
- Tray operation, startup at sign-in, and automatic reconnection.
- Encrypted connection settings.
