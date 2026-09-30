# Setup Instructions

## What you need

- A Raspberry Pi Zero 2 W. ONLY this model will work, it must be this EXACT version. Not the "Pi Zero", not the "Pi Zero W", not the "Pi Zero 2". Only the "Pi Zero 2 W".

- A USB power supply rated at least 2.1+, preferably 2.4+ amps. You're may already be using a suitable one for your RT4K.

- Cables and/or adapters to connect your USB power supply to the Raspberry Pi Zero 2 W's Micro USB power input (example: USB-A or USB-C to Micro USB)

- Cables and/or adapters to connect the Raspberry Pi Zero 2 W's other Micro USB port to the RetroTINK 4K's USB-C power input (example: Micro USB to USB-C)

- A microSD card. Minimum 8GB, recommend 16 GB or 32 GB. I recommend "Sandisk 32GB MAX Endurance", currently available on Amazon for $13 USD. These have great write endurance (they use MLC) and should last basically forever.

## Instructions

1. Download the Raspberry Pi Imager (https://www.raspberrypi.com/software/) and run it

1. Open the Imager's "App Options" (the gear/settings button), choose "Content Repository" → "Use custom URL", and enter:

   `https://github.com/Guspaz/rt4k_pi/releases/latest/download/rt4k_pi.json`

   If your Imager doesn't have that option, start it from a command prompt instead: `rpi-imager --repo https://github.com/Guspaz/rt4k_pi/releases/latest/download/rt4k_pi.json`

1. Select the device type "Raspberry Pi Zero 2 W"

1. For the operating system, choose "rt4k_pi". The Imager downloads it for you.

1. Insert your microSD card into the computer and choose it in the imager, then click "NEXT"

1. You will be asked if you want to use OS customization. Click "EDIT SETTINGS"

1. Enable "Configure wireless LAN" and enter the name and password of your Wi-Fi network. It must be a 2.4 GHz network.

1. Optionally, set a username and password and enable SSH if you want to log in to the Pi yourself. The hostname is always "rt4k", whatever you enter.

1. Click "SAVE", then "YES" to use the settings.

1. You will be warned that the imager will erase your microSD card. Ensure the right device is displayed and then click "YES".

1. Wait for the imager to finish. Remove the microSD card from the PC and insert it into the Pi Zero 2 W

1. Connect your RT4K power supply to the Raspberry Pi Zero 2 W's "PWR IN" USB port. If you're holding the Pi and the microSD slot and Mini HDMI port are on the left, then the "PWR IN" USB port is on the far right.

1. Connect the Raspberry Pi Zero 2 W's other USB port (the one closest to the middle) to the RT4K's power input. The green LED on the Pi will flicker/flash, that's the disk activity light.

1. The first boot takes a few minutes. After that, open http://rt4k.local in a browser on the same network.

<details>
<summary>Can't reach http://rt4k.local?</summary>

- Wait a few more minutes; the first boot is slow.
- Some devices (older Android phones in particular) don't support ".local" names. Look up the Pi's address in your router's device list and use that instead.
- Check that the Wi-Fi name and password were entered correctly and that the network is 2.4 GHz. If not, write the card again.

</details>
