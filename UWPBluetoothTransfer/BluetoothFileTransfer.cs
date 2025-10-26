using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Devices.Bluetooth.Rfcomm;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using System.IO;
using System.Linq;
using MimeMapping;
using System.Diagnostics;
using System.ServiceModel.Dispatcher;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using static System.Net.WebRequestMethods;
using Windows.UI.Xaml.Controls;

namespace UWPBluetoothTransfer
{
    public class BluetoothFileTransfer
    {
        private RfcommDeviceService RfcommService { get; set; }
        private StreamSocket Socket { get; set; }
        private StreamSocketListener Listener;
        private RfcommServiceProvider Provider;
        public BluetoothDevice SelectedDevice {get; set;}
        public TaskCompletionSource<bool> UserResponseTask;
        public event EventHandler<(string fileName, long fileSize)> IncomingFileTransferRequested;
        public event EventHandler<(ReceivedFile, string message)> IncomingFileTransferCompleted;

        public async Task<List<BluetoothDevice>> ScanForBluetoothDevicesAsync()
        {
            // Query for bluetooth devices
            string selector = BluetoothDevice.GetDeviceSelector();
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
            List<BluetoothDevice> BTDevices = new List<BluetoothDevice>();

            foreach (var device in devices)
            {
               BTDevices.Add(await BluetoothDevice.FromIdAsync(device.Id));
            }
            return BTDevices;
        }


        public async Task SendFileAsync(StorageFile file, IProgress<double> progress = null, bool spoofFileType = false)
        {
            try
            {
                // Ensure previous connections are properly disposed
                Dispose();

                // Get the rfcomm services
                RfcommDeviceServicesResult result = await SelectedDevice.GetRfcommServicesAsync();
                if (result.Services.Count == 0)
                {
                    throw new Exception("No RFCOMM services found on device.");
                }

                // set serviceIndex to index of OPP if present
                int serviceIndex = -1;
                for (int i = 0; i < result.Services.Count; i++)
                {
                    var name = result.Services[i].ServiceId.Uuid;
                    if (name.ToString().StartsWith("00001105"))
                    {
                        serviceIndex = i;
                        break;
                    }
                }

                if (serviceIndex == -1)
                {
                    throw new Exception("No Object Push Profile service found on device.");
                }

                RfcommService = result.Services[serviceIndex];

                Socket = new StreamSocket();
                await Socket.ConnectAsync(
                    RfcommService.ConnectionHostName,
                    RfcommService.ConnectionServiceName);


                if (Socket == null)
                {
                    throw new Exception("Failed to create socket connection.");
                }

                using (var stream = await file.OpenReadAsync())
                using (var writer = new DataWriter(Socket.OutputStream))
                {
                    // OBEX Connect Request
                    byte[] connectHeader = new byte[] {

                        0x80, // Connect OpCode
                        0x00, 0x07, // Packet Length (7 bytes)
                        0x10, // OBEX Version
                        0x00, // Flags
                        0x20, 0x00 // Max Packet Length (8192)

                    }; 
                    writer.WriteBytes(connectHeader);
                    await writer.StoreAsync();

                    uint connectionId = 0;

                    // Read connect response
                    using (var reader = new DataReader(Socket.InputStream))
                    {
                        reader.InputStreamOptions = InputStreamOptions.Partial;
                        await reader.LoadAsync(3);
                        byte[] response = new byte[3];
                        reader.ReadBytes(response);

                        if (response[0] != 0xA0) // Success response
                            throw new Exception("OBEX connection failed");

                        uint responseSize = BitConverter.ToUInt16(response.Reverse().ToArray(), 0);
                        await reader.LoadAsync(responseSize - 3);
                        response = new byte[responseSize - 3];
                        reader.ReadBytes(response);

                        byte[] connectionIdBytes = response
                            .SkipWhile(x => x != 0xCB)
                            .Skip(1) // Skip 0xCB
                            .Take(4)
                            .Reverse()
                            .ToArray();

                        // Check if 4 bytes were actually found
                        if (connectionIdBytes.Length == 4)
                        {
                            connectionId = BitConverter.ToUInt32(connectionIdBytes, 0);
                        }
                        else
                        {
                            connectionId = 0;
                        }
                        
                        // Send PUT request
                        byte[] putHeader = new byte[] {
                            0x02, // PUT OpCode
                            0x00, 0x00 // Packet Length (to be filled)
                        };

                        // Name header
                        byte[] tempNameBytes = System.Text.Encoding.Unicode.GetBytes(
                            spoofFileType ? Path.GetFileNameWithoutExtension(file.Name) : file.Name);
                        byte[] nameBytes = new byte[tempNameBytes.Length + 2];

                        // Swap endianness of name string
                        for (int i = 0; i < tempNameBytes.Length; i += 2)
                        {
                            nameBytes[i] = tempNameBytes[i + 1];
                            nameBytes[i+1] = tempNameBytes[i];
                            
                        }

                        byte[] nameHeader = new byte[] {
                            0x01, // Name header identifier
                            (byte)((nameBytes.Length + 3) >> 8),
                            (byte)(nameBytes.Length + 3) // Length of name header
                        };

                        // Length header
                        byte[] lengthHeader = new byte[] {
                            0xC3, // Length header identifier
                            (byte)(stream.Size >> 24), // The Length header is always 5 bytes total, 1 byte
                                                       // for the identifier + 4 bytes for the length value
                            (byte)(stream.Size >> 16),
                            (byte)(stream.Size >> 8),
                            (byte)stream.Size
                        };

                        byte[] connectionIdHeader = Array.Empty<byte>(); // Default to empty

                        // Only create the header if the connectionId is non-zero
                        if (connectionId != 0)
                        {
                            connectionIdHeader = new byte[]
                            {
                                0xCB,
                                (byte)(connectionId >> 24),
                                (byte)(connectionId >> 16),
                                (byte)(connectionId >> 8),
                                (byte)connectionId
                            };
                        }

                        
                        // Type header
                        string mimeType = MimeUtility.GetMimeMapping(file.Name + char.MinValue);
                        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(spoofFileType ? ("text/plain" + char.MinValue) : mimeType);

                        byte[] typeHeader = new byte[]
                        {
                            0x42, // Type header identifier
                            (byte)((typeBytes.Length + 3) >> 8), // Length of type header
                            (byte)(typeBytes.Length + 3)
                        };
                        
                        // Calculate new total packet length including all headers
                        int firstPacketLength = putHeader.Length +
                                                nameHeader.Length + nameBytes.Length +
                                                typeHeader.Length + typeBytes.Length +
                                                lengthHeader.Length +
                                                connectionIdHeader.Length +
                                                3; // + 3 to account for the 3 bytes of the body header, which hasn't been created yet

                        // Send file content in chunks
                        byte[] buffer = new byte[8192];
                        long totalBytesRead = 0;
                        int bodyBytes;
                        int chunkCount = 0;
                        int packetLen = 0;

                        // New OBEX packet for each chunk
                        while ((bodyBytes = await stream.AsStreamForRead().
                                   ReadAsync(buffer, 0, buffer.Length - firstPacketLength)) > 0)
                        {
                            // Body header
                            byte[] bodyHeader = new byte[]
                            {
                                0x48, // Body header identifier
                                (byte)((bodyBytes + 3) >> 8), // Length of body header
                                (byte)(bodyBytes + 3)
                            };

                            if (chunkCount > 0)
                            {
                                packetLen = bodyBytes + bodyHeader.Length +
                                            putHeader.Length;
                            }
                            else
                            {
                                packetLen = bodyBytes +
                                            bodyHeader.Length +
                                            connectionIdHeader.Length +
                                            typeHeader.Length +
                                            typeBytes.Length +
                                            putHeader.Length +
                                            nameHeader.Length +
                                            nameBytes.Length +
                                            lengthHeader.Length;
                            }
                            // Add packet length to put header
                            putHeader[1] = (byte)(packetLen >> 8);
                            putHeader[2] = (byte)packetLen;

                            // Write headers
                            writer.WriteBytes(putHeader);

                            // Only write these if this is the first packet
                            if (chunkCount == 0)
                            {
                                // Write connectionIdHeader only if it's not empty
                                if (connectionIdHeader.Length > 0)
                                {
                                    writer.WriteBytes(connectionIdHeader);
                                }
                                writer.WriteBytes(nameHeader);
                                writer.WriteBytes(nameBytes);
                                writer.WriteBytes(lengthHeader);
                                writer.WriteBytes(typeHeader);
                                writer.WriteBytes(typeBytes);
                            }
                            writer.WriteBytes(bodyHeader);
                            writer.WriteBytes(buffer.Take(bodyBytes).ToArray());
                            await writer.StoreAsync();

                            // Read response
                            await reader.LoadAsync(8);
                            response = new byte[8];
                            reader.ReadBytes(response);
                            if (response[0] != 0x90)
                            {
                                if (response[0] == 0xCF)
                                {
                                    throw new Exception($"OBEX transfer failed with code 0x" +
                                                        $"{BitConverter.ToString(response, 0, 1)}: Unsupported media type");
                                }
                                throw new Exception($"OBEX transfer failed with code 0x" +
                                                    $"{BitConverter.ToString(response, 0, 1)}");
                            }
                                

                            totalBytesRead += bodyBytes;
                            chunkCount++;
                            progress?.Report((double)totalBytesRead / stream.Size);
                        }

                        // End of Body
                        writer.WriteBytes(new byte[]
                        {
                            0x82, // Put, final bit is set
                            0x00, 0x06, // Length of packet
                            0x49, // End of Body header
                            0x00, 0x03 // Length of body (0) plus HI and header length (3)
                        });
                        await writer.StoreAsync();

                        await reader.LoadAsync(3);
                        response = new byte[3];
                        reader.ReadBytes(response);
                        if (response[0] != 0xA0)
                            throw new Exception($"OBEX transfer failed with code 0x{BitConverter.ToString(response, 0, 1)}");
                    }
                }
            }
            catch (Exception ex)
            {
               
                Dispose();
                throw new Exception($"File transfer failed: {ex.Message}", ex);
            }
        }

        public void IncomingFileAccepted(bool accepted)
        {
            UserResponseTask?.SetResult(accepted);
        }

        public async Task StartListeningForFileTransferConnectionAsync(IProgress<double> progress = null)
        {
            try
            {
                // Ensure previous connections are properly disposed
                Dispose();

                // Set up RFCOMM server listener 
                // Windows seems to not require pairing when using OPP despite the SocketProtectionLevel chosen
                Provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.ObexObjectPush);
                Listener = new StreamSocketListener();
                await Listener.BindServiceNameAsync(Provider.ServiceId.AsString(),
                    SocketProtectionLevel.BluetoothEncryptionWithAuthentication);

                // Makes the service visible to remote Bluetooth devices that perform a service discovery scan
                Provider.StartAdvertising(Listener);

                ReceivedFile receivedFile = null;

                Listener.ConnectionReceived += async (sender, args) =>
                {
                    try
                    {
                        using (var reader = new DataReader(args.Socket.InputStream))
                        using (var writer = new DataWriter(args.Socket.OutputStream))
                        {
                            reader.InputStreamOptions = InputStreamOptions.Partial;

                            // Read OBEX Connect Request
                            await reader.LoadAsync(64);
                            uint buffer = reader.UnconsumedBufferLength;
                            byte[] connectRequest = new byte[buffer];
                            reader.ReadBytes(connectRequest);

                            if (connectRequest[0] != 0x80) // Connect OpCode
                            {
                                // Send Connect Response
                                byte[] failResponse = new byte[] {
                                    0xC0, // Response code (Bad Request)
                                    0x00, 0x07, // Packet length
                                    0x10, // OBEX version
                                    0x00, // Flags
                                    0x20, 0x00 // Max packet length (8192)
                                };
                                writer.WriteBytes(failResponse);
                                await writer.StoreAsync();
                                throw new Exception("Invalid OBEX connect request");
                            }


                            // Send Connect Response
                            byte[] connectResponse = new byte[] {
                                0xA0, // Response code (Success)
                                0x00, 0x07, // Packet length
                                0x10, // OBEX version
                                0x00, // Flags
                                0x20, 0x00 // Max packet length (8192)
                            };
                            writer.WriteBytes(connectResponse);
                            await writer.StoreAsync();

                            // Process PUT request
                            string fileName = "";
                            long fileSize = 0;
                            MemoryStream fileContent = new MemoryStream();
                            int packetCount = 0;

                            // Loop to receive and process all PUT packets
                            while (true)
                            {
                                await reader.LoadAsync(3);
                                byte[] header = new byte[3];
                                reader.ReadBytes(header);

                                switch (header[0])
                                {
                                    
                                    case 0x02:  // Valid PUT-requests
                                    case 0x82:
                                        break;

                                    case 0xFF:  // Handle ABORT request
                                        byte[] OKResponse = new byte[] {
                                            0xA0,
                                            0x00, 0x03
                                        };
                                        writer.WriteBytes(OKResponse);
                                        await writer.StoreAsync();
                                        throw new Exception($"Current operation aborted by client");

                                    case 0x03:  // Handle GET-request with NOT FOUND
                                        byte[] NotFoundResponse = new byte[] {
                                            0x44,
                                            0x00, 0x03
                                        };
                                        writer.WriteBytes(NotFoundResponse);
                                        await writer.StoreAsync();
                                        throw new Exception($"Client attempted a GET request, which is not supported");

                                    default:    // Other, unknown headers
                                        throw new Exception($"Invalid OBEX operation, received header 0x{BitConverter.ToString(header, 0, 1)}");
                                }
                                
                                int packetLength = (header[1] << 8) | header[2];
                                await reader.LoadAsync((uint)packetLength - 3);

                                // Process headers
                                while (reader.UnconsumedBufferLength > 0)
                                {
                                    byte headerId = reader.ReadByte();
                                    switch (headerId)
                                    {
                                        case 0x01: // Name
                                            ushort nameLength = reader.ReadUInt16();
                                            byte[] nameBytes = new byte[nameLength - 3];
                                            reader.ReadBytes(nameBytes);
                                            // Convert from UTF-16BE to string
                                            fileName = System.Text.Encoding.BigEndianUnicode.GetString(nameBytes);
                                            break;

                                        case 0xC3: // Length
                                            fileSize = (reader.ReadByte() << 24) |
                                                     (reader.ReadByte() << 16) |
                                                     (reader.ReadByte() << 8) |
                                                     reader.ReadByte();
                                            break;

                                        case 0x48: // Body
                                        case 0x49: // End of Body
                                            ushort bodyLength = reader.ReadUInt16();
                                            uint currentBufferLength = reader.UnconsumedBufferLength;

                                            // If reported header length fails to match read buffer length, the body is sent/loaded in multiple parts but considered one packet
                                            if (currentBufferLength < bodyLength - 3)
                                            {
                                                uint writtenBytes = 0;
                                                do 
                                                {
                                                    currentBufferLength = reader.UnconsumedBufferLength;
                                                    byte[] partialBodyBytes = new byte[currentBufferLength];
                                                    reader.ReadBytes(partialBodyBytes);
                                                    await fileContent.WriteAsync(partialBodyBytes, 0,
                                                        partialBodyBytes.Length);
                                                    progress?.Report((double)fileContent.Length / fileSize);

                                                    writtenBytes += currentBufferLength;

                                                    // Never load data that would be part of the next packet
                                                    if(bodyLength - 3 - writtenBytes > 0)
                                                        await reader.LoadAsync((uint)(bodyLength - 3 - writtenBytes));

                                                } while (writtenBytes < bodyLength - 3);
                                            }
                                            else
                                            {
                                                byte[] bodyBytes = new byte[bodyLength - 3];
                                                reader.ReadBytes(bodyBytes);
                                                await fileContent.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                                                progress?.Report((double)fileContent.Length / fileSize);
                                            }
                                            break;

                                        default:
                                            // Skip unknown headers
                                            ushort length = reader.ReadUInt16();
                                            reader.ReadBytes(new byte[length - 3]);
                                            break;
                                    }
                                }

                                packetCount++;
                                
                                if (packetCount == 1)
                                {
                                    // Contains the user response (accepted/declined) to the incoming transfer request
                                    UserResponseTask = new TaskCompletionSource<bool>();

                                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                                        Windows.UI.Core.CoreDispatcherPriority.Normal,
                                        () =>
                                        {
                                            // Call method to show the prompt
                                            IncomingFileTransferRequested?.Invoke(this, (fileName, fileSize));
                                        }
                                    );

                                    bool userAccepted = await UserResponseTask.Task;
                                    if (!userAccepted)
                                    {
                                        // Send rejection response
                                        byte[] rejectResponse = new byte[] {
                                            0xC3, // Forbidden - operation is understood but refused
                                            0x00, 0x03 // Length
                                        };
                                        writer.WriteBytes(rejectResponse);
                                        await writer.StoreAsync();
                                        throw new Exception("File transfer declined.");
                                    }
                                }
                                
                                byte[] response = new byte[] {
                                    0x90, // Continue
                                    0x00, 0x03 // Length
                                };
                                switch (header[0])
                                {
                                    case 0x02:
                                        // Send continue response
                                        writer.WriteBytes(response);
                                        await writer.StoreAsync();
                                        break;

                                    case 0x82:
                                        // Send success response
                                        response[0] = 0xA0;
                                        writer.WriteBytes(response);
                                        await writer.StoreAsync();
                                        break;
                                }

                                if (header[0] == 0x82)
                                    break;
                            }

                            // File name should have a trailing string terminator character "\0"
                            // that needs to be removed for it to be handled correctly
                            string trimmedFileName = fileName.TrimEnd(char.MinValue);
                            receivedFile = new ReceivedFile(trimmedFileName, fileContent);

                            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                                Windows.UI.Core.CoreDispatcherPriority.Normal,
                                () =>
                                {
                                    IncomingFileTransferCompleted?.Invoke(this,
                                        (receivedFile, "File transfer successful."));
                                });

                        }
                    }
                    catch (Exception ex)
                    {
                        // Handle transfer errors, this is an async void function so exceptions thrown here will not be propagated and caught
                        Dispose();
                        await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                            Windows.UI.Core.CoreDispatcherPriority.Normal,
                            () =>
                            { 
                                // Update UI to show the error
                                IncomingFileTransferCompleted?.Invoke(this, (null, ex.Message));
                            });
                    }
                };
            }
            catch (Exception ex)
            {
                Dispose();
                throw new Exception($"File receive failed: {ex.Message}", ex);
            }
        }

        public void StopListeningForFileTransferConnection()
        {
            Dispose();
        }

        public void Dispose()
        {
            Socket?.Dispose();
            Socket = null;

            RfcommService?.Dispose();
            RfcommService = null;

            Provider?.StopAdvertising();
            Provider = null;

            Listener?.Dispose();
            Listener = null;
        }
    }

    public class ReceivedFile
    {
        public string FileNameWithExtension;
        public MemoryStream FileContent;

        public ReceivedFile(string FileName, MemoryStream FileContent)
        {
            FileNameWithExtension = FileName;
            this.FileContent = FileContent;
        }
    }
}