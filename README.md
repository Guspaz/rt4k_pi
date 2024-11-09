# Readme

This project is intended to replace the rt4k_esp32 project after the manufacturer rugpulled me and downgraded the microcontroller after I bought my initial unit. It's a work-in-progress and is not yet fit for public consumption.

It's designed to run on the Raspberry Pi Zero 2 W (Pi02W), and only the Pi02W: the Zero 2 is the only model that has "unlimited" power output due to having a direct shunt between the USB power input and output, and we need the W version to be able to communicate with it over wifi. You connect the RT4K power supply to the Pi02W power input, and the RT4K itself to the other Pi02W USB port.

rt4k_pi communicates with the RT4K via the FTDI virtual serial port on the RT4K's USB power input. It requires the RT4K be running at least firmware v1.6.7 to get the necessary serial command support. At the moment, it's limited to basic commands (emulating a remote control and some other passive commands), but I hope to re-implement the WebDAV server and automatic RT4K firmware later if Mike implements file I/O support over serial. It has the potential to be dramatically higher performance than the old rt4k_esp32 project was.

Installation instructions will come in the future, but it will basically involve using the Raspberry Pi Imager to install Raspberry OS Lite 64-bit with the login/wifi/hostname pre-configured by the imager. The user will only need to copy the rt4k_pi executable to the Pi and run it once, after which the plan is to have it register itself as a service and start automatically on boot.
