using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ModbusLibrary.Master;
using ModbusLibrary.Transport;
using System;
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

        public ObservableCollection<SlaveDevice> DeviceList { get; set; } = new ObservableCollection<SlaveDevice>();
        public ObservableCollection<LogEntry> LogData { get; set; } = new ObservableCollection<LogEntry>();

        // Fonksiyon/veri tipi ComboBox index -> string eşlemeleri (arka planda ekran olmadan kullanmak için)
        private static readonly string[] FunctionNames = { "Coil", "Input", "Register", "InpReg" };
        
        private static readonly string[] DataTypeNames = { "Signed", "Unsigned", "Hex", "Binary", "Float", "Float inverse", "Double", "Double Inverse", "Long", "Long Inverse" };
        public MainWindow()
        {
            InitializeComponent();
            txtPollInterval = this.FindControl<TextBox>("txtPollInterval")!;

            dataGridViewRegisters.ItemsSource = null; // aşağıda aktif cihaz seçilince set edilecek
            lstDevices.ItemsSource = DeviceList;
            lstLogs.ItemsSource = LogData;

            cmbDataType.SelectionChanged += CmbDataType_SelectionChanged;
            cmbFunction.SelectionChanged += CmbFunction_SelectionChanged;
        }
        private void CmbDataType_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_activeDevice != null)
            {
                // Ekrandaki yeni seçimi, aktif cihazın hafızasına yazCmbConnectionType_SelectionChanged
                _activeDevice.SelectedDataTypeIndex = cmbDataType.SelectedIndex;
            }
        }

        private void CmbFunction_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_activeDevice != null)
            {
                // Ekrandaki yeni seçimi, aktif cihazın hafızasına yaz
                _activeDevice.SelectedFunctionIndex = cmbFunction.SelectedIndex;
            }
        }

        // --- BAĞLAN / KES ---
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDevice == null) { AddLog("Warning: No device selected."); return; }

            if (btnConnect.Content?.ToString() == "Disconnect")
            {
                StopDevicePolling(_activeDevice);
                _activeDevice.Transport?.Disconnect();
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

            // Ekrandaki güncel ayarları cihaza yaz (bağlanmadan önce senkronla)
            SyncScreenToDevice(_activeDevice);
            
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

                ITransport transport = selectedConnection switch
                {
                    "Modbus TCP/IP" => new TcpTransport(),
                    "Modbus UDP/IP" => new UdpTransport(),
                    "Modbus RTU Over TCP/IP" => new RtuOverTcpTransport(),
                    "Modbus RTU Over UDP/IP" => new RtuOverUdpTransport(),
                    "Serial Port (RTU)" => new SerialTransport(selectedDataBits, selectedParity, selectedStopBits),
                    _ => new TcpTransport()
                };

                await transport.ConnectAsync(ipOrCom, portOrBaud);
                
                var device = _activeDevice;
                device.Transport = transport;
                device.Master = new ModbusMaster(transport);
                device.IsConnected = true;

                btnConnect.Content = "Disconnect";
                AddLog($"{device.Name}: Connected via {selectedConnection}");

                StartDevicePolling(device);
            }
            catch (Exception ex)
            {
                _activeDevice.IsConnected = false;
                btnConnect.Content = "Connect";
                AddLog($"{_activeDevice.Name}: Connection Error - device offline or unreachable.");
            }
        }
        

        // --- HER CİHAZ İÇİN ARKA PLAN POLL DÖNGÜSÜ ---
        private void StartDevicePolling(SlaveDevice device)
        {
            StopDevicePolling(device); // varsa eskisini iptal et

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
                    await PollDeviceOnce(device);
                }
                catch (Exception)
                {
                    if (device.IsConnected)
                    {
                        AddLog($"{device.Name}: Reading Error - Connection lost.");
                        device.Transport?.Disconnect();
                        device.IsConnected = false;

                        if (_activeDevice == device)
                        {
                            Dispatcher.UIThread.Post(() => btnConnect.Content = "Connect");
                        }
                    }
                    break; // bu cihazın döngüsünü sonlandır
                }

                try { await Task.Delay(intervalMs, token); }
                catch (TaskCanceledException) { break; }
            }
        }

        // Tek bir okuma turu — sadece device'ın kendi ayarlarına bakar, ekrandaki textbox'lara BAKMAZ
        private async Task PollDeviceOnce(SlaveDevice device)
        {
            if (device.Master == null || !device.IsConnected) return;

            if (!byte.TryParse(device.SlaveId.ToString(), out byte slaveId)) return;
            if (!ushort.TryParse(device.StartAddress, out ushort startAddress)) return;
            if (!ushort.TryParse(device.Quantity, out ushort quantity) || quantity == 0) return;

            var master = device.Master;
            int funcIndex = device.SelectedFunctionIndex;

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
                        ? await master.ReadCoilsAsync(slaveId, currentStart, readCount)
                        : await master.ReadDiscreteInputsAsync(slaveId, currentStart, readCount);

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
                        ? await master.ReadHoldingRegistersAsync(slaveId, currentStart, readCount)
                        : await master.ReadInputRegistersAsync(slaveId, currentStart, readCount);

                    Array.Copy(chunk, 0, allValues, destIndex, chunk.Length);
                    remaining -= readCount; currentStart += readCount; destIndex += readCount;
                }

                int step = ModbusLibrary.Utils.ModbusDataFormatter.GetStepForDataType(selectedType);
                int expectedRows = step > 0 ? quantity / step : quantity;

                UpdateDeviceGrid(device, startAddress, expectedRows, step, funcIndex, null, allValues, selectedType);
            }
        }

        // Cihazın kendi RegisterData'sını günceller — UI thread'e Dispatcher ile geçiyoruz
        private void UpdateDeviceGrid(SlaveDevice device, int startAddress, int rowCount, int step, int funcIndex, bool[]? bitValues, ushort[]? regValues, string selectedType)
        {
            string prefix = FunctionNames.ElementAtOrDefault(funcIndex) ?? "Address";

            Dispatcher.UIThread.Post(() =>
            {
                if (device.RegisterData.Count != rowCount)
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

        // --- TEXTBOX FİLTRELERİ (değişmedi) ---
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
                    // HEX: Sadece rakam, A-F harfleri ve x/X
                    newText = new string(originalText.Where(c => char.IsDigit(c) || "abcdefABCDEFxX".Contains(c)).ToArray());
                    int maxLen = newText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 6 : 4;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else if (selectedType == "Binary")
                {
                    // BINARY: Sadece 0 ve 1
                    newText = new string(originalText.Where(c => c == '0' || c == '1').ToArray());
                    if (newText.Length > 16) newText = newText.Substring(0, 16);
                }
                else if (selectedType.Contains("Float") || selectedType.Contains("Double"))
                {
                    // FLOAT / DOUBLE: Rakamlar, Eksi (-), Nokta (.) ve Virgül (,)
                    newText = new string(originalText.Where(c => char.IsDigit(c) || c == '-' || c == '.' || c == ',').ToArray());
                    
                    // Float için 15, Double için 20 karakter sınırı (eksi ve ondalık ayraç dahil)
                    int maxLen = selectedType.Contains("Float") ? 15 : 20;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else if (selectedType.Contains("Long") || selectedType == "Signed")
                {
                    // LONG / SIGNED (Tam Sayılar): Sadece Rakamlar ve Eksi (-) işareti. KÜSURAT YASAK!
                    newText = new string(originalText.Where(c => char.IsDigit(c) || c == '-').ToArray());

                    // Signed (16-bit) için 6 karakter, Long (64-bit) için 20 karakter sınırı
                    int maxLen = selectedType.Contains("Long") ? 11 : 6;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else
                {
                    // UNSIGNED (Varsayılan): Sadece Rakam. Eksi ve virgül yasak.
                    newText = new string(originalText.Where(char.IsDigit).ToArray());
                    
                    // Unsigned 16-bit maks 65535 olabilir (5 karakter)
                    if (newText.Length > 5) newText = newText.Substring(0, 5);
                }

                // Eğer kullanıcının girdiği hatalı/fazla karakterler silindiyse TextBox'ı güncelle
                if (originalText != newText)
                {
                    textBox.Text = newText;
                    textBox.CaretIndex = newText.Length; // İmleci en sona at
                }
            }
        }

        // --- YAZMA (aktif cihaz üzerinden, senkron - polling'i etkilemez çünkü artık ayrı Task) ---
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

            try
            {
                byte slaveId = byte.Parse(txtSlaveId.Text ?? "1");

                if (funcIndex == 0)
                {
                    string valueText = txtWriteValue.Text?.Trim().ToLower() ?? "0";
                    bool valueToWrite = valueText == "1" || valueText == "true";
                    await master.WriteSingleCoilAsync(slaveId, address, valueToWrite);
                    AddLog($"Success: {valueToWrite} was written to Coil[{address}].");
                }
                else if (funcIndex == 2)
                {
                    string selectedType = (cmbDataType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unsigned";
                    ushort[] registersToWrite;

                    // --- ADIM 1: VERİ DÖNÜŞTÜRME (LOKAL İŞLEM) ---
                    // Eğer burada hata çıkarsa ağ bağlantısını koparmayacağız, sadece log atıp iptal edeceğiz.
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
                        return; // Sistemi durdurmadan işlemi iptal et!
                    }

                    // --- ADIM 2: AĞ ÜZERİNDEN YAZMA (HABERLEŞME) ---
                    if (registersToWrite.Length == 1)
                    {
                        await master.WriteSingleRegisterAsync(slaveId, address, registersToWrite[0]);
                        string formattedLogValue = ModbusLibrary.Utils.ModbusDataFormatter.FormatValue(registersToWrite, 0, selectedType);
                        AddLog($"Success: {formattedLogValue} was written to Register[{address}].");
                    }
                    else
                    {
                        await master.WriteMultipleRegistersAsync(slaveId, address, registersToWrite);
                        AddLog($"Success: {txtWriteValue.Text} ({selectedType}) was written to Register[{address}].");
                    }
                }
            }
            catch (Exception ex)
            {
                // BURAYA SADECE GERÇEK AĞ HATALARINDA DÜŞER
                AddLog($"Communication Error: {ex.Message}");
                if (device.IsConnected)
                {
                    StopDevicePolling(device);
                    device.Transport?.Disconnect();
                    device.IsConnected = false;
                    if (_activeDevice == device) btnConnect.Content = "Connect";
                }
            }
        }

        // --- CİHAZ YÖNETİMİ ---
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
                    dev.Transport?.Disconnect();
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

        // Ekrandaki (textbox/combobox) değerleri, cihazın kendi ayarlarına yazar 
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
            // Eski cihazın form alanlarını kaydet (bağlantı/polling'e DOKUNMUYORUZ, arka planda devam etsin)
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is SlaveDevice oldDevice)
            {
                SyncScreenToDevice(oldDevice);

                // Eğer poll interval değiştiyse ve cihaz bağlıysa, çalışan görevi yeni interval ile yeniden başlat
                if (oldDevice.IsConnected)
                {
                    StartDevicePolling(oldDevice); // interval güncellemesi için restart (opsiyonel ama güvenli)
                }
            }

            if (lstDevices.SelectedItem is SlaveDevice selectedDevice)
            {
                _activeDevice = selectedDevice;

                cmbConnectionType.SelectedIndex = selectedDevice.SelectedConnectionIndex;
                if (selectedDevice.SelectedConnectionIndex == 1) // Serial Port (RTU)
                {
                    // CmbConnectionType_SelectionChanged zaten tetiklendi ve listeyi doldurdu
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

                // Grid'i bu cihazın KENDİ verisine bağla — ayrı Clear/doldurma yok, veri zaten arka planda birikiyor
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
                    byte slaveId;
                    try { slaveId = byte.Parse(txtSlaveId.Text ?? "1"); } catch { slaveId = 1; }
                    ushort address = (ushort)selectedRow.RawAddress;

                    if (funcIndex == 0) // Write Coil
                    {
                        try
                        {
                            bool valueToWrite = dialog.InputValue.Trim().ToLower() is "1" or "true";
                            await master.WriteSingleCoilAsync(slaveId, address, valueToWrite);
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

                        // --- ADIM 1: VERİ DÖNÜŞTÜRME (LOKAL İŞLEM) ---
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
                            // Dönüştürme hatası: Bağlantıyı KESME, sadece log at
                            AddLog($"Format Error: Failed to parse '{selectedType}'. Library Error: {parseEx.Message}");
                            return;
                        }

                        // --- ADIM 2: AĞ ÜZERİNDEN YAZMA ---
                        try
                        {
                            if (registersToWrite.Length == 1)
                            {
                                await master.WriteSingleRegisterAsync(slaveId, address, registersToWrite[0]);
                                string formattedLogValue = ModbusLibrary.Utils.ModbusDataFormatter.FormatValue(registersToWrite, 0, selectedType);
                                AddLog($"Success: {formattedLogValue} written to Register[{address}].");
                            }
                            else
                            {
                                await master.WriteMultipleRegistersAsync(slaveId, address, registersToWrite);
                                AddLog($"Success: {dialog.InputValue} ({selectedType}) written to Register[{address}].");
                            }
                        }
                        catch (Exception commEx)
                        {
                            HandleCommError(device, address, commEx);
                        }
                    }
                }
            }
        }

        // Kodu temiz tutmak için ağ hatası yakalama işlemini küçük bir yardımcı metoda aldık
        private void HandleCommError(SlaveDevice device, ushort address, Exception ex)
        {
            AddLog($"Communication Error while writing to Address [{address}]: {ex.Message}");
            if (device.IsConnected)
            {
                StopDevicePolling(device);
                device.Transport?.Disconnect();
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
                _activeDevice.Transport?.Disconnect();
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

                pnlSerialSettings.IsVisible = true;   // <-- satırın tamamını göster
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

                pnlSerialSettings.IsVisible = false;  // <-- satırın tamamını gizle
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
        [JsonIgnore] public CancellationTokenSource? PollCts { get; set; }

        // Her cihazın kendi register grid verisi — grid buna bağlanır
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