# Modbus Master Test Tool (Avalonia UI)

This project is a modern, cross-platform Modbus Master utility developed with C# and **Avalonia UI**. It is designed for industrial automation testing, SCADA system integration, and IoT environments. Unlike traditional Windows-bound tools, this application runs natively on **Windows, Linux (including Raspberry Pi), and macOS**.

## Features
* **Cross-Platform Compatibility:** Built with Avalonia UI, ensuring seamless operation across different operating systems.
* **Comprehensive Protocol Support:** Connect to field devices via:
  * Modbus TCP/IP
  * Modbus UDP/IP
  * Modbus RTU Over TCP/UDP
  * Serial Port (Modbus RTU)
* **Full Data Access:** Complete Read and Write capabilities for standard Modbus function codes:
  * 01: Read/Write Coils
  * 02: Read Discrete Inputs
  * 03: Read/Write Holding Registers
  * 04: Read Input Registers
* **Advanced Data Parsing:** Automatically decodes raw register data into multiple formats (Signed, Unsigned, Hex, Binary, Float, Double, Long).
* **Device Management:** Built-in device tree for tracking and managing multiple slave devices (nodes) simultaneously.
* **High-Performance UI:** Utilizes `ObservableCollection` and MVVM-friendly data binding for real-time, lag-free data grid updates.
* **Real-Time Diagnostics:** Custom color-coded logging system for monitoring connection status, successful data transactions, and network errors.

## Tech Stack
* C# / .NET
* Avalonia UI (Declarative XAML & Cross-Platform UI Framework)
* Object-Oriented Architecture (Decoupled UI and Modbus Transport layers)

## Usage
1. Select the desired connection protocol (e.g., Modbus TCP/IP or Serial Port) from the dropdown.
2. Enter the target IP Address/Port or COM Port/Baud Rate.
3. Configure the Polling Interval (in milliseconds) and click **Connect**.
4. Manage multiple devices using the right-side panel.
5. Use the main interface to select the Function Code, Data Type, and Register range to monitor or modify values in real time.

## Build and Run (Linux / Raspberry Pi Example)
To run this application on a Linux environment (e.g., Raspberry Pi):
```bash
# Navigate to the release folder
cd /path/to/ModbusTestAvalonia/bin/Release/net10.0/linux-arm64/

# Make the executable runable
chmod +x ModbusTestAvalonia

# Run the application
./ModbusTestAvalonia
