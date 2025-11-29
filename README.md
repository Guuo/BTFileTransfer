# BTFileTransfer
A Universal Windows Platform app for simple file transfer between paired devices via Bluetooth. Originally developed for use on the Hololens 2. 
<p align="center">
 <img src=https://github.com/user-attachments/assets/bdb272e8-89d8-49cd-b47d-5cd242f4c2ee
   width="500"/>
<p/>
  
## Installation
If possible, run Install.ps1 as a PowerShell script to automate the process of installing the required self-signed certificate followed by the app itself.

To manually install, first double-click UWPBluetoothTransfer_1.1.1.0_x86_x64_arm_arm64.cer to install the certificate. Choose Trusted People as the certificate store. Then, double-click UWPBluetoothTransfer_1.1.1.0_x86_x64_arm_arm64.msixbundle and follow the installation wizard to install the application.

## Uninstallation
### Windows 10/11
Open PowerShell as administrator and use the following command:

Get-AppxPackage 11a984ef-e41c-4b27-98a6-76e6d3b52d9e | Remove-AppxPackage 

## Usage
### Sending files
1. Pair your devices with Bluetooth via your operating system.
2. Press the "Scan for Bluetooth devices" button. The list below should show all paired devices.
3. Click on the device you wish to send files to.
4. (Optional) Check the "Spoof file type" checkbox to send every file as a .txt file. Some devices and/or operating systems may refuse to receive files of a type the device deems unsupported or unknown. When spoofing the file type, simply change the file extension back to the original from .txt after receiving the file on your device.
5. Press the "Select and Send File" button to open a file picker window. Choose your file to begin the transfer. The file picker only supports choosing one file at a time.

### Receiving files
1. Pair your devices with Bluetooth via your operating system.
2. Toggle the file reception switch on the right side of the app window.
3. While the file reception mode is active, attempt to share a file from your paired device.
4. After the entire file transfer process has completed succesfully, a file picker will automatically open allowing you to save the file to the location of your choosing. File reception only supports receiving a single file at a time.

A progress bar at the bottom tracks the progress you your file transfer. When the transfer ends, a text at the bottom will inform you of the result. If the transfer fails with an error code, you may check the code against the following table to troubleshoot your problem:

![kuva](https://github.com/user-attachments/assets/51f109f7-4660-4b33-b428-fdda0cdac462)

