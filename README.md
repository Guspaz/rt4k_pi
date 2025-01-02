# Readme

This project is intended to replace the rt4k_esp32 project after the manufacturer rugpulled me and downgraded the microcontroller after I bought my initial unit. It's a work-in-progress and is not yet fit for public consumption.

It's designed to run on the Raspberry Pi Zero 2 W (Pi02W), and only the Pi02W: the Zero 2 is the only model that has "unlimited" power output due to having a direct shunt between the USB power input and output, and we need the W version to be able to communicate with it over wifi. You connect the RT4K power supply to the Pi02W power input, and the RT4K itself to the other Pi02W USB port.

rt4k_pi communicates with the RT4K via the FTDI virtual serial port on the RT4K's USB power input. It requires the RT4K be running at least firmware v1.6.7 to get the necessary serial command support. At the moment, it's limited to basic commands (emulating a remote control and some other passive commands), but I plan to implement remote file access via SMB/CIFS (Windows file shares) once Mike adds file I/O support.

Incomplete installation instructions can be found at: https://github.com/Guspaz/rt4k_pi/blob/master/setup.md

The missing instructions are basically, use SSH/SCP to copy the rt4k_pi binary over to the /home/pi folder (assuming you used "pi" as the username) and run it, it will handle the rest of the install/config/setup itself.

I do plan to prove ready-made binaries eventually. The project is designed to be compiled to a single ARM64 executable using dotnet Native AOT. This requires a Linux build server with ARM64 cross-compile support, or more likely, an ARM64 Linux build server. For now, I'm building on a cheap Azure VM, but GitHub plans to launch ARM64 runners for open source projects by the end of the year, at which point I'll be able to have GitHub build releases. If you want to build it yourself, you'll need a working arm64 build environment for .net 9.0 nativeaot. Then do a "dotnet publish" from inside the source folder and grab the rt4k_bin file that ends up in bin/Release/net9.0/linux-arm64/publish
