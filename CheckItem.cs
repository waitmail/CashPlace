using System.ComponentModel;

namespace CashPlace
{
    public class CheckItem : INotifyPropertyChanged
    {
        public decimal ItsDeleted { get; set; }
        public DateTime DateTimeWrite { get; set; }
        public string ClientName { get; set; } = "";
        public decimal Cash { get; set; }
        public decimal Remainder { get; set; }
        public string Comment { get; set; } = "";
        public bool ItsPrint { get; set; }
        public string CheckType { get; set; } = "";
        public string DocumentNumber { get; set; } = "";
        public bool IsSent { get; set; } 
        public bool ItsPrintP { get; set; }
        public bool Extra { get; set; }


        // Вспомогательные свойства для удобного отображения в XAML
        public string FormattedDate => DateTimeWrite.ToString("dd.MM.yyyy HH:mm");
        public string FormattedCash => $"{Cash:C}";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}