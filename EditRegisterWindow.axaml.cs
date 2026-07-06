using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ModbusTestAvalonia
{
    public partial class EditRegisterWindow : Window
    {
        public string InputValue { get; private set; } = string.Empty;
        public bool IsConfirmed { get; private set; } = false;

        // Varsayılan constructor
        public EditRegisterWindow()
        {
            InitializeComponent();
        }

        // Pencere açılırken mevcut değeri içine yazmak için özel constructor
        public EditRegisterWindow(string currentValue)
        {
            InitializeComponent();
            var txt = this.FindControl<TextBox>("txtValue");
            if (txt != null)
            {
                txt.Text = currentValue;
                txt.Focus(); // Ekran açılınca imleç direkt kutuda olsun
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            InputValue = this.FindControl<TextBox>("txtValue")?.Text ?? "0";
            IsConfirmed = true;
            this.Close(); // Pencereyi kapat ve onayı gönder
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close(); // İptal et
        }
    }
}