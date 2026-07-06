using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Linq;
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
        private bool _isSystemActive = false;

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
            // 1. KULLANICI SİSTEMİ KAPATMAK İSTİYORSA (Buton Disconnect iken tıklandıysa)
            if (btnConnect.Content?.ToString() == "Disconnect")
            {
                _isSystemActive = false; // Sistemi komple kapat
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                btnConnect.Content = "Connect";
                AddLog("System stopped manually.");
                return;
            }

            // 2. SİSTEMİ BAŞLATMA VEYA CİHAZ GEÇİŞİ YAPMA
            _isSystemActive = true;
            btnConnect.Content = "Disconnect"; // Sistemi çalışıyor (aktif) olarak göster

            if (_isConnected)
            {
                timerPoll.Stop();
                _transport?.Disconnect();
                _isConnected = false;
                await System.Threading.Tasks.Task.Delay(100);
            }

            if (!byte.TryParse(txtSlaveId.Text, out _) ||
                !ushort.TryParse(txtStartAddress.Text, out _) ||
                !ushort.TryParse(txtQuantity.Text, out ushort q) || q <= 0)
            {
                AddLog("Warning: Please fix the fields before connecting.");
                _isSystemActive = false;
                btnConnect.Content = "Connect";
                return;
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
                _isConnected = true; // Fiziksel bağlantı başarılı

                AddLog($"Connection is Correct via {selectedConnection}");
                if (int.TryParse(txtPollInterval.Text, out int interval))
                {
                    timerPoll.Interval = TimeSpan.FromMilliseconds(interval);
                }
                timerPoll.Start();
            }
            catch (Exception ex)
            {
                _isConnected = false; // Fiziksel bağlantı başarısız
                // DİKKAT: _isSystemActive değişkenini false YAPMIYORUZ! Sistem hala açık, sadece bu cihaz kapalı.
                AddLog("Connection Error: Target device is offline or unreachable.");
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
                if (_isConnected) // Sadece koptuğu an 1 kere çalışır, spamı önler
                {
                    AddLog("Reading Error: Connection lost. Device went offline.");
                    _transport?.Disconnect();
                    _isConnected = false; // Fiziksel bağlantıyı kestik
                    // DİKKAT: timerPoll.Stop() YAZMIYORUZ! Sistem çalışmaya devam etsin.
                }
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
                    RegisterData.Add(new RegisterRow
                    {
                        Address = $"{prefix}[{startAddress + (i * step)}]",
                        Value = "0",

                        RawAddress = startAddress + (i * step)
                    });
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
        // FILTER THAT ALLOWS ONLY NUMBERS
        private void NumericTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrEmpty(textBox.Text))
            {
                // Filter only digit characters (0-9)
                string originalText = textBox.Text;
                string newText = new string(originalText.Where(char.IsDigit).ToArray());

                // If a letter is deleted, update the text and move the cursor to the end.
                if (originalText != newText)
                {
                    textBox.Text = newText;
                    textBox.CaretIndex = newText.Length;
                }
            }
        }

        // Filter allowing letters, numbers, and dots for IP and COM ports.
        private void IpTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrEmpty(textBox.Text))
            {
                string originalText = textBox.Text;
                string newText = originalText;

                // Get the currently selected connection type
                string? selectedConnection = (cmbConnectionType.SelectedItem as ComboBoxItem)?.Content?.ToString();

                if (selectedConnection == "Serial Port (RTU)")
                {
                    // IF SERIAL PORT IS SELECTED: Allow only letters and numbers (e.g., COM3, ttyS0)
                    newText = new string(originalText.Where(char.IsLetterOrDigit).ToArray());
                }
                else
                {
                    // IF TCP/UDP IS SELECTED: Allow only numbers and periods (e.g., 127.0.0.1)
                    newText = new string(originalText.Where(c => char.IsDigit(c) || c == '.').ToArray());
                }

                if (originalText != newText)
                {
                    textBox.Text = newText;
                    textBox.CaretIndex = newText.Length; // Move cursor to the end
                }
            }
        }
        //Dynamic filter for Write Value Box
        private void WriteValueTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrEmpty(textBox.Text))
            {
                string originalText = textBox.Text;
                string newText = originalText;

                // Find out which Data Type is currently selected
                string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";

                if (selectedType == "Hex")
                {
                    // HEX: Numbers only, letters A-F and 'x'
                    newText = new string(originalText.Where(c => char.IsDigit(c) || "abcdefABCDEFxX".Contains(c)).ToArray());

                    // LENGTH LIMIT: Maximum 6 characters if starting with "0x" (0xFFFF), 4 characters if not starting with "0x" (FFFF)
                    int maxLen = newText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 6 : 4;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else if (selectedType == "Binary")
                {
                    // BINARY: Only 0 and 1
                    newText = new string(originalText.Where(c => c == '0' || c == '1').ToArray());

                    // LENGTH LIMIT: Maximum 16 characters because it's 16-bit (1111111111111111)
                    if (newText.Length > 16) newText = newText.Substring(0, 16);
                }
                else if (selectedType.Contains("Float") || selectedType.Contains("Double") || selectedType.Contains("Signed"))
                {
                    // FLOAT / SIGNED: Numbers, Minus (-) and Period/Comma (.,)
                    newText = new string(originalText.Where(c => char.IsDigit(c) || c == '-' || c == '.' || c == ',').ToArray());
                }
                else
                {
                    // UNSIGNED: Numbers only
                    newText = new string(originalText.Where(char.IsDigit).ToArray());

                    // LENGTH LIMIT: Unsigned ushort can be a maximum of 65535 (5 characters)
                    if (newText.Length > 5) newText = newText.Substring(0, 5);
                }

                // Update the box if there are forbidden characters in the text entered by the user and they have been deleted.
                if (originalText != newText)
                {
                    textBox.Text = newText;
                    textBox.CaretIndex = newText.Length; // Move the cursor to the end
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


                        string formattedLogValue = ModbusLibrary.Utils.ModbusDataFormatter.FormatValue(new ushort[] { valueToWrite }, 0, selectedType);
                        AddLog($"Success: {formattedLogValue} was written to Register[{address}].");
                    }
                    else
                    {
                        ushort[] registersToWrite = ModbusLibrary.Utils.ModbusDataFormatter.BuildMultiRegisterValue(txtWriteValue.Text ?? "0", selectedType);
                        await _master.WriteMultipleRegistersAsync(slaveId, address, registersToWrite);
                        AddLog($"Success: {txtWriteValue.Text} ({selectedType}) was written to Register[{address}].");
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
            // 1. ESKİ CİHAZIN VERİLERİNİ KAYDET
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

            // 2. YENİ CİHAZA GEÇİŞ VE ARAYÜZ YÜKLEMESİ
            if (lstDevices.SelectedItem is SlaveDevice selectedDevice)
            {
                // ÇOK KRİTİK: Kullanıcının şu an sisteme bağlı olup olmadığını hafızaya alıyoruz
                bool wasSystemActive = _isSystemActive;

                // Eğer önceden bağlıysak, arka plandaki eski fiziki bağlantıyı sessizce temizle
                if (_isConnected)
                {
                    timerPoll.Stop();
                    _transport?.Disconnect();
                    _isConnected = false;
                    // DİKKAT: Butonu "Connect" yapmıyoruz, çünkü birazdan otomatik bağlanacağız!
                }

                // Yeni cihazın değerlerini ekrandaki kutulara doldur
                cmbConnectionType.SelectedIndex = selectedDevice.SelectedConnectionIndex;
                txtIpAddress.Text = selectedDevice.IpAddress;
                txtPort.Text = selectedDevice.Port;
                txtSlaveId.Text = selectedDevice.SlaveId.ToString();
                cmbFunction.SelectedIndex = selectedDevice.SelectedFunctionIndex;
                cmbDataType.SelectedIndex = selectedDevice.SelectedDataTypeIndex;
                txtStartAddress.Text = selectedDevice.StartAddress;
                txtQuantity.Text = selectedDevice.Quantity;
                txtPollInterval.Text = selectedDevice.PollInterval;

                AddLog($"Device changed: {selectedDevice.Name}");

                // 3. OTOMATİK YENİDEN BAĞLANMA (Auto-Reconnect)
                if (wasSystemActive)
                {
                    AddLog($"Auto-connecting to {selectedDevice.Name}...");
                    btnConnect.Content = "Connect"; // Kandırmaca: Sistemin yeni bir köprü kurabilmesi için butonu sıfırlıyoruz
                    BtnConnect_Click(this, new RoutedEventArgs());
                }
            }
        }
        private async void DataGridViewRegisters_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (dataGridViewRegisters.SelectedItem is RegisterRow selectedRow)
            {
                if (_master == null || !_isConnected) return;

                int funcIndex = cmbFunction.SelectedIndex;

                // Prevent windows from opening in Read-Only fields (02, 04)
                if (funcIndex == 1 || funcIndex == 3)
                {
                    AddLog("Warning: The selected function is Read-Only.");
                    return;
                }

                // We call up the pop-up window we just created
                string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";

                var dialog = new EditRegisterWindow(selectedRow.Value, selectedType);

                // Open the window as a "Dialog" (The user cannot click on the backend without closing this window)
                await dialog.ShowDialog(this);

                // If the user clicks the OK button (IsConfirmed = true), proceed with the writing process.
                if (dialog.IsConfirmed)
                {
                    try
                    {
                        byte slaveId = byte.Parse(txtSlaveId.Text ?? "1");
                        ushort address = (ushort)selectedRow.RawAddress;

                        if (funcIndex == 0) // Write Coil
                        {
                            bool valueToWrite = dialog.InputValue.Trim().ToLower() is "1" or "true";
                            await _master.WriteSingleCoilAsync(slaveId, address, valueToWrite);
                            AddLog($"Success: {valueToWrite} written to Coil[{address}].");
                        }
                        else if (funcIndex == 2) // Write Register
                        {
                            //string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";

                            if (selectedType is "Signed" or "Unsigned" or "Hex" or "Binary")
                            {
                                ushort valueToWrite = ModbusLibrary.Utils.ModbusDataFormatter.ParseRegisterValue(dialog.InputValue, selectedType);
                                await _master.WriteSingleRegisterAsync(slaveId, address, valueToWrite);

                                // NEW LOG LOGING: Reformats the entered value according to its current type (e.g., if it's Hex, it becomes 0xAAAA) before printing it to the log.
                                string formattedLogValue = ModbusLibrary.Utils.ModbusDataFormatter.FormatValue(new ushort[] { valueToWrite }, 0, selectedType);
                                AddLog($"Success: {formattedLogValue} written to Register[{address}].");
                            }
                            else // Float, Double etc.
                            {
                                ushort[] registersToWrite = ModbusLibrary.Utils.ModbusDataFormatter.BuildMultiRegisterValue(dialog.InputValue, selectedType);
                                await _master.WriteMultipleRegistersAsync(slaveId, address, registersToWrite);
                                AddLog($"Success: {dialog.InputValue} ({selectedType}) written to Register[{address}].");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"Writing Error: Failed to write data to Address [{selectedRow.RawAddress}].");
                    }
                }
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
        public int RawAddress { get; set; }
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
        public string PollInterval { get; set; } = "1000";
    }


    // Data model for color logging
    public class LogEntry
    {
        public string Message { get; set; } = string.Empty;
        public string Color { get; set; } = "Lime";
    }


}