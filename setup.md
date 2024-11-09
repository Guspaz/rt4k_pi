# Setup Instructions

## What you need

- A Raspberry Pi Zero 2 W. ONLY this model will work, it must be this EXACT version. Not the "Pi Zero", not the "Pi Zero W", not the "Pi Zero 2". Only the "Pi Zero 2 W".

- A USB power supply rated at least 2.1+, preferably 2.4+ amps. You're may already be using a suitable one for your RT4K.

- Cables and/or adapters to connect your USB power supply to the Raspberry Pi Zero 2 W's Micro USB power input (example: USB-A or USB-C to Micro USB)

- Cables and/or adapters to connect the Raspberry Pi Zero 2 W's other Micro USB port to the RetroTINK 4K's USB-C power input (example: Micro USB to USB-C)

- A microSD card. Minimum 8GB, recommend 16 GB or 32 GB. I recommend "Sandisk 32GB MAX Endurance", currently available on Amazon for $13 USD. These have great write endurance (they use MLC) and should last basically forever.

## Instructions

1. Download the Raspberry Pi Imager (https://www.raspberrypi.com/software/) and run it

1. Select the device type "Raspberry Pi Zero 2 W" (rt4k_pi does not support anything else)

1. Choose "Raspberry Pi OS Lite (64-bit)". It can be found in the "Raspberry Pi OS (other)" submenu.

1. Insert your microSD card into the computer and choose it in the imager, then click "NEXT"

1. You will be asked if you want to use OS customization. Click "EDIT SETTINGS"

1. Enable "Set hostname" and set it to "rt4k"

1. Enable "Set username and password" and set them as you like. DO NOT USE NUMBERS OR SPECIAL CHARACTERS IN EITHER THE USERNAME OR PASSWORD. Only use letters. For some reason, numbers break things.

1. Enable "Configure wireless LAN" and enter the SSID and password of your wifi network. It must be a 2.4 GHz network compatible with 802.11b/g/n.

1. Set the locale settings if you like.

1. Switch to the "Services" tab and enable SSH. Set it to use password authentication.

1. Click "SAVE"

1. In the "Use OS Customization" dialog, click "YES"

1. You will be warned that the imager will erase your microSD card. Ensure the right device is displayed and then click "YES".

1. Wait for the imager to finish. Remove the microSD card from the PC and insert it into the Pi Zero 2W

1. Connect your RT4K power supply to the Raspberry Pi Zero 2 W's "PWR IN" USB port. If you're holding the Pi and the microSD slot and Mini HDMI port are on the left, then the "PWR IN" USB port is on the far right.

1. Connect the Raspberry Pi Zero 2 W's other USB port (the one closest to the middle) to the RT4K's power input. The green LED on the Pi will flicker/flash, that's the disk activity light.

1. The Pi will take a few minutes to boot up. Only the first boot is this slow, it will boot faster the next time.

1. TODO: Instructions on installing via SSH & wget here