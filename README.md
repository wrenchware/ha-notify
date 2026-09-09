# HA Notify

A small personal Windows app for native Windows 11 notifications from Home Assistant, with a WinUI 3 settings window. Built independently in C#; no code from ha-china/ha-windows is included.

This is an independent project, not an official Home Assistant companion app. Home Assistant logo assets are credited in [Assets/README.md](HaNotify/Assets/README.md).

## Run

Requires Windows 11 on an x64 PC. Build with the .NET 10 SDK on Windows:

```powershell
dotnet publish .\HaNotify\HaNotify.csproj -c Release -o .\dist --self-contained true
.\dist\HaNotify.exe
```

1. Enter your Home Assistant URL (for example `http://homeassistant.local:8123`). Use your HTTPS address when connecting over the internet.
2. In your Home Assistant user profile, open **Security** and create a **long-lived access token**. Paste it into the app. Do not share the token in chat or commit it to source control.
3. Choose a device name such as `Office PC`, then click **Save & connect**.
4. Click **Test notification** to check Windows delivery. Notifications are identified as **Home Assistant** in Windows; the desktop app is named **HA Notify**. Allow notifications in Windows Settings → System → Notifications. Do Not Disturb can suppress banners while retaining notifications in Notification Center.
5. In HA's Developer Tools → Actions, find `notify.mobile_app_office_pc` (the exact suffix follows your registered device name). If it is missing, reload the Mobile App integration or restart HA.

```yaml
action: notify.mobile_app_office_pc
data:
  title: "Home Assistant"
  message: "The washing machine is finished."
```

The same action works in automations and scripts. Home Assistant's `mobile_app` integration must be enabled; it is included by `default_config` and will normally already be present if you use the phone companion app.

## Behavior and limits

- Real Windows App SDK notifications, including Notification Center history.
- Official Home Assistant logo for the app, tray, and notifications, with multiple resolutions for Windows display scaling. Regenerate with `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-Icon.ps1`.
- Light and dark colors automatically follow Windows Settings → Personalization → Colors → Choose your default app mode, including changes while running. No separate theme toggle. Windows high-contrast colors take precedence.
- The settings screen uses native WinUI 3 text fields, a password field with a reveal button, aligned action buttons, and a separate connection banner. The tray uses the Windows notification-area helper; it does not host the settings UI.
- Closing the window hides it; use the tray menu to exit. Optional startup at Windows sign-in.
- Reconnects automatically after a network failure or HA restart, including temporary authentication or notification-channel rejection while HA starts. Retries continue indefinitely with a delay capped at 60 seconds, reusing the saved registration. Persistent rejection requires checking the token or Mobile App integration.
- The Windows test result appears separately from HA connection status; the test does not check HA connectivity. Credentials and registration are encrypted using Windows DPAPI for the current user in `%LOCALAPPDATA%\HaNotify\connection.dat`.
- Receives directly over HA's authenticated WebSocket connection. No MQTT broker, custom HA integration, listening PC port, or cloud relay is needed.
- The PC must be awake, the app running, and HA reachable. Notifications sent while disconnected are not queued by this app.
- Version 0.1 handles titles and text only. Images, action buttons, click-through URLs, and phone-specific notification commands are not implemented.
- Changing the server or device name creates a new registration; remove old devices in HA's Mobile App integration as needed. Updating the token for the same server/name reuses its registration.
- Run as your normal Windows user; elevated apps cannot show these notifications. Keep the published folder at a stable path once enabling startup.

## References

- [HA app registration](https://developers.home-assistant.io/docs/api/native-app-integration/setup/)
- [HA WebSocket notifications](https://developers.home-assistant.io/docs/api/native-app-integration/notifications/)
- [Microsoft app notifications](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart)

Live delivery and restart recovery have been tested during development. Test delivery against your own Home Assistant instance after setup.

Run protocol regression checks with `dotnet run --project .\tests\ProtocolChecks.csproj -c Release`. They simulate HA restarting and rejecting authentication/subscription before recovery. `HaNotify.exe --preview` renders isolated light, dark, and narrower-window previews next to the executable without reading saved credentials or connecting to HA.
