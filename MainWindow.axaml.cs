using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ModbusLibrary.Master;
using ModbusLibrary.Transport;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ModbusTestAvalonia
{
    public partial class MainWindow : Window
    {
        private SlaveDevice? _activeDevice;
        private TrafficWindow _trafficWindow = new TrafficWindow();

        public ObservableCollection<SlaveDevice> DeviceList { get; set; } = new ObservableCollection<SlaveDevice>();
        public ObservableCollection<LogEntry> LogData { get; set; } = new ObservableCollection<LogEntry>();

        // Function/data type ComboBox index -> string mappings (for use in the background without displaying the screen)
        private static readonly string[] FunctionNames = { "Coil", "Input", "Register", "InpReg" };

        private static readonly string[] DataTypeNames = { "Signed", "Unsigned", "Hex", "Binary", "Float", "Float inverse", "Double", "Double Inverse", "Long", "Long Inverse" };

        private class SharedConnection
        {
            public ITransport Transport = null!;
            public ModbusMaster Master = null!;
            public int RefCount = 0;
            public SemaphoreSlim Gate = new SemaphoreSlim(1, 1); // queues simultaneous connects AND all read/write traffic on the shared line
        }

        private readonly Dictionary<string, SharedConnection> _sharedConnections = new();
        private readonly object _sharedConnLock = new object();

        private string BuildConnectionKey(string connType, string ipOrCom, string portOrBaud,
            int dataBits, System.IO.Ports.Parity parity, System.IO.Ports.StopBits stopBits)
        {
            if (connType == "Serial Port (RTU)")
                return $"SERIAL|{ipOrCom}|{portOrBaud}|{dataBits}|{parity}|{stopBits}";

            return $"{connType}|{ipOrCom}|{portOrBaud}";
        }

        // --- APPLICATION-LEVEL TIMEOUT WRAPPERS ---
        // The underlying transport has no read/write timeout of its own, so a request that never
        // gets a response would hang forever and permanently hold the shared CommGate. These wrappers
        // race the real call against a timer and throw TimeoutException if the timer wins.
        private static async Task<T> WithTimeout<T>(Task<T> task, int timeoutMs, string opName)
        {
            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(task, timeoutTask);

            if (completed == timeoutTask)
                throw new TimeoutException($"{opName} timed out after {timeoutMs}ms (no response from device).");

            return await task; // propagate real result or real exception
        }

        private static async Task WithTimeout(Task task, int timeoutMs, string opName)
        {
            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(task, timeoutTask);

            if (completed == timeoutTask)
                throw new TimeoutException($"{opName} timed out after {timeoutMs}ms (no response from device).");

            await task; // propagate real exception if any
        }

        public MainWindow()
        {
            InitializeComponent();
            txtPollInterval = this.FindControl<TextBox>("txtPollInterval")!;

            dataGridViewRegisters.ItemsSource = null; // will be set when the active device is selected below
            lstDevices.ItemsSource = DeviceList;
            lstLogs.ItemsSource = LogData;

            cmbDataType.SelectionChanged += CmbDataType_SelectionChanged;
            cmbFunction.SelectionChanged += CmbFunction_SelectionChanged;

            // We capture the Traffic event launched from the library and add it to the list.
            ModbusLibrary.Utils.ModbusTrafficLogger.OnTraffic = (data, isTx) =>
            {
                // HERE'S THE MIRACLE LINE: If the user presses the Stop button, the UI immediately stops logging!
                // (But the devices continue communicating with each other in the background)
                if (TrafficWindow.IsPaused) return;

                if (data == null || data.Length == 0) return;

                string direction = isTx ? "Tx" : "Rx";
                string hexFormat = BitConverter.ToString(data).Replace("-", " ");

                string logLine = $"{TrafficWindow.LogCounter:D6}-{direction}:00 {hexFormat}";

                Dispatcher.UIThread.Post(() =>
                {
                    TrafficWindow.TrafficLogs.Add(logLine);
                    TrafficWindow.LogCounter++;

                    if (TrafficWindow.TrafficLogs.Count > 1000)
                        TrafficWindow.TrafficLogs.RemoveAt(0);
                });
            };
        }
        private void BtnTraffic_Click(object sender, RoutedEventArgs e)
        {
            // We're just making it visible because the window was created beforehand.
            _trafficWindow.Show(this);
        }
        private void CmbDataType_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_activeDevice != null)
            {
                // Write the new selection on the screen to the active device's memory. CmbConnectionType_SelectionChanged
                _activeDevice.SelectedDataTypeIndex = cmbDataType.SelectedIndex;
            }
        }

        private void CmbFunction_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_activeDevice != null)
            {
                // Write the new selection on the screen to the memory of the active device.
                _activeDevice.SelectedFunctionIndex = cmbFunction.SelectedIndex;
            }
        }

        // --- CONNECT / DISCONNECT ---
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDevice == null) { AddLog("Warning: No device selected."); return; }

            if (btnConnect.Content?.ToString() == "Disconnect")
            {
                StopDevicePolling(_activeDevice);
                ReleaseSharedConnection(_activeDevice);
                _activeDevice.IsConnected = false;
                btnConnect.Content = "Connect";
                AddLog($"{_activeDevice.Name}: Disconnected manually.");
                return;
            }

            if (!byte.TryParse(txtSlaveId.Text, out _) ||
                !ushort.TryParse(txtStartAddress.Text, out _) ||
                !ushort.TryParse(txtQuantity.Text, out ushort q) || q <= 0)
            {
                AddLog("Warning: Please fix the fields before connecting.");
                return;
            }

            // Write the current settings on the screen to the device (synchronize before connecting)
            SyncScreenToDevice(_activeDevice);
            string? key = null;
            try
            {
                string ipOrCom = _activeDevice.IpAddress;
                int portOrBaud = int.Parse(_activeDevice.Port);
                string selectedConnection = (cmbConnectionType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Modbus TCP/IP";

                int selectedDataBits = cmbDataBits.SelectedIndex switch
                {
                    0 => 8,
                    1 => 7,
                    2 => 6,
                    3 => 5,
                    _ => 8
                };

                System.IO.Ports.Parity selectedParity = cmbParity.SelectedIndex switch
                {
                    0 => System.IO.Ports.Parity.None,
                    1 => System.IO.Ports.Parity.Odd,
                    2 => System.IO.Ports.Parity.Even,
                    3 => System.IO.Ports.Parity.Mark,
                    4 => System.IO.Ports.Parity.Space,
                    _ => System.IO.Ports.Parity.None
                };

                System.IO.Ports.StopBits selectedStopBits = cmbStopBits.SelectedIndex switch
                {
                    0 => System.IO.Ports.StopBits.One,
                    1 => System.IO.Ports.StopBits.OnePointFive,
                    2 => System.IO.Ports.StopBits.Two,
                    _ => System.IO.Ports.StopBits.One
                };

                key = BuildConnectionKey(selectedConnection, ipOrCom, portOrBaud.ToString(),
                                         selectedDataBits, selectedParity, selectedStopBits);
                SharedConnection shared;
                bool isNew;

                lock (_sharedConnLock)
                {
                    if (!_sharedConnections.TryGetValue(key, out shared!))
                    {
                        ITransport transport = selectedConnection switch
                        {
                            "Modbus TCP/IP" => new TcpTransport(),
                            "Modbus UDP/IP" => new UdpTransport(),
                            "Modbus RTU Over TCP/IP" => new RtuOverTcpTransport(),
                            "Modbus RTU Over UDP/IP" => new RtuOverUdpTransport(),
                            "Serial Port (RTU)" => new SerialTransport(selectedDataBits, selectedParity, selectedStopBits),
                            _ => new TcpTransport()
                        };

                        shared = new SharedConnection { Transport = transport };
                        _sharedConnections[key] = shared;
                        isNew = true;
                    }
                    else
                    {
                        isNew = false;
                    }
                }

                await shared.Gate.WaitAsync();
                try
                {
                    if (isNew)
                    {
                        await shared.Transport.ConnectAsync(ipOrCom, portOrBaud);
                        shared.Master = new ModbusMaster(shared.Transport);
                    }
                    shared.RefCount++;
                }
                finally
                {
                    shared.Gate.Release();
                }

                var device = _activeDevice;
                device.Transport = shared.Transport;
                device.Master = shared.Master;
                device.ConnectionKey = key;
                device.CommGate = shared.Gate;
                device.IsConnected = true;

                btnConnect.Content = "Disconnect";
                AddLog($"{device.Name}: Connected via {selectedConnection} (shared conn refcount={shared.RefCount})");

                StartDevicePolling(device);
            }
            catch (Exception ex)
            {
                if (key != null)
                {
                    lock (_sharedConnLock)
                    {
                        if (_sharedConnections.TryGetValue(key, out var s) && s.RefCount == 0)
                            _sharedConnections.Remove(key);
                    }
                }
                _activeDevice.IsConnected = false;
                btnConnect.Content = "Connect";
                AddLog($"{_activeDevice.Name}: Connection Error - device offline or unreachable: {ex.Message}");
            }
        }
        private void ReleaseSharedConnection(SlaveDevice device)
        {
            if (string.IsNullOrEmpty(device.ConnectionKey))
            {
                device.Transport?.Disconnect();
                device.CommGate = null;
                return;
            }

            lock (_sharedConnLock)
            {
                if (_sharedConnections.TryGetValue(device.ConnectionKey, out var shared))
                {
                    shared.RefCount--;
                    AddLog($"{device.Name}: Shared connection refcount now {shared.RefCount}.");

                    if (shared.RefCount <= 0)
                    {
                        shared.Transport.Disconnect();
                        _sharedConnections.Remove(device.ConnectionKey);
                    }
                }
            }

            device.ConnectionKey = null;
            device.Transport = null;
            device.Master = null;
            device.CommGate = null;
        }


        // --- BACKGROUND POLL CYCLE FOR EACH DEVICE ---
        private void StartDevicePolling(SlaveDevice device)
        {
            StopDevicePolling(device); // cancel the old one if it exists

            var cts = new CancellationTokenSource();
            device.PollCts = cts;

            if (!int.TryParse(device.PollInterval, out int interval) || interval <= 0)
                interval = 1000;

            _ = DevicePollingLoop(device, interval, cts.Token);
        }

        private void StopDevicePolling(SlaveDevice device)
        {
            device.PollCts?.Cancel();
            device.PollCts = null;
        }

        private async Task DevicePollingLoop(SlaveDevice device, int intervalMs, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PollDeviceOnce(device, token);
                }
                catch (Exception)
                {
                    if (device.IsConnected)
                    {
                        AddLog($"{device.Name}: Reading Error - Connection lost.");
                        ReleaseSharedConnection(device);
                        device.IsConnected = false;

                        if (_activeDevice == device)
                        {
                            Dispatcher.UIThread.Post(() => btnConnect.Content = "Connect");
                        }
                    }
                    break; // finish this device loop
                }

                try { await Task.Delay(intervalMs, token); }
                catch (TaskCanceledException) { break; }
            }
        }

        // Single round of reading — only looks at the device's own settings, NOT the textboxes on the screen
        private async Task PollDeviceOnce(SlaveDevice device, CancellationToken token)
        {
            if (device.Master == null || !device.IsConnected) return;

            if (!byte.TryParse(device.SlaveId.ToString(), out byte slaveId)) return;
            if (!ushort.TryParse(device.StartAddress, out ushort startAddress)) return;
            if (!ushort.TryParse(device.Quantity, out ushort quantity) || quantity == 0) return;

            var master = device.Master;
            int funcIndex = device.SelectedFunctionIndex;

            var gate = device.CommGate; 
            if (gate == null) return;

            bool lockAcquired = await gate.WaitAsync(2000, token);

            if (!lockAcquired)
            {
                AddLog($"Warning: {device.Name} could not acquire the communication line (Timeout).");
                return;
            }

            try
            {
                if (funcIndex == 0 || funcIndex == 1)
                {
                    ushort maxCoilRead = 2000;
                    bool[] allBitValues = new bool[quantity];
                    ushort remaining = quantity, currentStart = startAddress;
                    int destIndex = 0;

                    while (remaining > 0)
                    {
                        ushort readCount = remaining > maxCoilRead ? maxCoilRead : remaining;
                        bool[] chunk = funcIndex == 0
                            ? await WithTimeout(master.ReadCoilsAsync(slaveId, currentStart, readCount), 3000, "ReadCoils")
                            : await WithTimeout(master.ReadDiscreteInputsAsync(slaveId, currentStart, readCount), 3000, "ReadDiscreteInputs");

                        Array.Copy(chunk, 0, allBitValues, destIndex, chunk.Length);
                        remaining -= readCount; currentStart += readCount; destIndex += readCount;
                    }

                    UpdateDeviceGrid(device, startAddress, quantity, 1, funcIndex, allBitValues, null, "");
                }
                else
                {
                    ushort maxRegRead = 125;
                    ushort[] allValues = new ushort[quantity];
                    ushort remaining = quantity, currentStart = startAddress;
                    int destIndex = 0;

                    string selectedType = DataTypeNames.ElementAtOrDefault(device.SelectedDataTypeIndex) ?? "Unsigned";

                    while (remaining > 0)
                    {
                        ushort readCount = remaining > maxRegRead ? maxRegRead : remaining;
                        ushort[] chunk = funcIndex == 2
                            ? await WithTimeout(master.ReadHoldingRegistersAsync(slaveId, currentStart, readCount), 3000, "ReadHoldingRegisters")
                            : await WithTimeout(master.ReadInputRegistersAsync(slaveId, currentStart, readCount), 3000, "ReadInputRegisters");

                        Array.Copy(chunk, 0, allValues, destIndex, chunk.Length);
                        remaining -= readCount; currentStart += readCount; destIndex += readCount;
                    }

                    int step = ModbusLibrary.Utils.ModbusDataFormatter.GetStepForDataType(selectedType);
                    int expectedRows = step > 0 ? quantity / step : quantity;

                    UpdateDeviceGrid(device, startAddress, expectedRows, step, funcIndex, null, allValues, selectedType);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        // Updates the device's own RegisterData — Passing this to the UI thread via Dispatcher
        private void UpdateDeviceGrid(SlaveDevice device, int startAddress, int rowCount, int step, int funcIndex, bool[]? bitValues, ushort[]? regValues, string selectedType)
        {
            string prefix = FunctionNames.ElementAtOrDefault(funcIndex) ?? "Address";

            Dispatcher.UIThread.Post(() =>
            {
                bool needsRebuild = device.RegisterData.Count != rowCount
                    || device.LastGridFuncIndex != funcIndex
                    || device.LastGridStep != step;

                if (needsRebuild)
                {
                    device.RegisterData.Clear();
                    for (int i = 0; i < rowCount; i++)
                    {
                        device.RegisterData.Add(new RegisterRow
                        {
                            Address = $"{prefix}[{startAddress + (i * step)}]",
                            Value = "0",
                            RawAddress = startAddress + (i * step)
                        });
                    }

                    device.LastGridFuncIndex = funcIndex;
                    device.LastGridStep = step;
                }

                for (int i = 0; i < rowCount; i++)
                {
                    if (bitValues != null)
                        device.RegisterData[i].Value = bitValues[i] ? "1" : "0";
                    else if (regValues != null)
                        device.RegisterData[i].Value = ModbusLibrary.Utils.ModbusDataFormatter.FormatValue(regValues, i * step, selectedType);
                }
            });
        }

        // --- TEXTBOX FILTERS (unchanged) ---
        private void NumericTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrEmpty(textBox.Text))
            {
                string originalText = textBox.Text;
                string newText = new string(originalText.Where(char.IsDigit).ToArray());
                if (originalText != newText)
                {
                    textBox.Text = newText;
                    textBox.CaretIndex = newText.Length;
                }
            }
        }

        private void IpTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrEmpty(textBox.Text))
            {
                string originalText = textBox.Text;
                string newText = originalText;
                string? selectedConnection = (cmbConnectionType.SelectedItem as ComboBoxItem)?.Content?.ToString();

                newText = selectedConnection == "Serial Port (RTU)"
                    ? new string(originalText.Where(char.IsLetterOrDigit).ToArray())
                    : new string(originalText.Where(c => char.IsDigit(c) || c == '.').ToArray());

                if (originalText != newText)
                {
                    textBox.Text = newText;
                    textBox.CaretIndex = newText.Length;
                }
            }
        }

        private void WriteValueTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrEmpty(textBox.Text))
            {
                string originalText = textBox.Text;
                string newText = originalText;
                string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";

                if (selectedType == "Hex")
                {
                    // HEX: Numbers only, letters A-F and x/X
                    newText = new string(originalText.Where(c => char.IsDigit(c) || "abcdefABCDEFxX".Contains(c)).ToArray());
                    int maxLen = newText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 6 : 4;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else if (selectedType == "Binary")
                {
                    // BINARY: Only 0 and 1
                    newText = new string(originalText.Where(c => c == '0' || c == '1').ToArray());
                    if (newText.Length > 16) newText = newText.Substring(0, 16);
                }
                else if (selectedType.Contains("Float") || selectedType.Contains("Double"))
                {
                    // FLOAT / DOUBLE: Numbers, Minus (-), Period (.) and Comma (,)
                    newText = new string(originalText.Where(c => char.IsDigit(c) || c == '-' || c == '.' || c == ',').ToArray());

                    // 15 character limit for Float, 20 character limit for Double (including minus and decimal separators)
                    int maxLen = selectedType.Contains("Float") ? 15 : 20;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else if (selectedType.Contains("Long") || selectedType == "Signed")
                {
                    // LONG / SIGNED (Integers): Only digits and the minus (-) sign. Fractions are forbidden!
                    newText = new string(originalText.Where(c => char.IsDigit(c) || c == '-').ToArray());

                    // 6 character limit for Signed (16-bit), 20 character limit for Long (64-bit)
                    int maxLen = selectedType.Contains("Long") ? 11 : 6;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else
                {
                    // UNSIGNED (Default): Numbers only. Minus signs and commas are forbidden.
                    newText = new string(originalText.Where(char.IsDigit).ToArray());

                    // Unsigned 16-bit, maximum 65535 characters (5 characters)
                    if (newText.Length > 5) newText = newText.Substring(0, 5);
                }

                // Update the TextBox if incorrect/excess characters entered by the user are deleted.
                if (originalText != newText)
                {
                    textBox.Text = newText;
                    textBox.CaretIndex = newText.Length; // Move the cursor to the end
                }
            }
        }

        // --- WRITE (via active device, synchronous - does not affect polling because it is now a separate Task) ---
        private async void BtnWrite_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDevice?.Master == null || !_activeDevice.IsConnected)
            {
                AddLog("Warning: You must connect to a device first!");
                return;
            }

            int funcIndex = cmbFunction.SelectedIndex;
            if (funcIndex == 1 || funcIndex == 3)
            {
                AddLog("Error: The selected function is Read-Only. Data cannot be written.");
                return;
            }

            if (!ushort.TryParse(txtWriteAddress.Text, out ushort address))
            {
                AddLog("Error: Invalid write address.");
                return;
            }

            var device = _activeDevice;
            var master = device.Master;

            if (device.CommGate == null) return;

            bool lockAcquired = await device.CommGate.WaitAsync(3000);
            if (!lockAcquired)
            {
                AddLog($"Warning: {device.Name} could not acquire the communication line (Timeout).");
                return;
            }

            try
            {
                byte slaveId = byte.Parse(txtSlaveId.Text ?? "1");

                if (funcIndex == 0)
                {
                    string valueText = txtWriteValue.Text?.Trim().ToLower() ?? "0";
                    bool valueToWrite = valueText == "1" || valueText == "true";
                    await WithTimeout(master.WriteSingleCoilAsync(slaveId, address, valueToWrite), 3000, "WriteSingleCoil");
                    AddLog($"Success: {valueToWrite} was written to Coil[{address}].");
                }
                else if (funcIndex == 2)
                {
                    string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";
                    ushort[] registersToWrite;

                    // --- STEP 1: DATA TRANSFORMATION (LOCAL PROCESS) ---
                    // If an error occurs here, we will not disconnect the network; we will simply log the error and cancel the process.
                    try
                    {
                        if (selectedType is "Signed" or "Unsigned" or "Hex" or "Binary")
                        {
                            ushort valueToWrite = ModbusLibrary.Utils.ModbusDataFormatter.ParseRegisterValue(txtWriteValue.Text ?? "0", selectedType);
                            registersToWrite = new ushort[] { valueToWrite };
                        }
                        else
                        {
                            registersToWrite = ModbusLibrary.Utils.ModbusDataFormatter.BuildMultiRegisterValue(txtWriteValue.Text ?? "0", selectedType);
                        }
                    }
                    catch (Exception parseEx)
                    {
                        AddLog($"Format Error: '{selectedType}' could not be converted. Library Error: {parseEx.Message}");
                        return; // Cancel the operation without stopping the system! (finally still releases the gate)
                    }

                    // --- STEP 2: WRITING (COMMUNICATION) OVER THE NETWORK ---
                    if (registersToWrite.Length == 1)
                    {
                        await WithTimeout(master.WriteSingleRegisterAsync(slaveId, address, registersToWrite[0]), 3000, "WriteSingleRegister");
                        string formattedLogValue = ModbusLibrary.Utils.ModbusDataFormatter.FormatValue(registersToWrite, 0, selectedType);
                        AddLog($"Success: {formattedLogValue} was written to Register[{address}].");
                    }
                    else
                    {
                        await WithTimeout(master.WriteMultipleRegistersAsync(slaveId, address, registersToWrite), 3000, "WriteMultipleRegisters");
                        AddLog($"Success: {txtWriteValue.Text} ({selectedType}) was written to Register[{address}].");
                    }
                }
            }
            catch (TimeoutException tex)
            {
                // No response from the device at all -> the shared transport is now considered dead/unreliable.
                AddLog($"{device.Name}: {tex.Message} — connection will be reset.");
                StopDevicePolling(device);
                ReleaseSharedConnection(device);
                device.IsConnected = false;
                if (_activeDevice == device) btnConnect.Content = "Connect";
            }
            catch (Exception ex)
            {
                // ONLY THIS APPEARS IN CASE OF GENUINE NETWORK FAULTS
                AddLog($"Communication Error: {ex.Message}");
                if (device.IsConnected)
                {
                    StopDevicePolling(device);
                    ReleaseSharedConnection(device);
                    device.IsConnected = false;
                    if (_activeDevice == device) btnConnect.Content = "Connect";
                }
            }
            finally
            {
                device.CommGate.Release();
            }
        }

        // --- DEVICE MANAGEMENT ---
        private void BtnAddDevice_Click(object sender, RoutedEventArgs e)
        {
            if (byte.TryParse(txtNewDevId.Text, out byte id) && !string.IsNullOrWhiteSpace(txtNewDevName.Text))
            {
                string ip, port;
                if (cmbComPort.IsVisible)
                {
                    ip = cmbComPort.SelectedItem?.ToString() ?? "COM3";
                    port = cmbBaudRate.SelectedItem?.ToString() ?? "9600";
                }
                else
                {
                    ip = txtIpAddress.Text ?? "127.0.0.1";
                    port = txtPort.Text ?? "502";
                }
                var newDev = new SlaveDevice
                {
                    Name = txtNewDevName.Text,
                    SlaveId = id,
                    IpAddress = ip,
                    Port = port,
                    SelectedConnectionIndex = cmbConnectionType.SelectedIndex,
                    SelectedFunctionIndex = cmbFunction.SelectedIndex,
                    SelectedDataTypeIndex = cmbDataType.SelectedIndex,
                    StartAddress = txtStartAddress.Text ?? "0",
                    Quantity = txtQuantity.Text ?? "10",
                    PollInterval = txtPollInterval.Text ?? "1000",
                    SelectedDataBitsIndex = cmbDataBits.SelectedIndex,
                    SelectedParityIndex = cmbParity.SelectedIndex,
                    SelectedStopBitsIndex = cmbStopBits.SelectedIndex,
                };
                DeviceList.Add(newDev);
                AddLog($"New device added: {newDev.Name} (ID: {id})");
                txtNewDevName.Text = "";
                txtNewDevId.Text = "";
            }
            else
            {
                AddLog("Error: Invalid ID or Device Name!");
            }
        }

        private void BtnDeleteDevice_Click(object sender, RoutedEventArgs e)
        {
            if (lstDevices.SelectedItem is SlaveDevice dev)
            {
                StopDevicePolling(dev);
                if (dev.IsConnected)
                {
                    ReleaseSharedConnection(dev);
                    dev.IsConnected = false;
                }

                DeviceList.Remove(dev);
                AddLog($"Device deleted: {dev.Name}");

                if (_activeDevice == dev)
                {
                    _activeDevice = null;
                    dataGridViewRegisters.ItemsSource = null;
                    btnConnect.Content = "Connect";
                }
            }
        }

        // Writes the values ​​displayed on the screen (textbox/combobox) to the device's own settings.
        private void SyncScreenToDevice(SlaveDevice device)
        {
            if (byte.TryParse(txtSlaveId.Text, out byte id)) device.SlaveId = id;
            device.SelectedConnectionIndex = cmbConnectionType.SelectedIndex;
            device.SelectedFunctionIndex = cmbFunction.SelectedIndex;
            device.SelectedDataTypeIndex = cmbDataType.SelectedIndex;
            device.StartAddress = txtStartAddress.Text ?? "0";
            device.Quantity = txtQuantity.Text ?? "10";
            device.PollInterval = txtPollInterval.Text ?? "1000";
            device.SelectedDataBitsIndex = cmbDataBits.SelectedIndex;
            device.SelectedParityIndex = cmbParity.SelectedIndex;
            device.SelectedStopBitsIndex = cmbStopBits.SelectedIndex;

            if (cmbComPort.IsVisible)
            {
                device.IpAddress = cmbComPort.SelectedItem?.ToString() ?? "";
                device.Port = cmbBaudRate.SelectedItem?.ToString() ?? "9600";
            }
            else
            {
                device.IpAddress = txtIpAddress.Text ?? "";
                device.Port = txtPort.Text ?? "";
            }
        }

        private void LstDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Save the form fields from the old device (DO NOT TOUCH the linking/polling, let it continue in the background)
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is SlaveDevice oldDevice)
            {
                SyncScreenToDevice(oldDevice);

                // If the poll interval has changed and the device is connected, restart the running task with the new interval.
                if (oldDevice.IsConnected)
                {
                    StartDevicePolling(oldDevice); // Restart for interval update (optional but safe)
                }
            }

            if (lstDevices.SelectedItem is SlaveDevice selectedDevice)
            {
                _activeDevice = selectedDevice;

                cmbConnectionType.SelectedIndex = selectedDevice.SelectedConnectionIndex;
                if (selectedDevice.SelectedConnectionIndex == 1) // Serial Port (RTU)
                {
                    // CmbConnectionType_SelectionChanged has already been triggered and populated the list.
                    if (!string.IsNullOrEmpty(selectedDevice.IpAddress))
                        cmbComPort.SelectedItem = selectedDevice.IpAddress;

                    if (int.TryParse(selectedDevice.Port, out int baud))
                        cmbBaudRate.SelectedItem = baud;
                }
                else
                {
                    txtIpAddress.Text = selectedDevice.IpAddress;
                    txtPort.Text = selectedDevice.Port;
                }
                txtSlaveId.Text = selectedDevice.SlaveId.ToString();
                cmbFunction.SelectedIndex = selectedDevice.SelectedFunctionIndex;
                cmbDataType.SelectedIndex = selectedDevice.SelectedDataTypeIndex;
                cmbDataBits.SelectedIndex = selectedDevice.SelectedDataBitsIndex;
                cmbParity.SelectedIndex = selectedDevice.SelectedParityIndex;
                cmbStopBits.SelectedIndex = selectedDevice.SelectedStopBitsIndex;
                txtStartAddress.Text = selectedDevice.StartAddress;
                txtQuantity.Text = selectedDevice.Quantity;
                txtPollInterval.Text = selectedDevice.PollInterval;

                // Connect the grid to this device's OWN data — no separate clear/fill, data is already accumulating in the background.
                dataGridViewRegisters.ItemsSource = selectedDevice.RegisterData;

                if (selectedDevice.IsConnected && selectedDevice.Transport != null)
                {
                    btnConnect.Content = "Disconnect";
                    AddLog($"Switched to {selectedDevice.Name} (already connected, background polling continues).");
                }
                else
                {
                    btnConnect.Content = "Connect";
                    AddLog($"Switched to {selectedDevice.Name}, connecting...");
                    BtnConnect_Click(this, new RoutedEventArgs());
                }
            }
        }
        private async void DataGridViewRegisters_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (dataGridViewRegisters.SelectedItem is RegisterRow selectedRow)
            {
                if (_activeDevice?.Master == null || !_activeDevice.IsConnected) return;
                var device = _activeDevice;
                var master = device.Master;

                int funcIndex = cmbFunction.SelectedIndex;
                if (funcIndex == 1 || funcIndex == 3)
                {
                    AddLog("Warning: The selected function is Read-Only.");
                    return;
                }

                string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";
                var dialog = new EditRegisterWindow(selectedRow.Value, selectedType);
                await dialog.ShowDialog(this);

                if (dialog.IsConfirmed)
                {
                    if (device.CommGate == null) return;

                    bool lockAcquired = await device.CommGate.WaitAsync(2000);
                    if (!lockAcquired)
                    {
                        AddLog("Warning: Could not acquire the communication line (Timeout).");
                        return;
                    }
                    try
                    {
                        byte slaveId;
                        try { slaveId = byte.Parse(txtSlaveId.Text ?? "1"); } catch { slaveId = 1; }
                        ushort address = (ushort)selectedRow.RawAddress;

                        if (funcIndex == 0) // Write Coil
                        {
                            try
                            {
                                bool valueToWrite = dialog.InputValue.Trim().ToLower() is "1" or "true";
                                await WithTimeout(master.WriteSingleCoilAsync(slaveId, address, valueToWrite), 3000, "WriteSingleCoil");
                                AddLog($"Success: {valueToWrite} written to Coil[{address}].");
                            }
                            catch (Exception ex)
                            {
                                HandleCommError(device, address, ex);
                            }
                        }
                        else if (funcIndex == 2) // Write Register
                        {
                            ushort[] registersToWrite;

                            // --- STEP 1: DATA TRANSFORMATION (LOCAL PROCESS) ---
                            try
                            {
                                if (selectedType is "Signed" or "Unsigned" or "Hex" or "Binary")
                                {
                                    ushort valueToWrite = ModbusLibrary.Utils.ModbusDataFormatter.ParseRegisterValue(dialog.InputValue, selectedType);
                                    registersToWrite = new ushort[] { valueToWrite };
                                }
                                else
                                {
                                    registersToWrite = ModbusLibrary.Utils.ModbusDataFormatter.BuildMultiRegisterValue(dialog.InputValue, selectedType);
                                }
                            }
                            catch (Exception parseEx)
                            {
                                // Conversion error: Disconnect, just log
                                AddLog($"Format Error: Failed to parse '{selectedType}'. Library Error: {parseEx.Message}");
                                return; // finally still releases the gate
                            }

                            // --- STEP 2: WRITING OVER THE NETWORK ---
                            try
                            {
                                if (registersToWrite.Length == 1)
                                {
                                    await WithTimeout(master.WriteSingleRegisterAsync(slaveId, address, registersToWrite[0]), 3000, "WriteSingleRegister");
                                    string formattedLogValue = ModbusLibrary.Utils.ModbusDataFormatter.FormatValue(registersToWrite, 0, selectedType);
                                    AddLog($"Success: {formattedLogValue} written to Register[{address}].");
                                }
                                else
                                {
                                    await WithTimeout(master.WriteMultipleRegistersAsync(slaveId, address, registersToWrite), 3000, "WriteMultipleRegisters");
                                    AddLog($"Success: {dialog.InputValue} ({selectedType}) written to Register[{address}].");
                                }
                            }
                            catch (Exception commEx)
                            {
                                HandleCommError(device, address, commEx);
                            }
                        }
                    }
                    finally
                    {
                        device.CommGate.Release();
                    }
                }
            }
        }

        // To keep the code clean, we moved network error handling to a small helper method.
        private void HandleCommError(SlaveDevice device, ushort address, Exception ex)
        {
            AddLog($"Communication Error while writing to Address [{address}]: {ex.Message}");
            if (device.IsConnected)
            {
                StopDevicePolling(device);
                ReleaseSharedConnection(device);
                device.IsConnected = false;
                if (_activeDevice == device) btnConnect.Content = "Connect";
            }
        }

        private void AddLog(string message)
        {
            string finalMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            string color = "Lime";

            if (message.Contains("Error", StringComparison.OrdinalIgnoreCase)) color = "Red";
            else if (message.Contains("Warning", StringComparison.OrdinalIgnoreCase)) color = "Yellow";

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                LogData.Add(new LogEntry { Message = finalMessage, Color = color });
            });
        }

        private static readonly int[] BaudRates = { 300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 38400, 56000, 57600, 115200, 128000, 256000 };

        private void CmbConnectionType_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (lblIpAddress == null || lblPort == null || txtIpAddress == null || txtPort == null || cmbConnectionType == null) return;

            if (_activeDevice != null && _activeDevice.IsConnected)
            {
                StopDevicePolling(_activeDevice);
                ReleaseSharedConnection(_activeDevice);
                _activeDevice.IsConnected = false;

                btnConnect.Content = "Connect";
                AddLog("Connection type changed, previous connection closed.");
            }

            string? selectedConnection = (cmbConnectionType.SelectedItem as ComboBoxItem)?.Content?.ToString();

            if (selectedConnection == "Serial Port (RTU)")
            {
                lblIpAddress.Text = "COM Port:";
                lblPort.Text = "Baud Rate:";

                txtIpAddress.IsVisible = false;
                txtPort.IsVisible = false;
                cmbComPort.IsVisible = true;
                cmbBaudRate.IsVisible = true;

                cmbComPort.ItemsSource = System.IO.Ports.SerialPort.GetPortNames();
                if (cmbComPort.SelectedIndex < 0 && cmbComPort.ItemCount > 0)
                    cmbComPort.SelectedIndex = 0;

                if (cmbBaudRate.ItemsSource == null)
                {
                    cmbBaudRate.ItemsSource = BaudRates;
                    cmbBaudRate.SelectedItem = 9600;
                }

                pnlSerialSettings.IsVisible = true;
            }
            else
            {
                lblIpAddress.Text = "IP:";
                lblPort.Text = "Port:";

                txtIpAddress.IsVisible = true;
                txtPort.IsVisible = true;
                txtIpAddress.Text = "127.0.0.1";
                txtPort.Text = "502";

                cmbComPort.IsVisible = false;
                cmbBaudRate.IsVisible = false;

                pnlSerialSettings.IsVisible = false;
            }
        }
    }

    public class RegisterRow : INotifyPropertyChanged
    {
        public int RawAddress { get; set; }
        private string _address = string.Empty;
        private string _value = string.Empty;

        public string Address { get => _address; set { _address = value; OnPropertyChanged(); } }
        public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class SlaveDevice : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public byte SlaveId { get; set; }
        public string IpAddress { get; set; } = "127.0.0.1";
        public string Port { get; set; } = "502";
        public int SelectedConnectionIndex { get; set; } = 0;
        public int SelectedFunctionIndex { get; set; } = 2;
        public int SelectedDataTypeIndex { get; set; } = 1;
        public string StartAddress { get; set; } = "0";
        public string Quantity { get; set; } = "10";
        public string PollInterval { get; set; } = "1000";

        public int SelectedDataBitsIndex { get; set; } = 0;
        public int SelectedParityIndex { get; set; } = 0;
        public int SelectedStopBitsIndex { get; set; } = 0;

        [JsonIgnore] public ITransport? Transport { get; set; }
        [JsonIgnore] public ModbusMaster? Master { get; set; }
        [JsonIgnore] public string? ConnectionKey { get; set; }

        [JsonIgnore] public CancellationTokenSource? PollCts { get; set; }
        [JsonIgnore] public int LastGridFuncIndex { get; set; } = -1;
        [JsonIgnore] public int LastGridStep { get; set; } = -1;
        [JsonIgnore] public SemaphoreSlim? CommGate { get; set; }

        // Each device has its own register grid data — the grid is linked to this.
        [JsonIgnore] public ObservableCollection<RegisterRow> RegisterData { get; } = new();

        private bool _isConnected = false;
        [JsonIgnore]
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }


        [JsonIgnore]
        public string StatusColor => IsConnected ? "LimeGreen" : "Red";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class LogEntry
    {
        public string Message { get; set; } = string.Empty;
        public string Color { get; set; } = "Lime";
    }
}