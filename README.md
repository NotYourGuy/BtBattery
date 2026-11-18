<p align="center">
  <img src="docs/icon.svg" width="96" height="96" alt="BtBattery icon">
</p>

<h1 align="center">BtBattery</h1>

<p align="center">See the battery level of your Bluetooth headphones, mouse, keyboard and phone right on the Windows taskbar.</p>

<p align="center">
  <img src="docs/screenshot.png" alt="BtBattery on the Windows 11 taskbar, showing headphones 100%, keyboard 100% and mouse 90% next to the system tray">
</p>

## Features

- Shows every connected Bluetooth device that reports its battery, with an icon for its type.
- Sits next to the system tray and grows or shrinks as devices connect and disconnect.
- Hover to see device names. Levels turn red at 20% or below.
- Matches the taskbar's light or dark theme and your display scaling.
- Tiny and dependency-free: one small `.exe`, nothing to install.

## Get started

1. Download this repository (**Code → Download ZIP**) and unzip it somewhere you'll keep it, for example `Documents\BtBattery`.
2. Double-click **`build.cmd`**. It builds `BtBattery.exe` with the C# compiler that comes with Windows.
3. Double-click **`BtBattery.exe`**.

To start it automatically when you sign in, right-click the strip and choose **Start with Windows**.

## Using it

| Action | What it does |
| --- | --- |
| Hover | Shows each device's name and battery level |
| Left-click | Checks the levels right away (they also update every 30 seconds) |
| Right-click | **Refresh now**, **Start with Windows**, **Exit** |

## Good to know

- Requires Windows 10 or 11.
- Only devices that report their battery to Windows appear. If a device shows a percentage in **Settings → Bluetooth & devices**, it will show here too.
- Levels come from Windows, which updates them every few minutes, so they can be slightly behind.
- To uninstall, untick **Start with Windows**, choose **Exit**, then delete the folder.
