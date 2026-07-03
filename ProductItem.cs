using System.ComponentModel;

namespace CashPlace;

public class ProductItem : INotifyPropertyChanged
{
    public int Code { get; set; }
    public string Tovar { get; set; } = "";
    
    private decimal _quantity;
    public decimal Quantity 
    { 
        get => _quantity; 
        set { _quantity = value; OnPropertyChanged(nameof(Quantity)); OnPropertyChanged(nameof(SumAtDiscount)); } 
    }

    private decimal _priceAtDiscount;
    public decimal PriceAtDiscount 
    { 
        get => _priceAtDiscount; 
        set { _priceAtDiscount = value; OnPropertyChanged(nameof(PriceAtDiscount)); OnPropertyChanged(nameof(SumAtDiscount)); } 
    }

    public decimal SumAtDiscount => Quantity * PriceAtDiscount;
    
    public string Mark { get; set; } = "0";
    public bool IsMarked { get; set; }
    public bool IsFractional { get; set; }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}