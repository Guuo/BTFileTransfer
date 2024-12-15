using System;
using System.Collections.Generic;
using System.IO;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using Windows.Devices.Bluetooth;


namespace UWPBluetoothTransfer
{
    public sealed partial class MainPage : Page
    {
        private BluetoothFileTransfer bluetoothTransfer;
        private ObservableCollection<BluetoothDevice> deviceList;

        public MainPage()
        {
            this.InitializeComponent();
            bluetoothTransfer = new BluetoothFileTransfer();
            deviceList = new ObservableCollection<BluetoothDevice>();
            DeviceList.ItemsSource = deviceList;
        }

        private async void ScanDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ScanDevicesButton.IsEnabled = false;
                StatusText.Text = "Scanning for Bluetooth devices...";

                deviceList.Clear();
                foreach (var device in await bluetoothTransfer.ScanForBluetoothDevicesAsync())
                {
                    deviceList.Add(device);
                }

                StatusText.Text = $"Found {deviceList.Count} Bluetooth devices";
                ScanDevicesButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error initializing Bluetooth: " + ex.Message;
                ScanDevicesButton.IsEnabled = true;
            }
        }

        private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeviceList.SelectedItem is BluetoothDevice selectedDevice)
            {
                bluetoothTransfer.SelectedDevice = selectedDevice;
                SelectFileButton.IsEnabled = true;
                StatusText.Text = $"Selected device: {selectedDevice.Name}";
            }
            else
            {
                SelectFileButton.IsEnabled = false;
            }
        }

        private async void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                try
                {
                    StatusText.Text = "Sending file...";
                    SelectFileButton.IsEnabled = false;
                    ProgressGrid.Visibility = Visibility.Visible;
                    TransferProgressBar.Maximum = 100;
                    TransferProgressBar.Value = 0;
                    ProgressText.Text = "0%";

                    Progress<double> progress = new Progress<double>(progressValue =>
                    {
                        TransferProgressBar.Value = progressValue * 100;
                        ProgressText.Text = $"{progressValue:P0}";
                    });

                    await bluetoothTransfer.SendFileAsync(file, progress, SpoofFileTypeCheckBox.IsChecked ?? false);
                    StatusText.Text = "File sent successfully";
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Error sending file: " + ex.Message;
                }
                finally
                {
                    SelectFileButton.IsEnabled = true;
                    ProgressGrid.Visibility = Visibility.Collapsed;
                }
            }
        }
    }
}
