# BTFileTransfer
A Universal Windows Platform app for simple file transfer between paired devices via Bluetooth.

## Installation
If possible, run Install.ps1 as a PowerShell script to automate the process of installing the required self-signed certificate followed by the app itself.

To manually install, first double-click UWPBluetoothTransfer_1.0.0.0_x86_x64_arm_arm64.cer to install the certificate. Choose Trusted People as the certificate store. Then, double-click UWPBluetoothTransfer_1.0.0.0_x86_x64_arm_arm64.msixbundle and follow the installation wizard to install the application.

## Usage
1. Pair your devices with Bluetooth via your operating system.
2. Press the "Scan for Bluetooth devices" button. The list below should show all paired devices.
3. Click on the device you wish to send files to.
4. (Optional) Check the "Spoof file type" checkbox to send every file as a .txt file. Some devices and/or operating systems may refuse to receive files of a type the device deems unsupported or unknown. When spoofing the file type, simply change the file extension back to the original from .txt after receiving the file on your device.
5. Press the "Select and Send File" button to open a file picker window. Choose your file to begin the transfer. The file picker only supports choosing one file at a time.

A progress bar at the bottom tracks the progress you your file transfer. When the transfer ends, a text at the bottom will inform you of the result. If the transfer fails with an error code, you may check the code against the following table to troubleshoot your problem:

![kuva](https://github.com/user-attachments/assets/51f109f7-4660-4b33-b428-fdda0cdac462)

