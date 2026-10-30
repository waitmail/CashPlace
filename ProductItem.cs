using System.ComponentModel;

namespace CashPlace;

/// <summary>
/// Строка чека.
/// SumFull       — сумма БЕЗ скидки (Quantity × Price)           → мелким, зачёркнуто
/// SumAtDiscount — сумма СО СКИДКОЙ (Quantity × PriceAtDiscount) → КРУПНЫМ ШРИФТОМ
/// Обе всегда вычисляются из полей строки, поэтому рассинхрон невозможен.
/// </summary>
public class ProductItem : INotifyPropertyChanged
{
    public int Code { get; set; }

    public string Tovar { get; set; } = string.Empty;

    private decimal _quantity;
    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value) return;
            _quantity = value;
            OnPropertyChanged(nameof(Quantity));
            OnPropertyChanged(nameof(SumFull));
            OnPropertyChanged(nameof(SumAtDiscount));
            OnPropertyChanged(nameof(HasDiscount));
        }
    }

    private decimal _price;
    public decimal Price
    {
        get => _price;
        set
        {
            if (_price == value) return;
            _price = value;
            OnPropertyChanged(nameof(Price));
            OnPropertyChanged(nameof(SumFull));
            OnPropertyChanged(nameof(HasDiscount));
        }
    }

    private decimal _priceAtDiscount;
    public decimal PriceAtDiscount
    {
        get => _priceAtDiscount;
        set
        {
            if (_priceAtDiscount == value) return;
            _priceAtDiscount = value;
            OnPropertyChanged(nameof(PriceAtDiscount));
            OnPropertyChanged(nameof(SumAtDiscount));
            OnPropertyChanged(nameof(HasDiscount));
        }
    }

    public int Action { get; set; }
    public int Gift { get; set; }
    public int Action2 { get; set; }
    public string Mark { get; set; } = "0";
    public bool IsSertificate { get; set; }
    public bool IsFractional { get; set; }
    public bool IsMarked { get; set; }
    public decimal MaxQuantity { get; set; }
    public bool IsAddedToOrderOnCash { get; set; }
    public string PromoDescription { get; set; } = "";

    // ★ Сумма БЕЗ скидки: количество × полная цена (мелким шрифтом, зачёркнуто)
    public decimal SumFull => Math.Round(Quantity * Price, 2);

    // ★ Сумма СО СКИДКОЙ: количество × цена со скидкой (КРУПНЫМ шрифтом, синим).
    //   ВАЖНО: именно PriceAtDiscount, а не Price!
    public decimal SumAtDiscount => Math.Round(Quantity * PriceAtDiscount, 2);

    // ★ Скидка была — показываем зачёркнутую сумму и старую цену
    public bool HasDiscount => SumFull != SumAtDiscount;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
