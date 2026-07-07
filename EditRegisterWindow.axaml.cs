using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Linq;

namespace ModbusTestAvalonia
{
    public partial class EditRegisterWindow : Window
    {
        public string InputValue { get; private set; } = string.Empty;
        public bool IsConfirmed { get; private set; } = false;
        private string _dataType = "Unsigned";

        public EditRegisterWindow()
        {
            InitializeComponent();
        }

        public EditRegisterWindow(string currentValue, string dataType)
        {
            InitializeComponent();
            _dataType = dataType;

            var txt = this.FindControl<TextBox>("txtValue");
            if (txt != null)
            {
                txt.Text = currentValue;
                txt.Focus();
            }
        }

        // NEWLY ADDED DYNAMIC FILTER METHOD
        private void TxtValue_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrEmpty(textBox.Text))
            {
                string originalText = textBox.Text;
                string newText = originalText;

                // Apply filter based on the _dataType variable in memory
                if (_dataType == "Hex")
                {
                    // HEX: Numbers only, letters A-F and 'x'
                    newText = new string(originalText.Where(c => char.IsDigit(c) || "abcdefABCDEFxX".Contains(c)).ToArray());

                    // LENGTH LIMIT: Maximum 6 characters if starting with "0x" (0xFFFF), 4 characters if not starting with "0x" (FFFF)
                    int maxLen = newText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 6 : 4;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else if (_dataType == "Binary")
                {
                    // BINARY: Only 0 and 1
                    newText = new string(originalText.Where(c => c == '0' || c == '1').ToArray());

                    // LENGTH LIMIT: Maximum 16 characters because it's 16-bit (1111111111111111)
                    if (newText.Length > 16) newText = newText.Substring(0, 16);
                }
                else if (_dataType.Contains("Float") || _dataType.Contains("Double"))
                {
                    // FLOAT / DOUBLE: Numbers, Minus (-) and Period/Comma (.,)
                    newText = new string(originalText.Where(c => char.IsDigit(c) || c == '-' || c == '.' || c == ',').ToArray());

                    // 15 character limit for Float, 20 character limit for Double
                    int maxLen = _dataType.Contains("Float") ? 15 : 20;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else if (_dataType.Contains("Long") || _dataType == "Signed")
                {
                    // LONG / SIGNED (Integers): Only digits and the minus (-) sign. Fractions are forbidden!
                    newText = new string(originalText.Where(c => char.IsDigit(c) || c == '-').ToArray());

                    // 6 character limit for Signed (16-bit), 11 character limit for Long (Modbus 32-bit DINT).
                    int maxLen = _dataType.Contains("Long") ? 11 : 6;
                    if (newText.Length > maxLen) newText = newText.Substring(0, maxLen);
                }
                else
                {
                    // UNSIGNED: Numbers only
                    newText = new string(originalText.Where(char.IsDigit).ToArray());

                    // LENGTH LIMIT: Unsigned ushort can be a maximum of 65535 (5 characters)
                    if (newText.Length > 5) newText = newText.Substring(0, 5);
                }

                if (originalText != newText)
                {
                    textBox.Text = newText;
                    textBox.CaretIndex = newText.Length;
                }
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            InputValue = this.FindControl<TextBox>("txtValue")?.Text ?? "0";
            IsConfirmed = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}