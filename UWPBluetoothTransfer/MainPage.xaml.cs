using System;
using System.Collections.Generic;
using System.IO;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.UI.Core;


namespace UWPBluetoothTransfer
{
    public sealed partial class MainPage : Page
    {
        private BluetoothFileTransfer BluetoothTransfer;
        private ObservableCollection<BluetoothDevice> deviceList;

        public MainPage()
        {
            this.InitializeComponent();
            BluetoothTransfer = new BluetoothFileTransfer();
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
                foreach (var device in await BluetoothTransfer.ScanForBluetoothDevicesAsync())
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
                BluetoothTransfer.SelectedDevice = selectedDevice;
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

                    await BluetoothTransfer.SendFileAsync(file, progress, SpoofFileTypeCheckBox.IsChecked ?? false);
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

        private async void ReceptionModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool isReceptionMode = ReceptionModeToggle.IsOn;

            // Toggle UI elements for send mode
            ScanDevicesButton.IsEnabled = !isReceptionMode;
            DeviceList.IsEnabled = !isReceptionMode;
            SelectFileButton.IsEnabled = !isReceptionMode && DeviceList.SelectedItem != null;
            SpoofFileTypeCheckBox.IsEnabled = !isReceptionMode;

            if (isReceptionMode)
            {
                // Start listening for incoming connections
                StartFileReceptionMode();
                StatusText.Text = "Listening for incoming file transfers...";
            }
            else
            {
                // Stop listening for incoming connections
                await StopFileReceptionMode();

                // A check to keep error texts or other information visible when the
                // error resulted in reception mode being turned off
                if (StatusText.Text == "Listening for incoming file transfers...")
                    StatusText.Text = "File reception mode disabled.";
            }
        }
        private async void StartFileReceptionMode()
        {
            try
            {
                TransferProgressBar.Maximum = 100;
                TransferProgressBar.Value = 0;
                ProgressText.Text = "0%";
                ProgressGrid.Visibility = Visibility.Visible;

                Progress<double> progress = new Progress<double>(progressValue =>
                {
                    TransferProgressBar.Value = progressValue * 100;
                    ProgressText.Text = $"{progressValue:P0}";
                });

                // Subscribe to event that triggers after the first packet of a file has been read
                BluetoothTransfer.IncomingFileTransferRequested += OnIncomingIncomingFileTransferRequested;
                BluetoothTransfer.IncomingFileTransferCompleted += OnIncomingFileTransferCompleted;

                await BluetoothTransfer.StartListeningForFileTransferConnectionAsync(progress);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to start reception mode: {ex.Message}";
                ReceptionModeToggle.IsOn = false;
            }
        }


        private async Task<bool> SaveReceivedFile(ReceivedFile file)
        {
            try
            {
                StorageFile storageFile = null;

                // Create a TaskCompletionSource to handle the result
                var tcs = new TaskCompletionSource<StorageFile>();

                // Run FileSavePicker on UI thread
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                     () =>
                    {
                        FileSavePicker picker = new FileSavePicker();
                        picker.FileTypeChoices.Add(Path.GetExtension(file.FileNameWithExtension),
                            new List<string>() { Path.GetExtension(file.FileNameWithExtension) });
                        picker.SuggestedFileName = file.FileNameWithExtension;

                        // Use ContinueWith to handle the picker result
                        picker.PickSaveFileAsync().AsTask().ContinueWith(t => tcs.SetResult(t.Result));
                    });

                // Wait for the file picker result
                storageFile = await tcs.Task;

                if (storageFile != null)
                {
                    file.FileContent.Position = 0;
                    using (var stream = await storageFile.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        await stream.AsStreamForWrite()
                            .WriteAsync(file.FileContent.ToArray(), 0, (int)file.FileContent.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        StatusText.Text = $"Failed to save received file: {ex.Message}";
                    });
                await StopFileReceptionMode();
                return false;
            }
            await StopFileReceptionMode();
            return true;
        }

        private async void OnIncomingIncomingFileTransferRequested(object sender, (string fileName, long fileSize) args)
        {
            await ShowIncomingFileTransferPrompt(args.fileName, args.fileSize);
        }

        private async void OnIncomingFileTransferCompleted(object sender, ReceivedFile file)
        {
            if (file != null)
            {
                await SaveReceivedFile(file);
            }
            else
            {
                StatusText.Text = "Failed to receive file or file received was null, unable to save.";
                await StopFileReceptionMode();
            }
        }

        public async Task StopFileReceptionMode()
        {
            try
            {
                // Ensure UI changes are called on the correct thread
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        ProgressGrid.Visibility = Visibility.Collapsed;
                        ReceptionModeToggle.IsOn = false;
                    });

                // Unsubscribe from events
                if (BluetoothTransfer != null)
                {
                    BluetoothTransfer.IncomingFileTransferRequested -= OnIncomingIncomingFileTransferRequested;
                    BluetoothTransfer.IncomingFileTransferCompleted -= OnIncomingFileTransferCompleted;
                    BluetoothTransfer.StopListeningForFileTransferConnection();
                }
            }
            catch (Exception ex)
            {
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        StatusText.Text = $"Error while stopping reception mode: {ex.Message}";
                    });
            }
        }

        public async Task ShowIncomingFileTransferPrompt(string fileName, long fileSize)
        {
            try
            {
                // Read file metadata (name and size) from the incoming connection

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    // Update the dialog with file information
                    FileNameText.Text = fileName;
                    FileSizeText.Text = FormatFileSize(fileSize);

                    // Show the file reception dialog
                    ContentDialogResult result = await FileReceptionDialog.ShowAsync();

                    if (result == ContentDialogResult.Primary) // Accept
                    {
                        // Handle file reception
                        BluetoothTransfer.IncomingFileAccepted(true);
                        StatusText.Text = "Receiving file...";
                    }
                    else // Decline or Cancel
                    {
                        BluetoothTransfer.IncomingFileAccepted(false);
                        StatusText.Text = "File transfer declined.";
                        ProgressGrid.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    StatusText.Text = $"Error handling incoming connection: {ex.Message}";
                });
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }
    }
}
