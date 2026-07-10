using ZXing;
using ZXing.Net.Maui;

namespace CashPlace;

public partial class ScannerPage : ContentPage
{
    // Делегат, который вернет результат на главную страницу
    private readonly Action<string> _onScanResult;

    public ScannerPage(Action<string> onScanResult)
    {
        InitializeComponent();
        _onScanResult = onScanResult;

        // Настраиваем сканер только на QR-коды
        scannerView.Options = new BarcodeReaderOptions
        {
            Formats = ZXing.Net.Maui.BarcodeFormat.QrCode | ZXing.Net.Maui.BarcodeFormat.Ean13 | ZXing.Net.Maui.BarcodeFormat.Code128 | ZXing.Net.Maui.BarcodeFormat.Ean8 | ZXing.Net.Maui.BarcodeFormat.Code39,
            AutoRotate = true,
            Multiple = false
        };
    }

    private void OnBarcodesDetected(object sender, BarcodeDetectionEventArgs e)
    {
        var result = e.Results.FirstOrDefault();
        if (result != null)
        {
            // Отключаем сканер, чтобы не сработал несколько раз
            scannerView.IsDetecting = false;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                _onScanResult?.Invoke(result.Value);
                Navigation.PopModalAsync();
            });
        }
    }

    private void OnCloseClicked(object sender, EventArgs e)
    {
        Navigation.PopModalAsync();
    }
}