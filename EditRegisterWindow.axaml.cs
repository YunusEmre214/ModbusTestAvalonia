using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ModbusTestAvalonia
{
    public partial class EditRegisterWindow : Window
    {
        public string InputValue { get; private set; } = string.Empty;
        public bool IsConfirmed { get; private set; } = false;

        // Default constructor
        public EditRegisterWindow()
        {
            InitializeComponent();
        }

        // Special constructor to write the current value into the window when it opens
        public EditRegisterWindow(string currentValue)
        {
            InitializeComponent();
            var txt = this.FindControl<TextBox>("txtValue");
            if (txt != null)
            {
                txt.Text = currentValue;
                txt.Focus(); // When the screen turns on, the cursor should be directly on the box.
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            InputValue = this.FindControl<TextBox>("txtValue")?.Text ?? "0";
            IsConfirmed = true;
            this.Close(); // Close the window and send the approval message
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close(); // Cancel
        }
    }
}