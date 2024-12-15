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

namespace UWPBluetoothTransfer
{
    public class BluetoothFileTransfer
    {
        private RfcommDeviceService RfcommService { get; set; }
        private StreamSocket Socket { get; set; }
        public BluetoothDevice SelectedDevice {get; set;}

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

                int serviceIndex = -1;
                for (int i = 0; i < result.Services.Count; i++)
                {
                    var name = result.Services[i].ServiceId.Uuid;
                    if (name.ToString().StartsWith("00001106") || name.ToString().StartsWith("00001105"))
                    {
                        serviceIndex = i;
                        break;
                    }
                    
                }

                if (serviceIndex == -1)
                {
                    throw new Exception("No file transfer RFCOMM services found on device.");
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
                        connectionId = BitConverter.ToUInt32(response.SkipWhile(x => x != 0xCB).Skip(1).Take(4).Reverse().ToArray(), 0);
                        
                        // Send PUT request
                        byte[] putHeader = new byte[] {
                            0x02, // PUT OpCode
                            0x00, 0x00 // Packet Length (to be filled)
                        };


                        // Name header
                        byte[] tempNameBytes = System.Text.Encoding.Unicode.GetBytes(spoofFileType ? Path.GetFileNameWithoutExtension(file.Name) : file.FileType);
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
                            (byte)(stream.Size >> 24), // The Length header is always 5 bytes total, 1 byte for the identifier + 4 bytes for the length value
                            (byte)(stream.Size >> 16),
                            (byte)(stream.Size >> 8),
                            (byte)stream.Size
                        };

                        byte[] connectionIdHeader = new byte[]
                        {
                            0xCB,
                            (byte)(connectionId >> 24),
                            (byte)(connectionId >> 16),
                            (byte)(connectionId >> 8),
                            (byte)connectionId
                        };
                        
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
                        while ((bodyBytes = await stream.AsStreamForRead().ReadAsync(buffer, 0, buffer.Length - firstPacketLength)) > 0)
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
                                packetLen = bodyBytes + bodyHeader.Length + putHeader.Length + connectionIdHeader.Length;
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
                            writer.WriteBytes(connectionIdHeader);

                            // Only write these if this is the first packet
                            if (chunkCount == 0)
                            {
                                
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
                                    throw new Exception($"OBEX transfer failed with code 0x{BitConverter.ToString(response, 0, 1)}: Unsupported media type");
                                }
                                throw new Exception($"OBEX transfer failed with code 0x{BitConverter.ToString(response, 0, 1)}");
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
                            //0xcb, 0x00, 0x00, 0x00, 0x01, // Connection ID
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

        public void Dispose()
        {
            if (Socket != null)
            {
                Socket.Dispose();
                Socket = null;
            }
            if (RfcommService != null)
            {
                RfcommService.Dispose();
                RfcommService = null;
            }
        }
    }
}