using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ModbusLibrary.Master;
using ModbusLibrary.Transport;

namespace ModbusTestAvalonia
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer timerPoll;
        private ModbusMaster? _master;
        private ITransport? _transport;
        private bool _isConnected = false;
        



        public ObservableCollection<RegisterRow> RegisterData { get; set; } = new ObservableCollection<RegisterRow>();
        public ObservableCollection<SlaveDevice> DeviceList { get; set; } = new ObservableCollection<SlaveDevice>();

        // List to hold the colored log lines
        public ObservableCollection<LogEntry> LogData { get; set; } = new ObservableCollection<LogEntry>();

        public MainWindow()
        {

            InitializeComponent();
            txtPollInterval = this.FindControl<TextBox>("txtPollInterval")!;

            timerPoll = new DispatcherTimer();
            timerPoll.Interval = TimeSpan.FromMilliseconds(1000);
            timerPoll.Tick += TimerPoll_Tick;

            dataGridViewRegisters.ItemsSource = RegisterData;
            lstDevices.ItemsSource = DeviceList;
            lstLogs.ItemsSource = LogData; // We linked the log list

            

            cmbFunction.DropDownOpened += (s, e) => { if (_isConnected) timerPoll.Stop(); };
            cmbFunction.DropDownClosed += (s, e) => { if (_isConnected) timerPoll.Start(); };
            cmbDataType.DropDownOpened += (s, e) => { if (_isConnected) timerPoll.Stop(); };
            cmbDataType.DropDownClosed += (s, e) => { if (_isConnected) timerPoll.Start(); };
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                AddLog("Old connection closed, reconnecting...");
                await System.Threading.Tasks.Task.Delay(500); 
            }
            if (!_isConnected)
            {
                if (!byte.TryParse(txtSlaveId.Text, out _) ||
                    !ushort.TryParse(txtStartAddress.Text, out _) ||
                    !ushort.TryParse(txtQuantity.Text, out ushort q) || q <= 0)
                {
                    AddLog("Warning: Please fix the Slave ID, Start Address, or Quantity fields before connecting.");
                    return; // Bağlantı işlemini başlatmadan iptal et
                }
                try
                {
                    string ipOrCom = txtIpAddress.Text ?? "127.0.0.1";
                    int portOrBaud = int.Parse(txtPort.Text ?? "502");
                    string selectedConnection = (cmbConnectionType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Modbus TCP/IP";

                    if (selectedConnection == "Modbus TCP/IP")
                    {
                        _transport = new TcpTransport();
                        await _transport.ConnectAsync(ipOrCom, portOrBaud);
                    }
                    else if (selectedConnection == "Modbus UDP/IP")
                    {
                        _transport = new UdpTransport();
                        await _transport.ConnectAsync(ipOrCom, portOrBaud);
                    }
                    else if (selectedConnection == "Modbus RTU Over TCP/IP")
                    {
                        _transport = new RtuOverTcpTransport();
                        await _transport.ConnectAsync(ipOrCom, portOrBaud);
                    }
                    else if (selectedConnection == "Modbus RTU Over UDP/IP")
                    {
                        _transport = new RtuOverUdpTransport();
                        await _transport.ConnectAsync(ipOrCom, portOrBaud);
                    }
                    else if (selectedConnection == "Serial Port (RTU)")
                    {
                        _transport = new SerialTransport();
                        await _transport.ConnectAsync(ipOrCom, portOrBaud);
                    }

                    _master = new ModbusMaster(_transport);

                    _isConnected = true;
                    btnConnect.Content = "Disconnect";

                    AddLog($"Connection is Correct via {selectedConnection}");
                    if (int.TryParse(txtPollInterval.Text, out int interval))
                    {
                        timerPoll.Interval = TimeSpan.FromMilliseconds(interval);
                    }
                    timerPoll.Start();
                }
                catch (Exception ex)
                {
                    AddLog("Connection Error: Unable to establish a connection with the target device.");
                }
            }
            else
            {
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                btnConnect.Content = "Connect";
                AddLog("The connection was lost.");
            }
        }

        private async void TimerPoll_Tick(object? sender, EventArgs e)
        {
            if (_master == null || !_isConnected) return;
            if (!byte.TryParse(txtSlaveId.Text, out byte slaveId))
            {
                AddLog("Error: Invalid or empty Slave ID! Connection closed.");
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                btnConnect.Content = "Connect";
                return;
            }

            if (!ushort.TryParse(txtStartAddress.Text, out ushort startAddress))
            {
                AddLog("Error: Invalid or empty Start Address! Connection closed.");
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                btnConnect.Content = "Connect";
                return;
            }

            if (!ushort.TryParse(txtQuantity.Text, out ushort quantity) || quantity <= 0)
            {
                AddLog("Error: Quantity must be a valid number greater than 0! Connection closed.");
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                btnConnect.Content = "Connect";
                return;
            }

            try
            {
                //if (!byte.TryParse(txtSlaveId.Text, out byte slaveId)) return;
                //if (!ushort.TryParse(txtStartAddress.Text, out ushort startAddress)) return;
                //if (!ushort.TryParse(txtQuantity.Text, out ushort quantity) || quantity <= 0) return;

                int funcIndex = cmbFunction.SelectedIndex;

                if (funcIndex == 0 || funcIndex == 1)
                {
                    ushort maxCoilRead = 2000;
                    bool[] allBitValues = new bool[quantity];
                    ushort remaining = quantity;
                    ushort currentStart = startAddress;
                    int destIndex = 0;

                    while (remaining > 0)
                    {
                        ushort readCount = remaining > maxCoilRead ? maxCoilRead : remaining;
                        bool[] chunk = funcIndex == 0
                            ? await _master.ReadCoilsAsync(slaveId, currentStart, readCount)
                            : await _master.ReadDiscreteInputsAsync(slaveId, currentStart, readCount);

                        Array.Copy(chunk, 0, allBitValues, destIndex, chunk.Length);
                        remaining -= readCount;
                        currentStart += readCount;
                        destIndex += readCount;
                    }

                    UpdateGrid(startAddress, quantity, 1, funcIndex, allBitValues, null, "");
                }
                else
                {
                    ushort maxRegRead = 125;
                    ushort[] allValues = new ushort[quantity];
                    ushort remaining = quantity;
                    ushort currentStart = startAddress;
                    int destIndex = 0;

                    while (remaining > 0)
                    {
                        ushort readCount = remaining > maxRegRead ? maxRegRead : remaining;
                        ushort[] chunk = funcIndex == 2
                            ? await _master.ReadHoldingRegistersAsync(slaveId, currentStart, readCount)
                            : await _master.ReadInputRegistersAsync(slaveId, currentStart, readCount);

                        Array.Copy(chunk, 0, allValues, destIndex, chunk.Length);
                        remaining -= readCount;
                        currentStart += readCount;
                        destIndex += readCount;
                    }

                    string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";
                    int step = ModbusLibrary.Utils.ModbusDataFormatter.GetStepForDataType(selectedType);
                    int expectedRows = quantity / step;

                    UpdateGrid(startAddress, expectedRows, step, funcIndex, null, allValues, selectedType);
                }
            }
            catch (Exception ex)
            {
                AddLog("Reading Error: Connection lost. The remote host has closed the connection.");
                timerPoll.Stop();
                _isConnected = false;
                btnConnect.Content = "Connect";
            }
        }

        private void UpdateGrid(int startAddress, int rowCount, int step, int funcIndex, bool[]? bitValues, ushort[]? regValues, string selectedType)
        {
            string prefix = funcIndex switch
            {
                0 => "Coil",
                1 => "Input",
                2 => "Register",
                3 => "InpReg",
                _ => "Address"
            };

            if (RegisterData.Count != rowCount)
            {
                RegisterData.Clear();
                for (int i = 0; i < rowCount; i++)
                {
                    RegisterData.Add(new RegisterRow { Address = $"{prefix}[{startAddress + (i * step)}]", Value = "0" });
                }
            }

            for (int i = 0; i < rowCount; i++)
            {
                if (bitValues != null)
                {
                    RegisterData[i].Value = bitValues[i] ? "1" : "0";
                }
                else if (regValues != null)
                {
                    RegisterData[i].Value = ModbusLibrary.Utils.ModbusDataFormatter.FormatValue(regValues, i * step, selectedType);
                }
            }
        }

        // --- DATA WRITING OPERATION ---
        private async void BtnWrite_Click(object sender, RoutedEventArgs e)
        {
            if (_master == null || !_isConnected)
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

            bool wasPolling = timerPoll.IsEnabled;
            timerPoll.Stop();

            try
            {
                byte slaveId = byte.Parse(txtSlaveId.Text ?? "1");

                if (funcIndex == 0) // Write Coil (01)
                {
                    string valueText = txtWriteValue.Text?.Trim().ToLower() ?? "0";
                    bool valueToWrite = valueText == "1" || valueText == "true";
                    await _master.WriteSingleCoilAsync(slaveId, address, valueToWrite);
                    AddLog($"Success: {valueToWrite} was written to Coil[{address}].");
                }
                else if (funcIndex == 2) // Write Register (03)
                {
                    string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";

                    if (selectedType is "Signed" or "Unsigned" or "Hex" or "Binary")
                    {
                        ushort valueToWrite = ModbusLibrary.Utils.ModbusDataFormatter.ParseRegisterValue(txtWriteValue.Text ?? "0", selectedType);
                        await _master.WriteSingleRegisterAsync(slaveId, address, valueToWrite);
                        AddLog($"Success: {valueToWrite} was written to Register[{address}].");
                    }
                    else
                    {
                        ushort[] registersToWrite = ModbusLibrary.Utils.ModbusDataFormatter.BuildMultiRegisterValue(txtWriteValue.Text ?? "0", selectedType);
                        await _master.WriteMultipleRegistersAsync(slaveId, address, registersToWrite);
                        AddLog($"Success: Data ({selectedType}) was written to Register[{address}].");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("Writing Error: Failed to write data. The device might be disconnected or the address is invalid.");
            }
            finally
            {
                if (wasPolling) timerPoll.Start();
            }
        }

        // --- DEVICE MANAGEMENT ---
        private void BtnAddDevice_Click(object sender, RoutedEventArgs e)
        {
            if (byte.TryParse(txtNewDevId.Text, out byte id) && !string.IsNullOrWhiteSpace(txtNewDevName.Text))
            {
                var newDev = new SlaveDevice
                {
                    Name = txtNewDevName.Text,
                    SlaveId = id,
                    IpAddress = txtIpAddress.Text ?? "127.0.0.1",
                    Port = txtPort.Text ?? "502",
                    SelectedConnectionIndex = cmbConnectionType.SelectedIndex,
                    SelectedFunctionIndex = cmbFunction.SelectedIndex,
                    SelectedDataTypeIndex = cmbDataType.SelectedIndex,
                    StartAddress = txtStartAddress.Text ?? "0",
                    Quantity = txtQuantity.Text ?? "10"
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
                DeviceList.Remove(dev);
                AddLog($"Device deleted: {dev.Name}");
            }
        }

        private void LstDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isConnected)
            {
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                btnConnect.Content = "Connect";
                AddLog("Device switched. Disconnected from the previous device.");
            }
            // 1. Save the current UI values ​​of the (old) device whose selection has been changed to the object.
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is SlaveDevice oldDevice)
            {
                if (byte.TryParse(txtSlaveId.Text, out byte oldId)) oldDevice.SlaveId = oldId;
                oldDevice.IpAddress = txtIpAddress.Text ?? "";
                oldDevice.Port = txtPort.Text ?? "";
                oldDevice.SelectedConnectionIndex = cmbConnectionType.SelectedIndex;
                oldDevice.SelectedFunctionIndex = cmbFunction.SelectedIndex;
                oldDevice.SelectedDataTypeIndex = cmbDataType.SelectedIndex;
                oldDevice.StartAddress = txtStartAddress.Text ?? "0";
                oldDevice.Quantity = txtQuantity.Text ?? "10";
                oldDevice.PollInterval = txtPollInterval.Text ?? "1000";
            }

            // 2. Load the values ​​of the newly selected device into the UI.
            if (lstDevices.SelectedItem is SlaveDevice selectedDevice)
            {
                cmbConnectionType.SelectedIndex = selectedDevice.SelectedConnectionIndex;
                txtIpAddress.Text = selectedDevice.IpAddress;
                txtPort.Text = selectedDevice.Port;

                txtSlaveId.Text = selectedDevice.SlaveId.ToString();
                cmbFunction.SelectedIndex = selectedDevice.SelectedFunctionIndex;
                cmbDataType.SelectedIndex = selectedDevice.SelectedDataTypeIndex;
                txtStartAddress.Text = selectedDevice.StartAddress;
                txtQuantity.Text = selectedDevice.Quantity;
                txtPollInterval.Text=selectedDevice.PollInterval;

                AddLog($"Device changed: {selectedDevice.Name}");
            }
        }

        // --- COLOR LOG ALGORITHM ---
        private void AddLog(string message)
        {
            string finalMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            string color = "Lime"; // Default Green

            // Color assignment based on message content
            if (message.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                color = "Red";
            }
            else if (message.Contains("Warning", StringComparison.OrdinalIgnoreCase))
            {
                color = "Yellow";
            }

            // We're adding it via Dispatcher so the interface doesn't freeze.
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                LogData.Add(new LogEntry { Message = finalMessage, Color = color });
            });
        }
        private void CmbConnectionType_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (lblIpAddress == null || lblPort == null || txtIpAddress == null || txtPort == null || cmbConnectionType == null) return;

            
            if (_isConnected)
            {
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                
                btnConnect.Content = "Connect";
                AddLog("Connection type changed, previous connection closed.");
            }

            string? selectedConnection = (cmbConnectionType.SelectedItem as ComboBoxItem)?.Content?.ToString();

            if (selectedConnection == "Serial Port (RTU)")
            {
                lblIpAddress.Text = "COM Port:";
                txtIpAddress.Text = "COM3";
                lblPort.Text = "Baud Rate:";
                txtPort.Text = "9600";
            }
            else
            {
                lblIpAddress.Text = "IP:";
                txtIpAddress.Text = "127.0.0.1";
                lblPort.Text = "Port:";
                txtPort.Text = "502";
            }
        }
    }

    public class RegisterRow : INotifyPropertyChanged
    {
        private string _address = string.Empty;
        private string _value = string.Empty;

        public string Address
        {
            get => _address;
            set { _address = value; OnPropertyChanged(); }
        }

        public string Value
        {
            get => _value;
            set { _value = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class SlaveDevice
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
        public string PollInterval { get; set;  } = "1000";
    }
    

    // Data model for color logging
    public class LogEntry
    {
        public string Message { get; set; } = string.Empty;
        public string Color { get; set; } = "Lime";
    }
    
}