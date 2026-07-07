using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Collections.ObjectModel;

namespace ModbusTestAvalonia
{
    public partial class TrafficWindow : Window
    {
        public static ObservableCollection<string> TrafficLogs { get; } = new ObservableCollection<string>();
        public static int LogCounter = 0;

        // Variable that holds the pause state (Public static for global access)
        public static bool IsPaused { get; set; } = false;

        public TrafficWindow()
        {
            InitializeComponent();

            var lstTraffic = this.FindControl<ItemsControl>("lstTraffic");
            if (lstTraffic != null) lstTraffic.ItemsSource = TrafficLogs;

            // Automatically scrolls to the bottom when new data arrives.
            TrafficLogs.CollectionChanged += (s, e) =>
            {
                var sv = this.FindControl<ScrollViewer>("scrollViewer");
                sv?.ScrollToEnd();
            };
        }

        // NEWLY ADDED BUTTON EVENT (STOP / RESUME)
        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            IsPaused = !IsPaused; // Reverse the state (change True to False, False to True)

            var btn = sender as Button;
            if (btn != null)
            {
                if (IsPaused)
                {
                    btn.Content = "Resume";
                    btn.Background = Brushes.Green; 
                }
                else
                {
                    btn.Content = "Stop";
                    btn.Background = SolidColorBrush.Parse("#b22222"); 
                }
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TrafficLogs.Clear();
            LogCounter = 0;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }
    }
}