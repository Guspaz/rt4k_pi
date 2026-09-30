# Readme

*Disclaimer: This project was developed with the assistance of AI as of commit 036ab16.*

This project is intended to replace the rt4k_esp32 project after the manufacturer rugpulled me and downgraded the microcontroller after I bought my initial unit. It's a work-in-progress and is not yet fit for public consumption.

It's designed to run on the Raspberry Pi Zero 2 W (Pi02W), and only the Pi02W: the Zero 2 is the only model that has "unlimited" power output due to having a direct shunt between the USB power input and output, and we need the W version to be able to communicate with it over wifi. You connect the RT4K power supply to the Pi02W power input, and the RT4K itself to the other Pi02W USB port.

rt4k_pi connects to the RT4K over USB. It provides a web remote, a live view of the RT4K's on-screen display, device status, firmware updates, and access to the RT4K's SD card through a network share.

Use rt4k_pi only on a trusted home network. Do not expose its web interface, file sharing, or serial TCP service to the internet.

## Setup

In short: in the Raspberry Pi Imager, set a custom content repository of `https://raw.githubusercontent.com/Guspaz/rt4k_pi/imager/rt4k_pi.json`, choose "rt4k_pi", enter your Wi-Fi details, and boot the Pi.

### Installing on an existing Pi

Releases also include a standalone `rt4k_pi` executable for Raspberry Pi OS Lite **64-bit, Bookworm (Debian 12) or newer**. Copy it to its own folder on the Pi, run `chmod +x rt4k_pi`, then `sudo ./rt4k_pi`. It installs itself as a service.

Once installed, rt4k_pi updates itself from the Settings page.

## Serial TCP service

The optional serial TCP service lets other software send text commands to the RT4K on port 2000. It does not support file transfers or other binary commands. Commands must end with a newline and be no longer than 255 characters. Use the web UI or network share for file access instead.

## Settings and files

Settings are saved in `settings.json` beside the application. Keep a copy if you want to back up your configuration.

The network share lets you access files on the RT4K's SD card, with these limitations:

- Files you create or modify are limited to 64 MiB. Editing part of an existing file is subject to the same limit.
- Too many large file operations at once may fail because of the Pi's limited memory. Try copying files one at a time.
- Wait for file copies and saves to finish before restarting or unplugging either device. Unsaved changes can be lost.
- Close files before renaming or deleting them if an operation reports that a file is busy.

## Application updates

rt4k_pi checks for new releases in the background. When one is available, **Settings** shows what's new and an **Install** button. You can also select **Check for updates** at any time.

- Installing an update also updates the Pi's system software, so it can take several minutes. Keep the Pi powered until the page reloads.
- If the system software update fails, the current version of rt4k_pi is left in place.
- If Settings says the Pi needs to restart, some system updates will finish the next time it restarts.
- Only one update can run at a time, and not while an RT4K firmware update is running. Updates preserve your settings. Downloads are limited to 256 MiB.

If the application will not start after an update, you can restore the previous version over SSH: stop the rt4k_pi service, replace `rt4k_pi` with the saved `rt4k_pi.previous` file in the same folder, and restart the service.

## RT4K firmware updates

An RT4K Pro or CE must be powered on with an SD card inserted. Firmware **1.75.0 or newer** is required. If your device runs an older version, use RetroTINK's official SD-card update instructions first. Installing versions below this minimum is not supported.

1. Finish any file copies to the RT4K, then open **Firmware** in the web UI.
2. Experimental releases are included by default; you can change this in **Settings**. Select a version to read its changelog, or show older releases to choose an earlier supported version.
3. Select the version to install and confirm. The Pi downloads the firmware and sends the files for your RT4K model automatically.
4. Wait for the page to report that the update is complete. Status refreshes automatically, and you can leave the page and return without stopping the update.

The **Status** page shows an **Update available** link beside the scaler's firmware version when a newer release is found. Checks happen automatically in the background and follow your experimental firmware setting.

**Keep both the Pi and RT4K powered throughout the update. Unplugging the Pi can also turn off the RT4K. Losing power during installation can leave the RT4K unable to start.**

- You can cancel while files are downloading or being sent, but not once installation starts.
- Reaching 100% of a file transfer does not mean the update is finished. The RT4K still needs to check the files, install the firmware, and restart.
- Remote control, the live on-screen display, and file sharing pause during an update. Do not change the RT4K's SD-card contents through another connection during this time.
- Firmware downloads are limited to 64 MiB and must finish within 15 minutes. Individual firmware files are limited to 16 MiB. Both devices need enough free storage for the update.

### If an update is interrupted

Interrupted updates are never resumed automatically. If installation had not started, you can start a new update once the page reports that the previous attempt has stopped. Temporary downloads on the Pi are removed; files already sent to the RT4K are kept.

If installation may have started, **leave the RT4K powered on** and wait for it to finish restarting. A timeout does not necessarily mean the update failed. The page continues checking the device and blocks another update until the outcome is known.

If the RT4K cannot start or the page says its SD card needs attention, follow RetroTINK's official recovery instructions. Do not delete firmware files or update records just to bypass a blocked update.
