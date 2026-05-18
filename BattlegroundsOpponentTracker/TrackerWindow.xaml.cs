using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;

namespace BattlegroundsTracker
{
    public partial class TrackerWindow : UserControl
    {
        public TrackerWindow()
        {
            InitializeComponent();
        }

        public void UpdateRound(int round)
        {
            RoundText.Text = $"Runda {round}";
        }

        public void UpdateOpponents(Dictionary<string, int> meetCount)
        {
            OpponentList.Items.Clear();
            foreach (var kvp in meetCount)
            {
                var text = new TextBlock
                {
                    Text = $"{kvp.Key} – mött {kvp.Value}x",
                    Foreground = kvp.Value >= 2
                        ? Brushes.Orange
                        : Brushes.White,
                    FontSize = 13,
                    Margin = new System.Windows.Thickness(0, 3, 0, 3)
                };
                OpponentList.Items.Add(text);
            }
        }
    }
}