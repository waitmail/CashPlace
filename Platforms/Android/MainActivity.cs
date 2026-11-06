using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;

namespace CashPlace;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // ==================================================================
    // USB ФР АТОЛ: системный диалог «Разрешить доступ к USB-устройству?»
    // появляется сам, как только ФР подключают по USB (пока касса
    // открыта), или при запуске кассы с уже подключённым ФР.
    // VID — тот же список, что в модуле печати
    // (AtolFiscalPrinter.KnownFrVendorIds).
    // ==================================================================
    private static readonly int[] FrVendorIds = { 0x2912, 0x2E83, 0x0403 };

    private BroadcastReceiver? _frUsbReceiver;

    protected override void OnResume()
    {
        base.OnResume();

        // Кабель уже воткнут (запуск кассы или возврат в приложение).
        RequestFrUsbPermissionIfNeeded();

        // Ловим момент, когда ФР втыкают, пока касса открыта.
        if (_frUsbReceiver == null)
        {
            _frUsbReceiver = new FrUsbAttachReceiver();
            RegisterReceiver(_frUsbReceiver,
                new IntentFilter(UsbManager.ActionUsbDeviceAttached));
        }
    }

    protected override void OnPause()
    {
        if (_frUsbReceiver != null)
        {
            try { UnregisterReceiver(_frUsbReceiver); } catch { }
            _frUsbReceiver = null;
        }
        base.OnPause();
    }

    private void RequestFrUsbPermissionIfNeeded()
    {
        try
        {
            var usbManager = (UsbManager)GetSystemService(Context.UsbService);
            if (usbManager?.DeviceList == null)
            {
                return;
            }
            foreach (var device in usbManager.DeviceList.Values)
            {
                TryRequestFrPermission(this, device);
            }
        }
        catch { }
    }

    private static void TryRequestFrPermission(Context context, UsbDevice device)
    {
        try
        {
            if (Array.IndexOf(FrVendorIds, device.VendorId) < 0)
            {
                return;   // не ФР АТОЛ — не трогаем
            }
            var usbManager = (UsbManager)context.GetSystemService(Context.UsbService);
            if (usbManager == null || usbManager.HasPermission(device))
            {
                return;   // разрешение уже выдано — диалог не нужен
            }
            var intent = new Intent("com.yourname.cash8mobile.USB_PERMISSION");
            // ОБЯЗАТЕЛЬНО для Android 14+ (та же ловушка, что в СБОРКЕ 4):
            intent.SetPackage(context.PackageName);
            var pendingIntent = PendingIntent.GetBroadcast(
                context, 0, intent, PendingIntentFlags.Mutable);
            usbManager.RequestPermission(device, pendingIntent);
            // На экране появится системный диалог «Разрешить доступ».
        }
        catch { }
    }

    private sealed class FrUsbAttachReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            try
            {
                var device = intent?.GetParcelableExtra(UsbManager.ExtraDevice) as UsbDevice;
                if (context != null && device != null)
                {
                    TryRequestFrPermission(context, device);
                }
            }
            catch { }
        }
    }
}